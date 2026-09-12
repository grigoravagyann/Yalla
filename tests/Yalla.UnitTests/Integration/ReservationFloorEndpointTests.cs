using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The three siblings of <c>/release</c>, over HTTP: no-show, reject and extend-hold.
/// </summary>
/// <remarks>
/// <para>
/// <c>/release</c> had no HTTP-level test at all, which is why an omitted <c>outcome</c> could bind
/// <c>0</c>, miss the <c>NoShow</c> branch and be written as <c>CancelledByVenue</c> with a 200 for
/// as long as it did. Its three siblings sit in the same groups, take the same shape of request and
/// had the same gap.
/// </para>
/// <para>
/// <b>What only an HTTP test can see.</b> The service tests prove the rules. Everything between a
/// client and the service is untested by them: the authorization policy on the group, model binding,
/// <c>ClientCommandIdFilter</c>, <c>RequestValidationFilter</c> and <c>ApiExceptionMapper</c>. Each
/// test below is about one of those layers rather than about the rule underneath it.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class ReservationFloorEndpointTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ no-show

    /// <summary>
    /// A waiter marks a no-show; a diner cannot, and an idempotency key is required.
    /// </summary>
    [SkippableFact]
    public async Task Marking_a_no_show_is_staff_only_and_needs_a_command_id()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        var booking = await BookAsync(factory, world);

        // The diner who made the booking is not the floor. WaiterOrAbove is on the group, so this
        // is refused before the handler is reached.
        using var diner = factory.CreateClientWithToken(world.DinerToken);

        var byDiner = await diner.PostAsJsonAsync(
            $"/api/reservations/{booking}/no-show", new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Forbidden, byDiner.StatusCode);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, world.Branch));

        // Without the idempotency key: ClientCommandIdFilter, above the service, with its own
        // sentence about replays. Releasing twice from a tablet that lost its connection must not
        // put two no-shows on a diner's record.
        var noKey = await waiter.PostAsJsonAsync($"/api/reservations/{booking}/no-show", new { });

        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(
            "clientCommandId",
            (await noKey.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());

        // And properly, which writes the outcome that counts against the diner.
        var marked = await waiter.PostAsJsonAsync(
            $"/api/reservations/{booking}/no-show",
            new { clientCommandId = Guid.CreateVersion7(), reason = "nobody came" });

        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);
        var row = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == booking);

        Assert.Equal(ReservationStatus.NoShow, row.Status);
    }

    // ------------------------------------------------------------ reject

    /// <summary>
    /// A manager rejects a booking that is waiting for approval, and cannot reject a promised one.
    /// </summary>
    /// <remarks>
    /// The 409 is the point. Rejecting applies to a booking that was never promised; refusing one
    /// that is already <c>Confirmed</c> is what stops a manager quietly un-promising a table the
    /// diner has been told is theirs. That rule lives in the domain, and this proves the mapper
    /// carries it out as a conflict rather than a 400 or a 500.
    /// </remarks>
    [SkippableFact]
    public async Task Rejecting_is_manager_only_and_refuses_a_booking_already_promised()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        // A confirmed booking first: rejecting it must be refused.
        var confirmed = await BookAsync(factory, world);

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, world.Branch));

        var promised = await manager.PostAsJsonAsync(
            $"/api/reservations/{confirmed}/reject", new { reason = "changed my mind" });

        Assert.Equal(HttpStatusCode.Conflict, promised.StatusCode);

        // Now one that is genuinely waiting: the branch stops auto-confirming.
        await StopAutoConfirmingAsync(factory, world);

        var pending = await BookAsync(factory, world, tableIndex: 1);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(
                ReservationStatus.PendingApproval,
                (await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == pending)).Status);
        }

        // A waiter is not a manager. Approving and rejecting are the two decisions the group
        // reserves for ManagerOrAbove.
        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, world.Branch));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await waiter.PostAsJsonAsync($"/api/reservations/{pending}/reject", new { })).StatusCode);

        var rejected = await manager.PostAsJsonAsync(
            $"/api/reservations/{pending}/reject", new { reason = "fully committed that evening" });

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == pending);

            Assert.Equal(ReservationStatus.CancelledByVenue, row.Status);
            Assert.Equal("fully committed that evening", row.CancellationReason);
        }
    }

    /// <summary>A reason longer than the declared maximum is refused, naming the field.</summary>
    /// <remarks>
    /// <c>[MaxLength(500)]</c> on <c>DecideReservationRequest.Reason</c> was one of the forty
    /// attributes nothing enforced. This is the layer that now does.
    /// </remarks>
    [SkippableFact]
    public async Task A_rejection_reason_longer_than_the_declared_maximum_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        var booking = await BookAsync(factory, world);

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, world.Branch));

        var response = await manager.PostAsJsonAsync(
            $"/api/reservations/{booking}/reject", new { reason = new string('x', 501) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "reason",
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());
    }

    // ------------------------------------------------------------ extend-hold

    /// <summary>
    /// Only the diner who made the booking may extend its hold, and only with a command id.
    /// </summary>
    /// <remarks>
    /// The interesting boundary is the second diner: <c>VerifiedDiner</c> lets them past the policy
    /// on the group, and the service then refuses them for not owning the booking. A test that only
    /// checked an anonymous caller would not distinguish the two.
    /// </remarks>
    [SkippableFact]
    public async Task Only_the_booking_s_own_diner_can_extend_its_hold()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        var booking = await BookAsync(factory, world);

        using var anonymous = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(
                $"/api/reservations/{booking}/extend-hold",
                new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // A different verified diner passes the policy and is refused by the service.
        using var stranger = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var byStranger = await stranger.PostAsJsonAsync(
            $"/api/reservations/{booking}/extend-hold",
            new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Forbidden, byStranger.StatusCode);

        // The owner, with no command id: refused above the service, as everywhere else.
        using var diner = factory.CreateClientWithToken(world.DinerToken);

        var noKey = await diner.PostAsJsonAsync($"/api/reservations/{booking}/extend-hold", new { });

        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(
            "clientCommandId",
            (await noKey.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());
    }

    // ------------------------------------------------------------ helpers

    private sealed record World(AuthBranch Branch, string DinerToken);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<World> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db);

        return new World(branch, await SignInDinerAsync(factory));
    }

    /// <summary>Books a table as the world's diner and returns the booking's id.</summary>
    private async Task<Guid> BookAsync(YallaApiFactory factory, World world, int tableIndex = 0)
    {
        using var diner = factory.CreateClientWithToken(world.DinerToken);

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        var response = await diner.PostAsJsonAsync("/api/reservations", new
        {
            branchId = world.Branch.BranchId,
            tableId = world.Branch.TableIds[tableIndex],
            date = DateOnly.FromDateTime(localNow).AddDays(2).ToString("yyyy-MM-dd"),
            time = "18:00",
            partySize = 2,
            guestName = "Ani",
            guestPhone = "+37411223344",
            clientCommandId = Guid.CreateVersion7(),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Switches the branch off auto-confirm, so the next booking lands as PendingApproval.
    /// </summary>
    private async Task StopAutoConfirmingAsync(YallaApiFactory factory, World world)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await db.Branches.FirstAsync(b => b.Id == world.Branch.BranchId);
        var current = branch.ReservationPolicy;

        branch.UpdateReservationPolicy(
            new ReservationPolicy(
                current.TurnTimeMinutes,
                current.BufferMinutes,
                current.GraceMinutes,
                current.LateNudgeAfterMinutes,
                current.GraceExtensionMinutes,
                current.MinLeadMinutes,
                current.BookingWindowDays,
                current.CancellationDeadlineMinutes,
                autoConfirm: false,
                current.ServiceChargePercent,
                current.PricesIncludeVat,
                current.MaxSeatOverhang,
                current.ApprovalRequiredAbovePartySize,
                current.WalkInHoldbackMinutes,
                current.ReminderHoursBefore),
            factory.Clock.UtcNow);

        await db.SaveChangesAsync();
    }

    private static async Task<string> SignInManagerAsync(YallaApiFactory factory, AuthBranch branch)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in", new { email = branch.ManagerEmail, password = branch.ManagerPassword });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }
}
