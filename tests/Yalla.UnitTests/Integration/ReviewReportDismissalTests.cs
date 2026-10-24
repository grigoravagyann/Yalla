using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reviews;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Dismissing a review's reports: the review stays as it is and stops counting as reported, for the
/// platform's list and the venue's; the reports are kept, marked; a second dismissal writes nothing;
/// a report filed afterwards counts again; and only a platform admin may do it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReviewReportDismissalTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Dismissing_leaves_the_review_up_clears_the_reported_state_and_keeps_the_reports()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var (reviewId, reporters) = await ReportedReviewAsync(db, branch.BranchId, clock, reports: 3);

        var moderation = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var before = await moderation.ListForPlatformAsync(branch.BranchId, new ReviewModerationQuery(Filter: "reported"));
        Assert.Equal(3, Assert.Single(before.Items).ReportCount);

        clock.Advance(TimeSpan.FromMinutes(5));
        var dismissed = await moderation.DismissReportsForPlatformAsync(reviewId);

        Assert.Equal(reviewId, dismissed.ReviewId);
        Assert.Equal(0, dismissed.ReportCount);
        Assert.Null(dismissed.LastReportedAtUtc);
        Assert.False(dismissed.Hidden);

        // Out of the reported filter, for both tiers; still in the list, still published.
        var reported = await moderation.ListForPlatformAsync(branch.BranchId, new ReviewModerationQuery(Filter: "reported"));
        Assert.Empty(reported.Items);
        Assert.Equal(0, reported.Total);

        var venueModeration = fixture.CreateReviewModerationService(db, clock, TestActor.Manager(branch.ManagerId));
        Assert.Empty((await venueModeration.ListForVenueAsync(branch.BranchId, new ReviewModerationQuery(Filter: "reported"))).Items);
        Assert.Equal(0, Assert.Single((await venueModeration.ListForVenueAsync(branch.BranchId, new ReviewModerationQuery())).Items).ReportCount);

        await using var verify = fixture.CreateContext(clock);
        var review = await verify.BranchReviews.AsNoTracking().SingleAsync(r => r.Id == reviewId);
        Assert.Null(review.HiddenAtUtc);

        // The reports are the diners' word and stay, marked as set aside by this admin.
        var rows = await verify.BranchReviewReports.AsNoTracking().Where(r => r.ReviewId == reviewId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.True(r.IsDismissed);
            Assert.Equal(admin.StaffMemberId, r.DismissedByStaffMemberId);
            Assert.Equal(clock.UtcNow, r.DismissedAtUtc);
        });

        var audit = await verify.PlatformAuditLogs.AsNoTracking().SingleAsync(l => l.TargetId == reviewId);
        Assert.Equal(ReviewModerationService.DismissReportsAuditAction, audit.Action);
        Assert.Equal(nameof(BranchReview), audit.TargetType);
        Assert.Equal(branch.VenueId, audit.VenueId);
        Assert.Contains("\"dismissed\":3", audit.ChangesJson, StringComparison.Ordinal);

        _ = reporters;
    }

    [SkippableFact]
    public async Task Dismissing_again_writes_nothing_and_a_report_filed_afterwards_counts_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var (reviewId, _) = await ReportedReviewAsync(db, branch.BranchId, clock, reports: 2);
        var moderation = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var firstAt = clock.UtcNow;
        await moderation.DismissReportsForPlatformAsync(reviewId);

        clock.Advance(TimeSpan.FromMinutes(1));
        var again = await moderation.DismissReportsForPlatformAsync(reviewId);
        Assert.Equal(0, again.ReportCount);

        await using (var verify = fixture.CreateContext(clock))
        {
            Assert.Equal(1, await verify.PlatformAuditLogs.CountAsync(l => l.TargetId == reviewId));
            Assert.All(
                await verify.BranchReviewReports.AsNoTracking().Where(r => r.ReviewId == reviewId).ToListAsync(),
                r => Assert.Equal(firstAt, r.DismissedAtUtc));
        }

        // Somebody new reports it: it is reported again, by one.
        clock.Advance(TimeSpan.FromMinutes(1));
        var late = await ReviewTestData.SeedDinerAsync(db, "Late Reporter", clock.UtcNow);
        db.BranchReviewReports.Add(new BranchReviewReport(reviewId, late, ReviewReportReasons.Spam, null, clock.UtcNow));
        await db.SaveChangesAsync();

        var reported = await moderation.ListForPlatformAsync(branch.BranchId, new ReviewModerationQuery(Filter: "reported"));
        var row = Assert.Single(reported.Items);
        Assert.Equal(1, row.ReportCount);
        Assert.Equal(clock.UtcNow, row.LastReportedAtUtc);
    }

    [SkippableFact]
    public async Task A_reported_review_once_hidden_leaves_the_reported_list()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var (reviewId, _) = await ReportedReviewAsync(db, branch.BranchId, clock, reports: 2);
        var moderation = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        await moderation.SetVisibilityForPlatformAsync(reviewId, new SetReviewVisibilityCommand(true, "Offensive"));

        // Hiding it answered the reports: it is under Hidden, no longer waiting under Reported.
        var reported = await moderation.ListForPlatformAsync(branch.BranchId, new ReviewModerationQuery(Filter: "reported"));
        Assert.Empty(reported.Items);
        var hidden = await moderation.ListForPlatformAsync(branch.BranchId, new ReviewModerationQuery(Filter: "hidden"));
        Assert.Equal(reviewId, Assert.Single(hidden.Items).ReviewId);
    }

    [SkippableFact]
    public async Task A_hidden_review_stays_hidden_when_its_reports_are_dismissed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var (reviewId, _) = await ReportedReviewAsync(db, branch.BranchId, clock, reports: 1);
        var moderation = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        await moderation.SetVisibilityForPlatformAsync(reviewId, new SetReviewVisibilityCommand(true, "Offensive"));
        var dismissed = await moderation.DismissReportsForPlatformAsync(reviewId);

        Assert.True(dismissed.Hidden);
        Assert.Equal("Offensive", dismissed.HiddenReason);
        Assert.Equal(0, dismissed.ReportCount);
    }

    [SkippableFact]
    public async Task Only_a_platform_admin_dismisses_reports_and_an_unknown_review_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        var (reviewId, _) = await ReportedReviewAsync(db, branch.BranchId, clock, reports: 1);

        var asOwner = fixture.CreateReviewModerationService(db, clock, TestActor.Owner(owner.StaffMemberId));
        await Assert.ThrowsAsync<StaffPermissionException>(() => asOwner.DismissReportsForPlatformAsync(reviewId));

        // A token that says platform admin over a row that is not one is refused as well.
        var impostor = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(owner.StaffMemberId));
        await Assert.ThrowsAsync<StaffPermissionException>(() => impostor.DismissReportsForPlatformAsync(reviewId));

        await using (var verify = fixture.CreateContext(clock))
        {
            Assert.False(await verify.BranchReviewReports.AnyAsync(r => r.ReviewId == reviewId && r.DismissedAtUtc != null));
        }

        var platform = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => platform.DismissReportsForPlatformAsync(Guid.NewGuid()));
    }

    [SkippableFact]
    public async Task Restoring_undoes_the_last_dismissal_once_and_nothing_older()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var (reviewId, _) = await ReportedReviewAsync(db, branch.BranchId, clock, reports: 2);
        var moderation = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        // Dismissed, then reported again by somebody new, and dismissed again.
        await moderation.DismissReportsForPlatformAsync(reviewId);
        clock.Advance(TimeSpan.FromMinutes(1));
        var late = await ReviewTestData.SeedDinerAsync(db, "Late Reporter", clock.UtcNow);
        db.BranchReviewReports.Add(new BranchReviewReport(reviewId, late, ReviewReportReasons.Spam, null, clock.UtcNow));
        await db.SaveChangesAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await moderation.DismissReportsForPlatformAsync(reviewId);

        // The undo brings back the last dismissal's one report, not the first dismissal's two.
        clock.Advance(TimeSpan.FromSeconds(5));
        var restored = await moderation.RestoreReportsForPlatformAsync(reviewId);
        Assert.Equal(1, restored.ReportCount);
        Assert.Equal(1, Assert.Single((await moderation.ListForPlatformAsync(branch.BranchId, new ReviewModerationQuery(Filter: "reported"))).Items).ReportCount);

        await using (var verify = fixture.CreateContext(clock))
        {
            var rows = await verify.BranchReviewReports.AsNoTracking().Where(r => r.ReviewId == reviewId).ToListAsync();
            Assert.Null(Assert.Single(rows, r => r.DinerUserId == late).DismissedAtUtc);
            Assert.All(rows.Where(r => r.DinerUserId != late), r => Assert.NotNull(r.DismissedAtUtc));

            var audit = await verify.PlatformAuditLogs.AsNoTracking()
                .SingleAsync(l => l.TargetId == reviewId && l.Action == ReviewModerationService.RestoreReportsAuditAction);
            Assert.Contains("\"restored\":1", audit.ChangesJson, StringComparison.Ordinal);
            Assert.Equal(branch.VenueId, audit.VenueId);
        }

        // Once: a second undo does not reach back to the first dismissal.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, (await moderation.RestoreReportsForPlatformAsync(reviewId)).ReportCount);

        await using (var verify = fixture.CreateContext(clock))
        {
            Assert.Equal(1, await verify.PlatformAuditLogs.CountAsync(
                l => l.TargetId == reviewId && l.Action == ReviewModerationService.RestoreReportsAuditAction));
        }

        // A dismissal after the undo is undoable in its turn.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, (await moderation.DismissReportsForPlatformAsync(reviewId)).ReportCount);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, (await moderation.RestoreReportsForPlatformAsync(reviewId)).ReportCount);
    }

    [SkippableFact]
    public async Task Restoring_a_review_nobody_dismissed_writes_nothing_and_only_the_platform_may()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        var (reviewId, _) = await ReportedReviewAsync(db, branch.BranchId, clock, reports: 2);

        var platform = fixture.CreateReviewModerationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        Assert.Equal(2, (await platform.RestoreReportsForPlatformAsync(reviewId)).ReportCount);

        await using (var verify = fixture.CreateContext(clock))
        {
            Assert.False(await verify.PlatformAuditLogs.AnyAsync(l => l.TargetId == reviewId));
        }

        var asOwner = fixture.CreateReviewModerationService(db, clock, TestActor.Owner(owner.StaffMemberId));
        await Assert.ThrowsAsync<StaffPermissionException>(() => asOwner.RestoreReportsForPlatformAsync(reviewId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => platform.RestoreReportsForPlatformAsync(Guid.NewGuid()));
    }

    /// <summary>A published review at the branch with <paramref name="reports"/> diners' reports on it.</summary>
    internal static async Task<(Guid ReviewId, List<Guid> Reporters)> ReportedReviewAsync(
        Yalla.Infrastructure.Persistence.YallaDbContext db,
        Guid branchId,
        TestClock clock,
        int reports)
    {
        var author = await ReviewTestData.SeedDinerAsync(db, "Review Author", clock.UtcNow);
        var reviewId = await ReviewTestData.SeedReviewAsync(db, branchId, author, 1, "Rude and slow.", clock.UtcNow);
        var reporters = new List<Guid>();

        for (var i = 0; i < reports; i++)
        {
            var reporter = await ReviewTestData.SeedDinerAsync(db, $"Reporter {i}", clock.UtcNow);
            db.BranchReviewReports.Add(new BranchReviewReport(reviewId, reporter, ReviewReportReasons.Offensive, null, clock.UtcNow.AddSeconds(i)));
            reporters.Add(reporter);
        }

        await db.SaveChangesAsync();

        return (reviewId, reporters);
    }
}
