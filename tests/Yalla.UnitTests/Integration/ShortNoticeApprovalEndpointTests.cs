using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Short-notice approval through the real pipeline: who the floor's list and the decisions admit, the
/// JSON the tablet reads, and the 409 a late answer gets.
/// </summary>
/// <remarks>
/// <para>
/// The policies are what these are about - <c>WaiterOrAbove</c> on approve, reject and the list, and
/// <c>BranchScoped</c> on the list - so they go through the host, where a policy that was never
/// attached shows up as somebody getting in. The rules themselves are <see cref="ShortNoticeApprovalTests"/>.
/// </para>
/// <para>
/// Tokens are stamped with the API's clock and checked against the real one, so every token is issued
/// first and the API's clock is moved on - to noon, two days from now, at the branch - only then.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ShortNoticeApprovalEndpointTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task The_floors_list_is_for_waiters_and_above_at_the_branch_and_reads_as_documented()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);

        AuthBranch branch;
        AuthBranch otherVenue;
        Guid cookId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            otherVenue = await AuthTestData.CreateBranchAsync(db);
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);
        }

        using var anonymous = factory.CreateClient();
        using var diner = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);
        using var earlier = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var cook = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cookId));
        using var outsider = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, otherVenue));

        var (noonUtc, date) = MoveToNoonInTwoDays(factory);

        // Somebody at table 1 now, seated by the branch's waiter.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await fixture.CreateService(db, factory.Clock, TestActor.Waiter(branch.WaiterId))
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, branch.FirstTableId, 2, Guid.CreateVersion7()));
        }

        // Table 1's evening: a 17:00 the waiter has confirmed, then the request at 19:00.
        var before = await BookAsync(earlier, branch, date, "17:00", "Anna", "+37499111222");
        Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync($"/api/reservations/{before}/approve", new { })).StatusCode);

        var request = await BookAsync(diner, branch, date, "19:00", "Gor", "+37499333444");

        var route = $"/api/branches/{branch.BranchId}/reservations/awaiting";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await diner.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cook.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(route)).StatusCode);

        var listed = await waiter.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var item = Assert.Single((await listed.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());

        Assert.Equal((int)ApprovalTrigger.ShortNotice, item.GetProperty("trigger").GetInt32());
        Assert.Equal(noonUtc.AddMinutes(15), item.GetProperty("approvalExpiresAtUtc").GetDateTime());

        var reservation = item.GetProperty("reservation");
        Assert.Equal(request, reservation.GetProperty("id").GetGuid());
        Assert.Equal("Gor", reservation.GetProperty("guestName").GetString());
        Assert.Equal("+37499333444", reservation.GetProperty("guestPhone").GetString());
        Assert.Equal((int)ApprovalTrigger.ShortNotice, reservation.GetProperty("awaitingApprovalBecause").GetInt32());
        Assert.Equal(noonUtc.AddMinutes(15), reservation.GetProperty("approvalExpiresAtUtc").GetDateTime());

        // Nulls are left off the wire, as everywhere in this API.
        Assert.False(reservation.TryGetProperty("declinedBecause", out _));

        var previous = item.GetProperty("previousBooking");
        Assert.Equal(before, previous.GetProperty("reservationId").GetGuid());
        Assert.Equal("17:00:00", previous.GetProperty("localStartTime").GetString());
        Assert.Equal("Anna", previous.GetProperty("guestName").GetString());
        Assert.Equal("+37499111222", previous.GetProperty("guestPhone").GetString());
        Assert.Equal((int)ReservationStatus.Confirmed, previous.GetProperty("status").GetInt32());
        Assert.Equal(2, previous.GetProperty("partySize").GetInt32());
        Assert.True(previous.TryGetProperty("startUtc", out _));

        Assert.False(item.TryGetProperty("nextBooking", out _));

        var sitting = item.GetProperty("currentSitting");
        Assert.Equal(noonUtc, sitting.GetProperty("seatedAtUtc").GetDateTime());
        Assert.Equal(2, sitting.GetProperty("partySize").GetInt32());
    }

    [SkippableFact]
    public async Task A_waiter_approves_and_declines_through_the_routes_and_a_late_answer_is_a_409()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);

        AuthBranch branch;
        AuthBranch otherVenue;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            otherVenue = await AuthTestData.CreateBranchAsync(db);
        }

        // Three diners, one per table at 20:00: one diner holds one table at a branch at a time.
        using var diner = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);
        using var second = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);
        using var third = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var outsider = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, otherVenue));

        var (_, date) = MoveToNoonInTwoDays(factory);

        var toApprove = await BookAsync(diner, branch, date, "20:00", tableId: branch.TableIds[0]);
        var toDecline = await BookAsync(second, branch, date, "20:00", tableId: branch.TableIds[1]);
        var tooLate = await BookAsync(third, branch, date, "20:00", tableId: branch.TableIds[2]);

        // Another venue's waiter guessing the id.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync($"/api/reservations/{toApprove}/approve", new { })).StatusCode);

        var approved = await waiter.PostAsJsonAsync($"/api/reservations/{toApprove}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal((int)ReservationStatus.Confirmed, (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());

        var declined = await waiter.PostAsJsonAsync($"/api/reservations/{toDecline}/reject", new { reason = "A private event tonight." });
        Assert.Equal(HttpStatusCode.OK, declined.StatusCode);

        var declinedBody = await declined.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)ReservationStatus.CancelledByVenue, declinedBody.GetProperty("status").GetInt32());
        Assert.Equal((int)DeclineReason.StaffDeclined, declinedBody.GetProperty("declinedBecause").GetInt32());
        Assert.Equal("A private event tonight.", declinedBody.GetProperty("cancellationReason").GetString());

        // Sixteen minutes on, nobody having answered the third: too late, whichever way.
        factory.Clock.Advance(TimeSpan.FromMinutes(16));

        foreach (var decision in new[] { "approve", "reject" })
        {
            var late = await waiter.PostAsJsonAsync($"/api/reservations/{tooLate}/{decision}", new { });
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);

            var problem = await late.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("reservation-approval-expired", problem.GetProperty("code").GetString());
            Assert.Equal(tooLate, problem.GetProperty("context").GetProperty("reservationId").GetGuid());
            Assert.Equal((int)ReservationStatus.PendingApproval, problem.GetProperty("context").GetProperty("status").GetInt32());
        }

        // And the floor's list no longer offers it.
        var awaiting = await waiter.GetFromJsonAsync<JsonElement>($"/api/branches/{branch.BranchId}/reservations/awaiting");
        Assert.DoesNotContain(
            awaiting.EnumerateArray(), a => a.GetProperty("reservation").GetProperty("id").GetGuid() == tooLate);
    }

    /// <summary>
    /// Moves the API's clock to 12:00 at the branch two days from now - after every token is issued -
    /// and answers with that instant and that date, on which an evening booking is at short notice.
    /// </summary>
    private static (DateTime NoonUtc, DateOnly Date) MoveToNoonInTwoDays(YallaApiFactory factory)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);
        var date = DateOnly.FromDateTime(localNow).AddDays(2);

        var noonUtc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(12, 0)), zone);
        factory.Clock.UtcNow = DateTime.SpecifyKind(noonUtc, DateTimeKind.Utc);

        return (factory.Clock.UtcNow, date);
    }

    private static async Task<Guid> BookAsync(
        HttpClient diner,
        AuthBranch branch,
        DateOnly date,
        string time,
        string guestName = "Ani Floor",
        string guestPhone = "+37411223344",
        Guid? tableId = null)
    {
        var created = await diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId = tableId ?? branch.FirstTableId,
                date = date.ToString("yyyy-MM-dd"),
                time,
                partySize = 2,
                guestName,
                guestPhone,
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)ReservationStatus.PendingApproval, body.GetProperty("status").GetInt32());

        return body.GetProperty("id").GetGuid();
    }
}
