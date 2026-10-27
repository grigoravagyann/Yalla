using System.Net.Http.Json;
using Yalla.Domain.Enums;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>
    /// AppBookings and WebBookings are decided by the route, never by the <c>channel</c> a request names. A diner's
    /// session - the app and the public page book through the same route - needs both: with either blocked, no channel
    /// books or moves there, and the listing says so. The manage link is the page's own and needs web bookings only.
    /// Existing bookings still cancel either way.
    /// </summary>
    [SkippableFact]
    public async Task Blocked_booking_channels_refuse_new_bookings_and_moves_whatever_channel_is_named()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);

        using var appGuest = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var webGuest = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var newcomer = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var anonymous = factory.CreateClient();

        // Made while both were allowed: the in-flight bookings.
        var fromApp = await CreatedAsync(appGuest, AppBooking(factory, branch, daysAhead: 2, tableIndex: 0));
        var fromPage = await CreatedAsync(webGuest, ReviewTestData.Booking(factory, branch, branch.TableIds[1], ReservationChannel.Web));
        var token = Uri.EscapeDataString(fromPage.GetProperty("manageToken").GetString()!);
        var appMove = $"/api/reservations/{fromApp.GetProperty("id").GetGuid()}/reschedule";

        object Move(int table, string time) => new
        {
            date = LocalDate(factory, 2),
            time,
            partySize = 2,
            diningTableId = branch.TableIds[table],
            clientCommandId = Guid.CreateVersion7(),
        };

        // App bookings blocked: a diner's session books nothing, whichever channel it names - and moves nothing.
        await BlockAsync(factory, branch.BranchId, BranchFeature.AppBookings);

        foreach (var channel in new[] { ReservationChannel.App, ReservationChannel.Web, ReservationChannel.Unknown })
        {
            await AssertRefusedAsync(
                await newcomer.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[2], channel)),
                BranchFeature.AppBookings,
                branch.BranchId);
        }

        await AssertRefusedAsync(await appGuest.PostAsJsonAsync(appMove, Move(0, "20:00")), BranchFeature.AppBookings, branch.BranchId);

        var listing = await ReadAsync(anonymous, $"/api/public/branches/{branch.BranchId}");
        Assert.False(listing.GetProperty("acceptsAppBookings").GetBoolean());
        Assert.False(listing.GetProperty("acceptsWebBookings").GetBoolean());

        // The manage link is the page's own route: it still moves the page's booking.
        Assert.True((await ReadAsync(anonymous, $"/api/public/bookings/{token}")).GetProperty("canChange").GetBoolean());
        await OkAsync(await anonymous.PostAsJsonAsync($"/api/public/bookings/{token}/reschedule", Move(1, "20:00")));

        // Web bookings blocked instead: the session is refused naming them, and so is the link.
        await BlockAsync(factory, branch.BranchId, BranchFeature.WebBookings);

        await AssertRefusedAsync(
            await newcomer.PostAsJsonAsync("/api/reservations", AppBooking(factory, branch, daysAhead: 3, tableIndex: 2)),
            BranchFeature.WebBookings,
            branch.BranchId);
        await AssertRefusedAsync(await appGuest.PostAsJsonAsync(appMove, Move(0, "21:00")), BranchFeature.WebBookings, branch.BranchId);

        var page = await ReadAsync(anonymous, $"/api/public/bookings/{token}");
        Assert.False(page.GetProperty("canChange").GetBoolean());
        Assert.True(page.GetProperty("canCancel").GetBoolean());

        await AssertRefusedAsync(
            await anonymous.PostAsJsonAsync($"/api/public/bookings/{token}/reschedule", Move(1, "21:00")),
            BranchFeature.WebBookings,
            branch.BranchId);

        // Both bookings made before still cancel - from the app and from the link.
        await OkAsync(await appGuest.PostAsJsonAsync($"/api/reservations/{fromApp.GetProperty("id").GetGuid()}/cancel", new { }));

        var cancelled = await OkAsync(await anonymous.PostAsJsonAsync($"/api/public/bookings/{token}/cancel", new { }));
        Assert.Equal((int)ReservationStatus.CancelledByDiner, cancelled.GetProperty("status").GetInt32());
    }
}
