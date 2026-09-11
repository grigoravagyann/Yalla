using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>GET /api/branches/{branchId}/reservations?status=</c> - the list the console's
/// "awaiting approval" panel reads before it calls approve or reject.
/// </summary>
/// <remarks>
/// <para>
/// The route carries <c>BranchScoped</c>, which widens a venue user to every branch of their
/// venue. That is right for settings and wrong for this list: a manager whose record names a home
/// branch decides that branch's bookings and nobody else's, and approve and reject already refuse
/// them elsewhere. The list goes through the same service check, so what a manager can see and
/// what they can decide are one rule - and the branch-B-manager test here is what proves the
/// narrower half actually runs behind the policy.
/// </para>
/// <para>
/// The property-name assertions are deliberate and exact: the console's contract is written
/// against these names, and a rename here is a blank panel there with no test to say why.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class BranchReservationListTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task An_owner_lists_the_pending_booking_with_the_names_the_console_reads()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        PanelAccount owner;
        Guid bigTableId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            bigTableId = await AddBigTableAsync(db, branch);
        }

        var pendingId = await BookAsync(factory, branch, bigTableId, partySize: 9, expected: ReservationStatus.PendingApproval);

        using var client = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));

        var response = await client.GetAsync(ListUrl(branch.BranchId, ReservationStatus.PendingApproval));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        var row = Assert.Single(body.EnumerateArray());

        // Exactly the names the console's contract is written against.
        Assert.Equal(pendingId, row.GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("code").GetString()));
        Assert.Equal(branch.BranchId, row.GetProperty("branchId").GetGuid());
        Assert.Equal("Ani Test", row.GetProperty("guestName").GetString());
        Assert.Equal("+37411223344", row.GetProperty("guestPhone").GetString());
        Assert.Equal(9, row.GetProperty("partySize").GetInt32());
        Assert.Equal("12", row.GetProperty("tableLabel").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", row.GetProperty("localDate").GetString());
        Assert.Equal("18:00:00", row.GetProperty("localStartTime").GetString());
        Assert.Equal((int)ReservationStatus.PendingApproval, row.GetProperty("status").GetInt32());
        Assert.Equal(
            (int)ApprovalTrigger.LargeParty,
            row.GetProperty("awaitingApprovalBecause").GetInt32());
    }

    [SkippableFact]
    public async Task A_confirmed_booking_is_not_in_the_pending_list_but_is_in_the_confirmed_one()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        PanelAccount owner;
        Guid bigTableId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            bigTableId = await AddBigTableAsync(db, branch);
        }

        var pendingId = await BookAsync(factory, branch, bigTableId, partySize: 9, expected: ReservationStatus.PendingApproval);
        var confirmedId = await BookAsync(factory, branch, branch.FirstTableId, partySize: 2, expected: ReservationStatus.Confirmed);

        using var client = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));

        var pending = await Ids(client, ListUrl(branch.BranchId, ReservationStatus.PendingApproval));
        var confirmed = await Ids(client, ListUrl(branch.BranchId, ReservationStatus.Confirmed));

        Assert.Equal([pendingId], pending);
        Assert.Equal([confirmedId], confirmed);
    }

    [SkippableFact]
    public async Task A_manager_of_branch_B_cannot_list_branch_A_even_inside_their_own_venue()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        PanelAccount managerElsewhere;
        PanelAccount managerEverywhere;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);

            var branchB = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan");

            // Same venue, so BranchScoped lets them through: the refusal has to come from the
            // service, the same check approve and reject make.
            managerElsewhere = await AuthTestData.SeedManagerAsync(db, branch.VenueId, branchId: branchB);

            // A manager whose record names no branch covers every branch of the venue.
            managerEverywhere = await AuthTestData.SeedManagerAsync(db, branch.VenueId, branchId: null);
        }

        using var elsewhere = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, managerElsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await elsewhere.GetAsync(ListUrl(branch.BranchId, ReservationStatus.PendingApproval))).StatusCode);

        using var everywhere = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, managerEverywhere));

        Assert.Equal(
            HttpStatusCode.OK,
            (await everywhere.GetAsync(ListUrl(branch.BranchId, ReservationStatus.PendingApproval))).StatusCode);

        // And the branch's own manager, whose record names it.
        using var home = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        Assert.Equal(
            HttpStatusCode.OK,
            (await home.GetAsync(ListUrl(branch.BranchId, ReservationStatus.PendingApproval))).StatusCode);
    }

    [SkippableFact]
    public async Task Another_venues_manager_a_waiter_and_an_anonymous_caller_are_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        AuthBranch elsewhere;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            elsewhere = await AuthTestData.CreateBranchAsync(db);
        }

        var url = ListUrl(branch.BranchId, ReservationStatus.PendingApproval);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);

        using var outsider = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, elsewhere));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync(url)).StatusCode);
    }

    [SkippableFact]
    public async Task A_missing_status_is_refused_with_the_house_422()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        PanelAccount owner;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        }

        using var client = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));

        var response = await client.GetAsync($"/api/branches/{branch.BranchId}/reservations");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var fields = body.GetProperty("context").GetProperty("fields");

        Assert.Contains(fields.EnumerateArray(), f => f.GetProperty("field").GetString() == "status");

        // A number that is no status - 3 was Late, retired - is refused the same way.
        var unknown = await client.GetAsync($"/api/branches/{branch.BranchId}/reservations?status=3");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static string ListUrl(Guid branchId, ReservationStatus status) =>
        $"/api/branches/{branchId}/reservations?status={(int)status}";

    private static async Task<List<Guid>> Ids(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid())
            .ToList();
    }

    /// <summary>
    /// Ten seats, so a party of nine fits and lands over the seeded approval threshold of eight
    /// without tripping the seat-overhang rule - the same recipe the approve tests use.
    /// </summary>
    private static async Task<Guid> AddBigTableAsync(YallaDbContext db, AuthBranch branch)
    {
        var testBranch = new TestBranch(
            branch.VenueId, branch.BranchId, branch.WaiterId, branch.ManagerId, branch.TableIds, "Asia/Yerevan");

        return (await TestBranchBuilder.AddTableAsync(db, testBranch, "12", seats: 10)).Id;
    }

    private static async Task<Guid> BookAsync(
        YallaApiFactory factory,
        AuthBranch branch,
        Guid tableId,
        int partySize,
        ReservationStatus expected)
    {
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);
        var date = DateOnly.FromDateTime(localNow).AddDays(2);

        var created = await diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId,
                date = date.ToString("yyyy-MM-dd"),
                time = "18:00",
                partySize,
                guestName = "Ani Test",
                guestPhone = "+37411223344",
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)expected, body.GetProperty("status").GetInt32());

        return body.GetProperty("id").GetGuid();
    }

    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });

        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
}
