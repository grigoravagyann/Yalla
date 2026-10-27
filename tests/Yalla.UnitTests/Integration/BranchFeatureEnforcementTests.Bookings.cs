using System.Net;
using System.Net.Http.Json;
using Yalla.Domain.Enums;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>
    /// AppBookings and WebBookings: a booking or a move over a blocked channel is refused - from the app, from the page,
    /// and from the page's manage link, whose read stops offering "Change" - while a booking made before can still be
    /// cancelled from either. A client that leaves the channel out books only while a diner channel is allowed.
    /// </summary>
    [SkippableFact]
    public async Task Blocked_booking_channels_refuse_new_bookings_and_moves_and_existing_bookings_still_cancel()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);

        using var appGuest = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var webGuest = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var newcomer = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var anonymous = factory.CreateClient();

        // Made while both channels were allowed: the in-flight bookings.
        var fromApp = await CreatedAsync(appGuest, AppBooking(factory, branch, daysAhead: 2, tableIndex: 0));
        var fromPage = await CreatedAsync(webGuest, ReviewTestData.Booking(factory, branch, branch.TableIds[1], ReservationChannel.Web));
        var token = fromPage.GetProperty("manageToken").GetString()!;

        await BlockAsync(factory, branch.BranchId, BranchFeature.AppBookings);

        // A new app booking, and a move of the existing one, are refused; the page still books.
        await AssertRefusedAsync(
            await newcomer.PostAsJsonAsync("/api/reservations", AppBooking(factory, branch, daysAhead: 3, tableIndex: 2)),
            BranchFeature.AppBookings,
            branch.BranchId);

        await AssertRefusedAsync(
            await appGuest.PostAsJsonAsync(
                $"/api/reservations/{fromApp.GetProperty("id").GetGuid()}/reschedule",
                new
                {
                    date = LocalDate(factory, 2),
                    time = "20:00",
                    partySize = 2,
                    diningTableId = branch.TableIds[0],
                    clientCommandId = Guid.CreateVersion7(),
                }),
            BranchFeature.AppBookings,
            branch.BranchId);

        // Leaving the channel out books while one diner channel is allowed.
        await CreatedAsync(newcomer, ReviewTestData.Booking(factory, branch, branch.TableIds[2], ReservationChannel.Unknown));

        // The app's own booking still cancels.
        await OkAsync(await appGuest.PostAsJsonAsync($"/api/reservations/{fromApp.GetProperty("id").GetGuid()}/cancel", new { }));

        // Both blocked: the page and the unnamed channel are refused too, each naming what it needed.
        await BlockAsync(factory, branch.BranchId, BranchFeature.AppBookings, BranchFeature.WebBookings);

        using var last = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        await AssertRefusedAsync(
            await last.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.Web)),
            BranchFeature.WebBookings,
            branch.BranchId);

        await AssertRefusedAsync(
            await last.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.Unknown)),
            BranchFeature.AppBookings,
            branch.BranchId);

        // The manage link: no "Change" offered, the move refused, the cancel taken.
        var page = await ReadAsync(anonymous, $"/api/public/bookings/{Uri.EscapeDataString(token)}");
        Assert.False(page.GetProperty("canChange").GetBoolean());
        Assert.True(page.GetProperty("canCancel").GetBoolean());

        await AssertRefusedAsync(
            await anonymous.PostAsJsonAsync(
                $"/api/public/bookings/{Uri.EscapeDataString(token)}/reschedule",
                new
                {
                    date = LocalDate(factory, 2),
                    time = "20:00",
                    partySize = 2,
                    diningTableId = branch.TableIds[1],
                    clientCommandId = Guid.CreateVersion7(),
                }),
            BranchFeature.WebBookings,
            branch.BranchId);

        var cancelled = await OkAsync(await anonymous.PostAsJsonAsync($"/api/public/bookings/{Uri.EscapeDataString(token)}/cancel", new { }));
        Assert.Equal((int)ReservationStatus.CancelledByDiner, cancelled.GetProperty("status").GetInt32());
    }
}
