using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Platform;
using Yalla.Application.Reviews;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The audit log as the console's activity feed reads it: every platform mutation names who did it
/// and what it was done to as they were called then, and the venue it concerns; a tier change is its
/// own entry; staff a venue's owner adds are in it too; and the feed is newest first, limited,
/// narrowable to one venue, and leaves out the rows no member of staff wrote.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformActivityTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Every_platform_mutation_records_who_and_what_by_name_and_the_venue_it_concerns()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow.AddYears(2));
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var actor = TestActor.PlatformAdmin(admin.StaffMemberId);
        var platform = fixture.CreatePlatformService(db, clock, actor);
        var marker = Guid.NewGuid().ToString("N")[..8];

        var created = await platform.CreateVenueAsync(new CreateVenueCommand($"Luna {marker}", VenueType.Cafe, $"luna-{marker}", Branch("main")));
        var venueId = created.Venue.VenueId;
        var branchId = created.Branches[0].BranchId;

        clock.Advance(TimeSpan.FromSeconds(1));
        await platform.UpdateVenueAsync(venueId, new UpdateVenueCommand(Name: $"Luna Cafe {marker}"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await platform.UpdateBranchAsync(branchId, new UpdateBranchCommand(SubscriptionTier: SubscriptionTier.Paid));
        clock.Advance(TimeSpan.FromSeconds(1));
        await platform.UpdateBranchAsync(branchId, new UpdateBranchCommand(Name: "Kaskad", SubscriptionTier: SubscriptionTier.Free));
        clock.Advance(TimeSpan.FromSeconds(1));
        await platform.UpdateBranchAsync(branchId, new UpdateBranchCommand(Name: "Kaskad Terrace"));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await platform.AddBranchAsync(venueId, Branch("arabkir"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await platform.SuspendVenueAsync(venueId);
        clock.Advance(TimeSpan.FromSeconds(1));
        await platform.ReactivateVenueAsync(venueId);
        clock.Advance(TimeSpan.FromSeconds(1));
        await platform.DeleteVenueAsync(venueId);

        await using var verify = fixture.CreateContext(clock);
        var rows = await verify.PlatformAuditLogs
            .AsNoTracking()
            .Where(l => l.VenueId == venueId)
            .ToListAsync();

        // One group per call, in order. The rows of one call share its instant, so within a call the
        // order is the set's, not the list's.
        var calls = rows
            .GroupBy(r => r.AtUtc)
            .OrderBy(g => g.Key)
            .Select(g => g.Select(r => r.Action).Order(StringComparer.Ordinal).ToArray())
            .ToList();

        string[][] expected =
        [
            ["branch.create", "venue.create"],
            ["venue.update"],
            ["branch.tier-change"],
            ["branch.tier-change", "branch.update"],
            ["branch.update"],
            ["branch.create"],
            ["venue.suspend"],
            ["venue.reactivate"],
            ["venue.delete"],
        ];

        Assert.Equal(expected.Length, calls.Count);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], calls[i]);
        }

        Assert.All(rows, r =>
        {
            Assert.Equal(admin.StaffMemberId, r.ActorStaffMemberId);
            Assert.Equal(StaffRole.PlatformAdmin, r.ActorRole);
            Assert.Equal("Platform Administrator", r.ActorDisplay);
            Assert.Equal(venueId, r.VenueId);
        });

        PlatformAuditLog Row(string action, int nth = 0) =>
            rows.Where(r => r.Action == action).OrderBy(r => r.AtUtc).ElementAt(nth);

        // Names as they were then: created as "Luna", renamed after.
        Assert.Equal($"Luna {marker}", Row("venue.create").TargetDisplay);
        Assert.Equal($"Luna Cafe {marker}", Row("venue.update").TargetDisplay);
        Assert.Equal($"Luna Cafe {marker}", Row("venue.delete").TargetDisplay);
        Assert.Equal("Branch main", Row("branch.create").TargetDisplay);
        Assert.Equal("Branch arabkir", Row("branch.create", 1).TargetDisplay);
        Assert.Equal(second.BranchId, Row("branch.create", 1).TargetId);

        // A tier change says from and to; a request that changes the tier and the name writes both
        // entries, and one that changes only the name writes only the update.
        using (var tier = JsonDocument.Parse(Row("branch.tier-change").ChangesJson))
        {
            Assert.Equal((int)SubscriptionTier.Free, tier.RootElement.GetProperty("from").GetInt32());
            Assert.Equal((int)SubscriptionTier.Paid, tier.RootElement.GetProperty("to").GetInt32());
            Assert.Equal(branchId, Row("branch.tier-change").TargetId);
        }

        Assert.Equal("Kaskad", Row("branch.tier-change", 1).TargetDisplay);
        Assert.Equal("Kaskad", Row("branch.update").TargetDisplay);
        Assert.Equal("Kaskad Terrace", Row("branch.update", 1).TargetDisplay);
    }

    [SkippableFact]
    public async Task A_change_that_does_not_move_the_tier_writes_no_tier_entry()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db, subscriptionTier: SubscriptionTier.Paid);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        // Already paid: asking for Paid again is not a tier change.
        await platform.UpdateBranchAsync(branch.BranchId, new UpdateBranchCommand(SubscriptionTier: SubscriptionTier.Paid));

        await using var verify = fixture.CreateContext(clock);
        var actions = await verify.PlatformAuditLogs.AsNoTracking()
            .Where(l => l.TargetId == branch.BranchId)
            .Select(l => l.Action)
            .ToListAsync();

        Assert.Equal(["branch.update"], actions);
    }

    [SkippableFact]
    public async Task Staff_added_by_a_venues_owner_are_in_the_feed_under_the_owners_name_and_role()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow.AddYears(2).AddDays(1));
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);

        var staff = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.StaffMemberId));
        var mariam = await staff.CreateAsync(
            branch.VenueId,
            new CreateStaffCommand("Mariam Avetisyan", "+37455501234", StaffRole.Manager, "5813", branch.BranchId));

        clock.Advance(TimeSpan.FromSeconds(1));
        await staff.IssueSignInAsync(branch.VenueId, mariam.Id, new IssueSignInCommand($"mariam-{Guid.NewGuid():N}@example.test"));

        var feed = await fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId))
            .GetActivityAsync(new ActivityQuery(Limit: 10, VenueId: branch.VenueId));

        Assert.Equal(["staff.sign-in-issued", "staff.create"], feed.Items.Select(i => i.Action));

        var added = feed.Items[1];
        Assert.Equal(owner.StaffMemberId, added.Actor.StaffMemberId);
        Assert.Equal(StaffRole.Owner, added.Actor.Role);
        Assert.Equal("Founder", added.Actor.DisplayName);
        Assert.Equal("StaffMember", added.Target.Type);
        Assert.Equal(mariam.Id, added.Target.Id);
        Assert.Equal("Mariam Avetisyan", added.Target.DisplayName);
        Assert.Equal(branch.VenueId, added.Venue!.VenueId);
        Assert.Equal((int)StaffRole.Manager, added.Detail!.Value.GetProperty("role").GetInt32());
        Assert.Equal(branch.BranchId, added.Detail.Value.GetProperty("branchId").GetGuid());

        // Never the PIN, the password or a hash of either.
        var detail = added.Detail.Value.GetRawText();
        Assert.DoesNotContain("5813", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("pin", detail, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task The_feed_is_newest_first_limited_and_leaves_out_rows_no_staff_member_wrote_and_failed_password_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // The venue on today's clock, so it is not the newest venue for the overview's tests; the
        // changes far ahead of every other test's, so they are the newest rows in the shared log.
        TestBranch branch;
        PlatformAdminAccount admin;

        await using (var today = fixture.CreateContext(new TestClock(TestNow.UtcNow)))
        {
            branch = await TestBranchBuilder.CreateAsync(today);
            admin = await AuthTestData.CreatePlatformAdminAsync(today);
        }

        var clock = new TestClock(TestNow.UtcNow.AddYears(200));
        await using var db = fixture.CreateContext(clock);
        var actor = TestActor.PlatformAdmin(admin.StaffMemberId);
        var platform = fixture.CreatePlatformService(db, clock, actor);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, actor);

        await platform.SuspendVenueAsync(branch.VenueId);
        clock.Advance(TimeSpan.FromSeconds(1));
        await platform.ReactivateVenueAsync(branch.VenueId);
        clock.Advance(TimeSpan.FromSeconds(1));

        // A diner deleting their account writes a row with no staff member behind it.
        var dinerId = Guid.NewGuid();
        db.PlatformAuditLogs.Add(new PlatformAuditLog(dinerId, "diner.account-deleted", "DinerUser", dinerId, "{\"actorType\":\"diner\"}", clock.UtcNow));
        await db.SaveChangesAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        // A wrong current password is written by the admin, but it is the lockout's counter, not a change.
        await Assert.ThrowsAsync<FieldValidationException>(
            () => fixture.CreatePlatformAdminAccountService(db, clock, actor)
                .ChangePasswordAsync(new ChangePlatformAdminPasswordCommand("not-the-password-at-all", "a-new-password-long-enough")));
        Assert.True(await db.PlatformAuditLogs.AnyAsync(
            l => l.ActorStaffMemberId == admin.StaffMemberId && l.Action == "admin.password-change-failed" && l.ActorRole != null));
        clock.Advance(TimeSpan.FromSeconds(1));

        await platform.SuspendVenueAsync(branch.VenueId);

        var feed = await query.GetActivityAsync(new ActivityQuery(Limit: 3));

        Assert.Equal(3, feed.Items.Count);
        Assert.Equal(["venue.suspend", "venue.reactivate", "venue.suspend"], feed.Items.Select(i => i.Action));
        Assert.DoesNotContain(feed.Items, i => i.Action == "diner.account-deleted");
        Assert.DoesNotContain(feed.Items, i => i.Action == "admin.password-change-failed");
        Assert.True(feed.Items[0].AtUtc > feed.Items[1].AtUtc);

        var top = feed.Items[0];
        Assert.Equal(StaffRole.PlatformAdmin, top.Actor.Role);
        Assert.Equal("Platform Administrator", top.Actor.DisplayName);
        Assert.Equal("Venue", top.Target.Type);
        Assert.Equal(branch.VenueId, top.Target.Id);
        Assert.Equal(branch.VenueId, top.Venue!.VenueId);
        Assert.StartsWith("Test Venue", top.Venue.Name, StringComparison.Ordinal);
        Assert.Equal(DateTimeKind.Utc, top.AtUtc.Kind);
    }

    [SkippableFact]
    public async Task A_limit_outside_one_to_a_hundred_is_refused_naming_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var low = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.GetActivityAsync(new ActivityQuery(Limit: 0)));
        var high = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.GetActivityAsync(new ActivityQuery(Limit: 101)));

        Assert.Equal("limit", low.ParamName);
        Assert.Equal("limit", high.ParamName);

        Assert.InRange((await query.GetActivityAsync(new ActivityQuery(Limit: 100))).Items.Count, 0, 100);
    }

    [SkippableFact]
    public async Task A_review_moderated_by_the_platform_is_named_by_its_branch_never_its_author()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var author = await ReviewTestData.SeedDinerAsync(db, "Anahit Reviewersurname", clock.UtcNow);
        var reviewId = await ReviewTestData.SeedReviewAsync(db, branch.BranchId, author, 1, "Names a waiter.", clock.UtcNow);

        var moderation = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        await moderation.SetVisibilityForPlatformAsync(reviewId, new SetReviewVisibilityCommand(true, "Personal data"));

        await using var verify = fixture.CreateContext(clock);
        var row = await verify.PlatformAuditLogs.AsNoTracking().SingleAsync(l => l.TargetId == reviewId);
        var branchName = await verify.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.Name).FirstAsync();

        Assert.Equal("review.hide", row.Action);
        Assert.Equal(nameof(BranchReview), row.TargetType);
        Assert.Equal(branchName, row.TargetDisplay);
        Assert.Equal(branch.VenueId, row.VenueId);
        Assert.DoesNotContain("Reviewersurname", row.ChangesJson + row.TargetDisplay + row.ActorDisplay, StringComparison.Ordinal);
    }

    private static CreateBranchCommand Branch(string name) => new(
        Name: $"Branch {name}",
        Slug: $"{name}-{Guid.NewGuid():N}"[..24],
        Address: "1 Test Street, Yerevan",
        Latitude: 40.18,
        Longitude: 44.51,
        TimeZoneId: "Asia/Yerevan");
}
