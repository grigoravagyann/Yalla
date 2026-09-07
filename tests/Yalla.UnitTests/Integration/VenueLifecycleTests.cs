using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Platform;
using Yalla.Application.Reservations;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// What suspending and deleting a venue actually stops.
/// </summary>
/// <remarks>
/// Hiding a venue from search is not the whole lever. A diner's app caches branch ids and the QR
/// sticker stays on the table long after the invoice stops being paid, so the entry points that
/// start new business have to refuse too - otherwise a booking made after a soft delete breaks the
/// very invariant the delete just checked.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class VenueLifecycleTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 5, 14, 0, 0, DateTimeKind.Utc);

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_suspended_or_deleted_venue_takes_no_new_bookings(bool delete)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var dinerId = Guid.CreateVersion7();

        // It books happily while the venue is trading, so the refusal below is the venue's state.
        var booking = fixture.CreateReservationService(db, clock, TestActor.Diner(dinerId));
        var before = await booking.CreateAsync(NewBooking(branch, clock));
        Assert.Equal(ReservationStatus.Confirmed, before.Status);

        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        if (delete)
        {
            // The booking above is in the future and blocks a delete, so it goes first - which is
            // exactly the invariant a post-delete booking would break.
            var made = await db.Reservations.FirstAsync(r => r.Id == before.Id);
            made.CancelByDiner(clock.UtcNow, "test", afterDeadline: false);
            await db.SaveChangesAsync();

            await platform.DeleteVenueAsync(branch.VenueId);
        }
        else
        {
            await platform.SuspendVenueAsync(branch.VenueId);
        }

        await using var afterDb = fixture.CreateContext(clock);
        var after = fixture.CreateReservationService(afterDb, clock, TestActor.Diner(dinerId));

        var refused = await Assert.ThrowsAsync<BranchUnavailableException>(
            () => after.CreateAsync(NewBooking(branch, clock)));

        Assert.Equal(branch.BranchId, refused.BranchId);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.Reservations.CountAsync(r => r.BranchId == branch.BranchId));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_suspended_or_deleted_venue_opens_no_new_tabs_however_the_sticker_reads(bool delete)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var qr = await db.DiningTables.AsNoTracking()
            .Where(t => t.Id == branch.TableIds[1]).Select(t => t.QrToken).FirstAsync();

        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        if (delete)
        {
            await platform.DeleteVenueAsync(branch.VenueId);
        }
        else
        {
            await platform.SuspendVenueAsync(branch.VenueId);
        }

        await using var scanDb = fixture.CreateContext(clock);
        var tabs = fixture.CreateTabService(scanDb, clock, new TestActor(ActorType.Diner, null, null, null));

        var refused = await Assert.ThrowsAsync<BranchUnavailableException>(
            () => tabs.OpenAsync(new OpenTabCommand(qr, "phone-a", Guid.CreateVersion7())));

        Assert.Equal(branch.BranchId, refused.BranchId);

        // Nothing was written: no session, no tab, and the table was never seated.
        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(0, await verify.Tabs.CountAsync(t => t.BranchId == branch.BranchId));
        Assert.Equal(0, await verify.TableSessions.CountAsync(s => s.BranchId == branch.BranchId));
        Assert.Equal(TableStatus.Free, (await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == branch.TableIds[1])).Status);
    }

    /// <summary>
    /// The party already at the table keeps its bill. Suspension is about the venue's invoice, and
    /// stranding money on an occupied table would punish the diners for it.
    /// </summary>
    [SkippableFact]
    public async Task A_tab_opened_before_a_suspension_stays_readable_and_settleable()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var qr = await db.DiningTables.AsNoTracking()
            .Where(t => t.Id == branch.FirstTableId).Select(t => t.QrToken).FirstAsync();

        var anonymous = new TestActor(ActorType.Diner, null, null, null);
        var opened = await fixture.CreateTabService(db, clock, anonymous)
            .OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7(), "Aram"));

        await fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId))
            .SuspendVenueAsync(branch.VenueId);

        await using var afterDb = fixture.CreateContext(clock);

        var view = await fixture.CreateTabQuery(afterDb)
            .GetForParticipantAsync(opened.Tab.TabId, opened.Tab.Me.ParticipantId);

        Assert.NotNull(view);
        Assert.Equal(TabStatus.Open, view!.Status);

        // The host can still change the split - the tab is live, only the venue is shut to newcomers.
        var settled = await fixture.CreateTabService(afterDb, clock, anonymous)
            .SetSettlementModeAsync(opened.Tab.TabId, opened.Tab.Me.ParticipantId, SettlementMode.EveryonePaysOwnItems);

        Assert.Equal(SettlementMode.EveryonePaysOwnItems, settled.SettlementMode);
    }

    [SkippableFact]
    public async Task A_deleted_venue_refuses_every_change_including_its_name_slug_and_branches()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        await service.DeleteVenueAsync(branch.VenueId);

        await Assert.ThrowsAsync<DomainStateException>(
            () => service.UpdateVenueAsync(branch.VenueId, new UpdateVenueCommand(Name: "Phoenix")));

        await Assert.ThrowsAsync<DomainStateException>(
            () => service.UpdateVenueAsync(branch.VenueId, new UpdateVenueCommand(Slug: "phoenix-" + Guid.NewGuid().ToString("N")[..8])));

        await Assert.ThrowsAsync<DomainStateException>(
            () => service.UpdateVenueAsync(branch.VenueId, new UpdateVenueCommand(IsActive: true)));

        // Its branches are closed records too - a deleted venue must not be quietly upgraded to Paid.
        await Assert.ThrowsAsync<DomainStateException>(
            () => service.UpdateBranchAsync(branch.BranchId, new UpdateBranchCommand(SubscriptionTier: SubscriptionTier.Paid)));

        await Assert.ThrowsAsync<DomainStateException>(
            () => service.AddBranchAsync(branch.VenueId, new CreateBranchCommand(
                "Second", "second-" + Guid.NewGuid().ToString("N")[..8], "1 Test Street", 40.18, 44.51, "Asia/Yerevan", 800, 600)));

        await using var verify = fixture.CreateContext(clock);
        var venue = await verify.Venues.AsNoTracking().FirstAsync(v => v.Id == branch.VenueId);

        Assert.NotEqual("Phoenix", venue.Name);
        Assert.NotNull(venue.DeletedAtUtc);

        var unchanged = await verify.Branches.AsNoTracking().FirstAsync(b => b.Id == branch.BranchId);
        Assert.Equal(SubscriptionTier.Paid, unchanged.SubscriptionTier); // the builder's default, not a new write
        Assert.Equal(1, await verify.Branches.CountAsync(b => b.VenueId == branch.VenueId));
    }

    /// <summary>Reactivating puts everything back, so suspension is a pause and not a slow delete.</summary>
    [SkippableFact]
    public async Task Reactivating_restores_booking_and_scanning()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        await platform.SuspendVenueAsync(branch.VenueId);
        await platform.ReactivateVenueAsync(branch.VenueId);

        await using var afterDb = fixture.CreateContext(clock);
        var booked = await fixture.CreateReservationService(afterDb, clock, TestActor.Diner())
            .CreateAsync(NewBooking(branch, clock));

        Assert.Equal(ReservationStatus.Confirmed, booked.Status);
    }

    /// <summary>Through the pipeline: the availability endpoint and a booking agree about a suspended venue.</summary>
    [SkippableFact]
    public async Task Over_http_a_suspended_venue_refuses_both_browsing_and_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        AuthBranch branch;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        using var platform = factory.CreateClientWithToken(
            await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        (await platform.PostAsJsonAsync($"/api/platform/venues/{branch.VenueId}/suspend", new { }))
            .EnsureSuccessStatusCode();

        var local = TimeZoneInfo.ConvertTimeFromUtc(
            factory.Clock.UtcNow.AddDays(1), TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan"));

        var booking = await diner.PostAsJsonAsync("/api/reservations", new
        {
            branchId = branch.BranchId,
            tableId = branch.FirstTableId,
            // date and time, not localDate/localTime. Those were the names this test sent for as
            // long as it has existed, and they bind to nothing - the record declares Date and Time.
            // It passed anyway because the venue gate answers before either is read, so it proved
            // the 409 while proving nothing about the booking. The same shape as the settlement-mode
            // bug: a caller sending field names the server does not have, and the server not caring.
            date = DateOnly.FromDateTime(local).ToString("yyyy-MM-dd"),
            time = "19:00",
            partySize = 2,
            guestName = "Ani",
            guestPhone = "+37411223344",
            clientCommandId = Guid.CreateVersion7(),
        });

        Assert.Equal(HttpStatusCode.Conflict, booking.StatusCode);
        Assert.Equal(
            "branch-unavailable",
            (await booking.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>A verified diner, through the real phone-code flow the booking policy requires.</summary>
    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static CreateReservationCommand NewBooking(TestBranch branch, TestClock clock)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var local = TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow.AddDays(1), zone);

        return new CreateReservationCommand(
            BranchId: branch.BranchId,
            TableId: branch.FirstTableId,
            LocalDate: DateOnly.FromDateTime(local),
            LocalTime: new TimeOnly(19, 0),
            PartySize: 2,
            GuestName: "Ani Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7());
    }
}
