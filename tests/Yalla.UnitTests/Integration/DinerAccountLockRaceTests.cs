using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Application.Media;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Media;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// One account on two phones: a heart, a report, a review, a profile edit, a password, a picture or a
/// booking sent while the other phone deletes the account; an order marked ready in that moment; and
/// two hearts racing the favourites limit.
/// </summary>
/// <remarks>
/// A request from the second phone can be past the token check - cached for five seconds, per process -
/// when the first phone deletes the account, and two taps can both count before either inserts. Neither
/// happens on a sequential test, so an interceptor makes the competing write happen at the worst moment,
/// every run. Where the fix makes that write wait, it is started and given up on after
/// <see cref="LockWait"/>, and awaited once the first write is done.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DinerAccountLockRaceTests(SqlServerFixture fixture) : IDisposable
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    private readonly string root = Path.Combine(Path.GetTempPath(), "yalla-lock-race-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_heart_sent_while_the_account_is_deleted_is_refused_and_leaves_nothing_on_the_tombstone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        AuthBranch branch;
        Guid dinerUserId;
        await using (var db = fixture.CreateContext(clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            dinerUserId = await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", clock.UtcNow);
        }

        // Phone B's heart has been authenticated; phone A's deletion commits before B touches the database.
        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("FROM [Branches]", StringComparison.OrdinalIgnoreCase),
            () => EraseAsync(clock, dinerUserId));

        await using var phoneB = fixture.CreateContext(clock, race);

        var refused = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => Favorites(phoneB, clock, dinerUserId).AddAsync(branch.BranchId));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        Assert.Equal("session-revoked", refused.ReasonCode);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.DinerFavorites.AnyAsync(f => f.DinerUserId == dinerUserId));
    }

    [SkippableFact]
    public async Task A_report_filed_while_the_account_is_deleted_is_refused_and_leaves_nothing_on_the_tombstone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        Guid reporterId;
        Guid reviewId;
        await using (var db = fixture.CreateContext(clock))
        {
            var branch = await AuthTestData.CreateBranchAsync(db);
            var authorId = await ReviewTestData.SeedDinerAsync(db, "Narek Petrosyan", clock.UtcNow);
            reporterId = await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", clock.UtcNow);
            reviewId = await ReviewTestData.SeedReviewAsync(db, branch.BranchId, authorId, 1, "Cold soup.", clock.UtcNow);
        }

        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("FROM [BranchReviews]", StringComparison.OrdinalIgnoreCase),
            () => EraseAsync(clock, reporterId));

        await using var phoneB = fixture.CreateContext(clock, race);
        var reviews = new BranchReviewService(
            phoneB, clock, TestActor.Diner(reporterId), NullLogger<BranchReviewService>.Instance);

        var refused = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => reviews.ReportAsync(reviewId, new ReportReviewCommand("not-a-visit", "My neighbour, never went.")));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        Assert.Equal("session-revoked", refused.ReasonCode);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.BranchReviewReports.AnyAsync(r => r.DinerUserId == reporterId));
    }

    [SkippableFact]
    public async Task Two_hearts_racing_at_499_leave_500_and_the_later_one_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        AuthBranch home;
        Guid sibling;
        Guid dinerUserId;
        await using (var db = fixture.CreateContext(clock))
        {
            home = await AuthTestData.CreateBranchAsync(db);
            sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 1);
            dinerUserId = await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", clock.UtcNow);

            db.DinerFavorites.AddRange(
                Enumerable.Range(0, DinerFavorite.MaxPerDiner - 1)
                    .Select(_ => new DinerFavorite(dinerUserId, Guid.CreateVersion7(), clock.UtcNow.AddDays(-1))));

            await db.SaveChangesAsync();
        }

        Task? otherPhone = null;

        // Phone A has counted 499 and is about to insert. Phone B hearts a different place now.
        var race = new BeforeFirstInsertInto("DinerFavorites", async () =>
        {
            otherPhone = Task.Run(async () =>
            {
                await using var otherDb = fixture.CreateContext(clock);
                await Favorites(otherDb, clock, dinerUserId).AddAsync(sibling);
            });

            await Task.WhenAny(otherPhone, Task.Delay(LockWait));
        });

        await using var phoneA = fixture.CreateContext(clock, race);
        var first = await Record.ExceptionAsync(() => Favorites(phoneA, clock, dinerUserId).AddAsync(home.BranchId));

        Assert.True(race.Fired, "The other heart never raced this one, so this proves nothing.");
        var second = await Record.ExceptionAsync(() => otherPhone!);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(DinerFavorite.MaxPerDiner, await verify.DinerFavorites.CountAsync(f => f.DinerUserId == dinerUserId));

        Assert.Null(first);
        Assert.IsType<DomainStateException>(second);
    }

    [SkippableFact]
    public async Task A_review_written_while_the_account_is_deleted_goes_with_the_account()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var (branch, dinerUserId) = await SeedVisitorAsync(clock);

        // Phone B has passed the phone gate and the visit check and is inserting; phone A deletes now.
        var deletion = new DeletionMidWrite(() => EraseAsync(clock, dinerUserId));
        var race = new BeforeFirstInsertInto("BranchReviews", deletion.StartAsync);

        await using var phoneB = fixture.CreateContext(clock, race);
        var written = await Record.ExceptionAsync(
            () => Reviews(phoneB, clock, dinerUserId).CreateAsync(branch.BranchId, new SubmitBranchReviewCommand(4, "Warm lavash.")));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        await deletion.FinishedAsync();
        Assert.Null(written);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.BranchReviews.AnyAsync(r => r.DinerUserId == dinerUserId));
    }

    [SkippableFact]
    public async Task A_first_review_put_while_the_account_is_deleted_goes_with_the_account()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var (branch, dinerUserId) = await SeedVisitorAsync(clock);

        var deletion = new DeletionMidWrite(() => EraseAsync(clock, dinerUserId));
        var race = new BeforeFirstInsertInto("BranchReviews", deletion.StartAsync);

        await using var phoneB = fixture.CreateContext(clock, race);
        var written = await Record.ExceptionAsync(
            () => Reviews(phoneB, clock, dinerUserId).UpsertAsync(branch.BranchId, new SubmitBranchReviewCommand(5)));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        await deletion.FinishedAsync();
        Assert.Null(written);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.BranchReviews.AnyAsync(r => r.DinerUserId == dinerUserId));
    }

    [SkippableFact]
    public async Task A_profile_edit_saved_while_the_account_is_deleted_leaves_nothing_on_the_tombstone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var dinerUserId = await SeedDinerAsync(clock);
        var suffix = Guid.NewGuid().ToString("N")[..10];

        // Phone B has read the row and is saving the new name; phone A deletes now.
        var deletion = new DeletionMidWrite(() => EraseAsync(clock, dinerUserId));
        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [DinerUsers]", StringComparison.OrdinalIgnoreCase), deletion.StartAsync);

        await using var phoneB = fixture.CreateContext(clock, race);
        var saved = await Record.ExceptionAsync(() => Profile(phoneB, clock, dinerUserId).UpdateAsync(
            new UpdateDinerProfileCommand("Ani Newname", $"ani_{suffix}", $"ani-{suffix}@example.test")));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        await deletion.FinishedAsync();
        Assert.Null(saved);

        await AssertEmptyTombstoneAsync(clock, dinerUserId);
    }

    [SkippableFact]
    public async Task A_password_set_while_the_account_is_deleted_leaves_no_hash_on_the_tombstone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var dinerUserId = await SeedDinerAsync(clock);

        var deletion = new DeletionMidWrite(() => EraseAsync(clock, dinerUserId));
        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [DinerUsers]", StringComparison.OrdinalIgnoreCase), deletion.StartAsync);

        await using var phoneB = fixture.CreateContext(clock, race);
        var set = await Record.ExceptionAsync(() => Profile(phoneB, clock, dinerUserId).SetPasswordAsync(
            currentPassword: null, "khachapuri-2026", tokenSessionGeneration: 0, tokenRefreshChainId: null));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        await deletion.FinishedAsync();
        Assert.Null(set);

        await AssertEmptyTombstoneAsync(clock, dinerUserId);
    }

    [SkippableFact]
    public async Task A_picture_uploaded_while_the_account_is_deleted_is_deleted_with_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var dinerUserId = await SeedDinerAsync(clock);
        var storage = Storage();

        // Phone B has stored the files and is inserting the row; phone A deletes now.
        var deletion = new DeletionMidWrite(() => EraseAsync(clock, dinerUserId, storage));
        var race = new BeforeFirstInsertInto("Photos", deletion.StartAsync);

        await using var phoneB = fixture.CreateContext(clock, race);
        await using var content = new MemoryStream(Png());
        var uploaded = await Record.ExceptionAsync(
            () => Profile(phoneB, clock, dinerUserId, storage).SetPhotoAsync(content, "image/png"));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        await deletion.FinishedAsync();
        Assert.Null(uploaded);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.Photos.AnyAsync(p => p.DinerUserId == dinerUserId));
        await AssertEmptyTombstoneAsync(clock, dinerUserId);
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public async Task A_picture_processed_while_the_account_was_deleted_is_refused_and_its_files_removed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var dinerUserId = await SeedDinerAsync(clock);
        var storage = Storage();

        // The deletion commits while phone B is encoding the picture, before B takes the account's lock.
        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("WITH (UPDLOCK, HOLDLOCK)", StringComparison.OrdinalIgnoreCase),
            () => EraseAsync(clock, dinerUserId, storage));

        await using var phoneB = fixture.CreateContext(clock, race);
        await using var content = new MemoryStream(Png());

        var refused = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => Profile(phoneB, clock, dinerUserId, storage).SetPhotoAsync(content, "image/png"));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        Assert.Equal("session-revoked", refused.ReasonCode);
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public async Task A_booking_made_while_the_account_is_deleted_keeps_no_link_to_it_and_no_feed_entry()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // 12:00 in Yerevan, booking three days out at 18:00: past the lead time, and the reminder is due later.
        var clock = new TestClock(new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc));
        TestBranch branch;
        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
        }

        var dinerUserId = await SeedDinerAsync(clock);

        // Phone B holds the table and has re-checked the slot, and is inserting; phone A deletes now.
        var deletion = new DeletionMidWrite(() => EraseAsync(clock, dinerUserId));
        var race = new BeforeFirstInsertInto("Reservations", deletion.StartAsync);

        await using var phoneB = fixture.CreateContext(clock, race);
        var booked = await Record.ExceptionAsync(() => fixture
            .CreateReservationService(phoneB, clock, TestActor.Diner(dinerUserId))
            .CreateAsync(new CreateReservationCommand(
                branch.BranchId,
                branch.FirstTableId,
                new DateOnly(2026, 9, 13),
                new TimeOnly(18, 0),
                PartySize: 2,
                GuestName: "Ani Twophones",
                GuestPhone: "+37411223344",
                ClientCommandId: Guid.CreateVersion7())));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        await deletion.FinishedAsync();
        Assert.Null(booked);

        // The venue's booking stays, cut loose from the person, and nothing of it is in their feed.
        await using var verify = fixture.CreateContext(clock);
        Assert.True(await verify.Reservations.AnyAsync(r => r.BranchId == branch.BranchId && r.DinerUserId == null));
        Assert.False(await verify.Reservations.AnyAsync(r => r.DinerUserId == dinerUserId));
        Assert.False(await verify.DinerNotifications.AnyAsync(n => n.DinerUserId == dinerUserId));
    }

    [SkippableFact]
    public async Task An_order_marked_ready_while_the_account_is_deleted_leaves_no_feed_entry()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        AuthBranch branch;
        Guid dinerUserId;
        Guid orderId;
        await using (var db = fixture.CreateContext(clock))
        {
            var now = clock.UtcNow;
            branch = await AuthTestData.CreateBranchAsync(db);
            var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            dinerUserId = await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", now);
            var tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.FirstTableId, now);

            var participant = TabParticipant.Guest(tab.TabId, "Ani", $"device-{Guid.NewGuid():N}", now, false, dinerUserId);
            participant.Approve(now);
            db.TabParticipants.Add(participant);

            var order = TabOrder.PlacedByDiner(tab.TabId, participant.Id, now);
            order.AddLine(menu.Coffee, "Flat white", TestMenu.CoffeeAmd, 1);
            db.TabOrders.Add(order);

            await db.SaveChangesAsync();

            await db.Branches
                .Where(b => b.Id == branch.BranchId)
                .ExecuteUpdateAsync(set => set.SetProperty(b => b.NotifyOnOrderReady, true));

            orderId = order.Id;
        }

        await using (var kitchenDb = fixture.CreateContext(clock))
        {
            await fixture.CreateOrderService(kitchenDb, clock, TestActor.Waiter(branch.WaiterId))
                .MoveOrderStatusAsync(orderId, TabOrderStatus.InKitchen);
        }

        // The floor has read whose order it is and is saving "ready"; the diner deletes the account now.
        var deletion = new DeletionMidWrite(() => EraseAsync(clock, dinerUserId));
        var race = new BeforeFirstInsertInto("DinerNotifications", deletion.StartAsync);

        await using var floor = fixture.CreateContext(clock, race);
        var moved = await Record.ExceptionAsync(() => fixture
            .CreateOrderService(floor, clock, TestActor.Waiter(branch.WaiterId))
            .MoveOrderStatusAsync(orderId, TabOrderStatus.Ready));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        await deletion.FinishedAsync();
        Assert.Null(moved);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.DinerNotifications.AnyAsync(n => n.DinerUserId == dinerUserId));
    }

    /// <summary>Phone A's deletion, committed in full on its own connection.</summary>
    /// <param name="clock">The test's clock.</param>
    /// <param name="dinerUserId">The account.</param>
    /// <param name="storage">Where its pictures are; without it the account owns none and the photo service is never reached.</param>
    private async Task EraseAsync(TestClock clock, Guid dinerUserId, IPhotoStorage? storage = null)
    {
        await using var db = fixture.CreateContext(clock);
        var diner = await db.DinerUsers.SingleAsync(d => d.Id == dinerUserId);

        IPhotoService photos = storage is null
            ? null!
            : new PhotoService(
                db, storage, clock, fixture.CreateBranchGuard(db, TestActor.Diner(dinerUserId)), NullLogger<PhotoService>.Instance);

        await DinerAccountDeletion.EraseAsync(
            db, photos, new RefreshTokenStore(db, clock), diner, clock.UtcNow, CancellationToken.None);
    }

    /// <summary>
    /// Phone A's deletion started in the middle of phone B's write, and given <see cref="LockWait"/>
    /// before B's write goes on. Without the account lock it commits first; with it, it waits for B's
    /// commit and is awaited by <see cref="FinishedAsync"/>.
    /// </summary>
    private sealed class DeletionMidWrite(Func<Task> erase)
    {
        private Task? running;

        public async Task StartAsync()
        {
            running = Task.Run(erase);
            await Task.WhenAny(running, Task.Delay(LockWait));
        }

        public Task FinishedAsync() => running ?? Task.CompletedTask;
    }

    private async Task<Guid> SeedDinerAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        return await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", clock.UtcNow);
    }

    /// <summary>A branch, and a diner with a visit there - what a first review needs.</summary>
    private async Task<(AuthBranch Branch, Guid DinerUserId)> SeedVisitorAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var branch = await AuthTestData.CreateBranchAsync(db);
        var dinerUserId = await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", clock.UtcNow);
        await ReviewTestData.SeedTabVisitAsync(db, branch, clock.UtcNow, dinerUserId);

        return (branch, dinerUserId);
    }

    /// <summary>Nothing of the person left on the account row.</summary>
    private async Task AssertEmptyTombstoneAsync(TestClock clock, Guid dinerUserId)
    {
        await using var verify = fixture.CreateContext(clock);
        var row = await verify.DinerUsers.AsNoTracking().SingleAsync(d => d.Id == dinerUserId);

        Assert.True(row.IsDeleted);
        Assert.Null(row.DisplayName);
        Assert.Null(row.Username);
        Assert.Null(row.Email);
        Assert.Null(row.PasswordHash);
        Assert.Null(row.PhotoId);
    }

    private static BranchReviewService Reviews(YallaDbContext db, IClock clock, Guid dinerUserId) =>
        new(db, clock, TestActor.Diner(dinerUserId), NullLogger<BranchReviewService>.Instance);

    /// <summary>The profile service; the attempt limiter is only for deleting, which these tests do not call.</summary>
    private DinerProfileService Profile(YallaDbContext db, IClock clock, Guid dinerUserId, IPhotoStorage? storage = null) =>
        new(
            db,
            clock,
            TestActor.Diner(dinerUserId),
            storage!,
            photos: null!,
            new SecretHasher(),
            new RefreshTokenStore(db, clock),
            attemptLimiter: null!,
            fixture.CreateTokenAuthority(db, clock),
            NullLogger<DinerProfileService>.Instance);

    private IPhotoStorage Storage() =>
        new LocalDiskPhotoStorage(new PhotoStorageOptions { RootPath = root }, NullLogger<LocalDiskPhotoStorage>.Instance);

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(32, 32);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(120, 60, 90));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Adding a heart never reads the public card, which is all the listing query is for.</summary>
    private static DinerFavoriteService Favorites(YallaDbContext db, IClock clock, Guid dinerUserId) =>
        new(db, clock, TestActor.Diner(dinerUserId), listings: null!, NullLogger<DinerFavoriteService>.Instance);
}
