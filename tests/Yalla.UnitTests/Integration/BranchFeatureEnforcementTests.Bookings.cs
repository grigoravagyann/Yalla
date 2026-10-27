using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Domain.Enums;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>
    /// AppBookings and WebBookings are independent, and decided by the route, never by the <c>channel</c> a body names:
    /// <c>POST /api/reservations</c> is the app's, <c>POST /api/public/branches/{id}/reservations</c> the page's, the
    /// manage link the page's; a move from a diner's session goes by the channel the booking was made on. Blocking one
    /// leaves the other booking and moving; bookings made before still cancel.
    /// </summary>
    [SkippableFact]
    public async Task Each_booking_channel_is_blocked_on_its_own_routes_whatever_channel_a_body_names()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory, tableCount: 4);

        using var appGuest = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var webGuest = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var anonymous = factory.CreateClient();

        // Made while both were allowed: one from the app, one from the page.
        var fromApp = await CreatedAsync(appGuest, AppBooking(factory, branch, daysAhead: 2, tableIndex: 0));
        var fromPage = await PageBookedAsync(webGuest, factory, branch, table: 1);
        Assert.Equal(ReservationChannel.Web, await StoredChannelAsync(factory, fromPage.GetProperty("id").GetGuid()));

        var appMove = $"/api/reservations/{fromApp.GetProperty("id").GetGuid()}/reschedule";
        var pageMoveFromSession = $"/api/reservations/{fromPage.GetProperty("id").GetGuid()}/reschedule";
        var appLink = Uri.EscapeDataString(fromApp.GetProperty("manageToken").GetString()!);
        var pageLink = Uri.EscapeDataString(fromPage.GetProperty("manageToken").GetString()!);

        object Move(int table, string time) => new
        {
            date = LocalDate(factory, 2),
            time,
            partySize = 2,
            diningTableId = branch.TableIds[table],
            clientCommandId = Guid.CreateVersion7(),
        };

        // ---- Web bookings blocked: the page's routes refused, the app's untouched.
        await BlockAsync(factory, branch.BranchId, BranchFeature.WebBookings);

        using (var newcomer = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token))
        {
            await AssertRefusedAsync(
                await newcomer.PostAsJsonAsync($"/api/public/branches/{branch.BranchId}/reservations", PageBooking(factory, branch, table: 2)),
                BranchFeature.WebBookings,
                branch.BranchId);

            // Naming Web on the app's route changes nothing: it books, as the app.
            var named = await CreatedAsync(
                newcomer, ReviewTestData.Booking(factory, branch, branch.TableIds[2], ReservationChannel.Web));
            Assert.Equal(ReservationChannel.App, await StoredChannelAsync(factory, named.GetProperty("id").GetGuid()));
        }

        var listing = await ReadAsync(anonymous, $"/api/public/branches/{branch.BranchId}");
        Assert.True(listing.GetProperty("acceptsAppBookings").GetBoolean());
        Assert.False(listing.GetProperty("acceptsWebBookings").GetBoolean());

        await OkAsync(await appGuest.PostAsJsonAsync(appMove, Move(0, "20:00")));

        // The page's booking does not move - not by its link, nor from the diner's session.
        Assert.False((await ReadAsync(anonymous, $"/api/public/bookings/{pageLink}")).GetProperty("canChange").GetBoolean());
        await AssertRefusedAsync(
            await anonymous.PostAsJsonAsync($"/api/public/bookings/{pageLink}/reschedule", Move(1, "20:00")),
            BranchFeature.WebBookings,
            branch.BranchId);
        await AssertRefusedAsync(
            await webGuest.PostAsJsonAsync(pageMoveFromSession, Move(1, "20:00")), BranchFeature.WebBookings, branch.BranchId);

        // ---- App bookings blocked instead: the app's routes refused, the page's untouched.
        await BlockAsync(factory, branch.BranchId, BranchFeature.AppBookings);

        using (var newcomer = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token))
        {
            foreach (var channel in new[] { ReservationChannel.App, ReservationChannel.Web, ReservationChannel.Unknown })
            {
                await AssertRefusedAsync(
                    await newcomer.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[3], channel)),
                    BranchFeature.AppBookings,
                    branch.BranchId);
            }

            await PageBookedAsync(newcomer, factory, branch, table: 3);
        }

        listing = await ReadAsync(anonymous, $"/api/public/branches/{branch.BranchId}");
        Assert.False(listing.GetProperty("acceptsAppBookings").GetBoolean());
        Assert.True(listing.GetProperty("acceptsWebBookings").GetBoolean());

        await AssertRefusedAsync(await appGuest.PostAsJsonAsync(appMove, Move(0, "21:00")), BranchFeature.AppBookings, branch.BranchId);

        // The page's booking moves from the diner's session too - it was made on the web.
        await OkAsync(await webGuest.PostAsJsonAsync(pageMoveFromSession, Move(1, "20:30")));

        // Any booking's manage link is the page: it moves the app's booking as well.
        Assert.True((await ReadAsync(anonymous, $"/api/public/bookings/{appLink}")).GetProperty("canChange").GetBoolean());
        await OkAsync(await anonymous.PostAsJsonAsync($"/api/public/bookings/{appLink}/reschedule", Move(0, "21:30")));

        // ---- Both blocked: bookings made before still cancel, from the app and from the link.
        await BlockAsync(factory, branch.BranchId, BranchFeature.AppBookings, BranchFeature.WebBookings);

        await OkAsync(await appGuest.PostAsJsonAsync($"/api/reservations/{fromApp.GetProperty("id").GetGuid()}/cancel", new { }));

        var cancelled = await OkAsync(await anonymous.PostAsJsonAsync($"/api/public/bookings/{pageLink}/cancel", new { }));
        Assert.Equal((int)ReservationStatus.CancelledByDiner, cancelled.GetProperty("status").GetInt32());
    }

    private async Task<ReservationChannel> StoredChannelAsync(YallaApiFactory factory, Guid reservationId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return (await db.Reservations.FindAsync(reservationId))!.Channel;
    }

    /// <summary>A body for the public page's booking route: two days out at 18:00, party of two, no branch, no channel.</summary>
    private static object PageBooking(YallaApiFactory factory, AuthBranch branch, int table) => new
    {
        tableId = branch.TableIds[table],
        date = LocalDate(factory, 2),
        time = "18:00",
        partySize = 2,
        guestName = "Ani Page",
        guestPhone = "+37499000998",
        clientCommandId = Guid.CreateVersion7(),
    };

    private static async Task<JsonElement> PageBookedAsync(HttpClient diner, YallaApiFactory factory, AuthBranch branch, int table)
    {
        var response = await diner.PostAsJsonAsync($"/api/public/branches/{branch.BranchId}/reservations", PageBooking(factory, branch, table));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
