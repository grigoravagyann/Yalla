using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The two account changes that used to decide from a read taken before the account's lock: a password
/// reset from a link, and a phone proof that displaces a registrant.
/// </summary>
/// <remarks>
/// <para>
/// Each test holds the account's lock in a transaction of its own - standing in for a password change,
/// a second use of the link, a registrant setting a password - starts the change under test, checks it
/// is really waiting, and only then makes its own change and commits. A change that read before the lock
/// would be working from the row as it was, and would undo what committed while it waited.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class AccountLockRaceTests(SqlServerFixture fixture)
{
    private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(750);

    [SkippableFact]
    public async Task A_reset_link_spent_while_the_reset_waited_resets_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        var (managerId, link) = await ManagerWithLinkAsync(clock);

        // A password change holds the account: it will spend every unused link.
        await using var changing = fixture.CreateContext(clock);
        await using var transaction = await changing.Database.BeginTransactionAsync();
        await new RefreshTokenStore(changing, clock).LockSubjectAsync(RefreshTokenSubject.VenueUser, managerId, CancellationToken.None);

        await using var resetting = fixture.CreateContext(clock);
        var reset = Task.Run(() => fixture.CreateVenueUserAuthService(resetting, clock).ResetPasswordAsync(link, "a-password-from-the-link"));
        await Task.WhenAny(reset, Task.Delay(LockWait));
        Assert.False(reset.IsCompleted, "The reset did not wait for the account's lock.");

        foreach (var unused in await changing.PasswordResetTokens.Where(t => t.StaffMemberId == managerId && t.ConsumedAtUtc == null).ToListAsync())
        {
            unused.Supersede(clock.UtcNow);
        }

        await changing.SaveChangesAsync();
        await transaction.CommitAsync();

        var refused = Assert.IsType<AuthenticationFailedException>(await Record.ExceptionAsync(() => reset));
        Assert.Equal("reset-token-invalid", refused.ReasonCode);

        // The password is still the one it was.
        Assert.True(await PasswordIsAsync(clock, managerId, AuthTestData.ManagerPassword));
    }

    [SkippableFact]
    public async Task One_link_used_twice_at_once_resets_the_password_once()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        var (managerId, link) = await ManagerWithLinkAsync(clock);

        // Something else holds the account, so both uses arrive and queue.
        await using var holding = fixture.CreateContext(clock);
        await using var transaction = await holding.Database.BeginTransactionAsync();
        await new RefreshTokenStore(holding, clock).LockSubjectAsync(RefreshTokenSubject.VenueUser, managerId, CancellationToken.None);

        await using var first = fixture.CreateContext(clock);
        await using var second = fixture.CreateContext(clock);
        var uses = new[]
        {
            Task.Run(() => fixture.CreateVenueUserAuthService(first, clock).ResetPasswordAsync(link, "the-first-new-password")),
            Task.Run(() => fixture.CreateVenueUserAuthService(second, clock).ResetPasswordAsync(link, "the-second-new-password")),
        };
        await Task.WhenAny(Task.WhenAll(uses), Task.Delay(LockWait));
        Assert.All(uses, use => Assert.False(use.IsCompleted, "A reset did not wait for the account's lock."));

        await transaction.CommitAsync();

        var outcomes = new List<Exception?>();
        foreach (var use in uses)
        {
            outcomes.Add(await Record.ExceptionAsync(() => use));
        }

        Assert.Single(outcomes, o => o is null);
        Assert.Single(outcomes, o => o is AuthenticationFailedException { ReasonCode: "reset-token-invalid" });

        var won = outcomes[0] is null ? "the-first-new-password" : "the-second-new-password";
        Assert.True(await PasswordIsAsync(clock, managerId, won));
    }

    [SkippableFact]
    public async Task A_phone_proof_that_waited_on_a_registrant_ends_the_session_they_started_meanwhile()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        var phone = ReviewTestData.NextPhone();
        var unique = Guid.NewGuid().ToString("N")[..12];
        Guid dinerId;

        // Somebody registered with a number that is not theirs: a password, the number unproved.
        await using (var db = fixture.CreateContext(clock))
        {
            var squatter = DinerUser.Register(
                phone, "en", "Squatter", $"squat{unique}", $"squat{unique}@example.test", new SecretHasher().Hash("the-squatters-password"));
            db.DinerUsers.Add(squatter);
            await db.SaveChangesAsync();
            dinerId = squatter.Id;
        }

        // The squatter is setting a new password - which moves the generation to 1 - and holds the account.
        await using var squatting = fixture.CreateContext(clock);
        await using var transaction = await squatting.Database.BeginTransactionAsync();
        await new RefreshTokenStore(squatting, clock).LockSubjectAsync(RefreshTokenSubject.Diner, dinerId, CancellationToken.None);

        // The number's owner proves it now.
        await using var proving = fixture.CreateContext(clock);
        var proof = Task.Run(() => PhoneSignIn(proving, clock).SignInProvedNumberAsync(phone, "hy", null, clock.UtcNow, "code", CancellationToken.None));
        await Task.WhenAny(proof, Task.Delay(LockWait));
        Assert.False(proof.IsCompleted, "The phone proof did not wait for the account's lock.");

        var row = await squatting.DinerUsers.SingleAsync(d => d.Id == dinerId);
        row.SetPassword(new SecretHasher().Hash("a-newer-squatters-password"));
        await squatting.SaveChangesAsync();
        await transaction.CommitAsync();

        var signedIn = await proof;
        Assert.Equal(dinerId, signedIn.DinerUserId);

        // Displaced from where the squatter left it: past generation 1, so the token they minted at 1 is
        // dead, and their password gone.
        await using var verify = fixture.CreateContext(clock);
        var after = await verify.DinerUsers.AsNoTracking().SingleAsync(d => d.Id == dinerId);
        Assert.Equal(2, after.SessionGeneration);
        Assert.Null(after.PasswordHash);
        Assert.NotNull(after.PhoneVerifiedAtUtc);
    }

    [SkippableFact]
    public async Task An_old_link_used_while_a_new_one_is_issued_waits_and_then_resets_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        var (managerId, oldLink) = await ManagerWithLinkAsync(clock);
        Guid venueId;
        Guid ownerId;

        await using (var db = fixture.CreateContext(clock))
        {
            venueId = await db.StaffMembers.Where(s => s.Id == managerId).Select(s => s.VenueId!.Value).SingleAsync();
            ownerId = (await AuthTestData.SeedOwnerAsync(db, venueId)).StaffMemberId;
        }

        // The owner issues a new link. Just before it is written, the old one is used.
        await using var resetting = fixture.CreateContext(clock);
        Task? reset = null;
        var resetFinishedFirst = false;

        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("INSERT INTO [PasswordResetTokens]", StringComparison.Ordinal),
            async () =>
            {
                reset = Task.Run(() => fixture.CreateVenueUserAuthService(resetting, clock).ResetPasswordAsync(oldLink, "a-password-from-the-old-link"));
                await Task.WhenAny(reset, Task.Delay(LockWait));
                resetFinishedFirst = reset.IsCompleted;
            });

        await using var issuing = fixture.CreateContext(clock, race);
        var issued = await fixture.CreateStaffManagementService(issuing, clock, TestActor.Owner(ownerId))
            .IssueSignInAsync(venueId, managerId, new Yalla.Application.Staff.IssueSignInCommand($"reissued-{Guid.NewGuid():N}@example.test"));

        Assert.True(race.Fired);
        Assert.False(resetFinishedFirst, "The old link's reset did not wait for the reissue holding the account.");

        // One outcome: the reissue retired the old link, so the reset that waited for it finds it spent.
        var refused = Assert.IsType<AuthenticationFailedException>(await Record.ExceptionAsync(() => reset!));
        Assert.Equal("reset-token-invalid", refused.ReasonCode);
        Assert.True(await PasswordIsAsync(clock, managerId, AuthTestData.ManagerPassword));

        // And the new link is the one that works.
        await using var redeeming = fixture.CreateContext(clock);
        var token = StaffSignInIssuanceTests.TokenFrom(issued.ResetLink);
        await fixture.CreateVenueUserAuthService(redeeming, clock).ResetPasswordAsync(token, "a-password-from-the-new-link");
        Assert.True(await PasswordIsAsync(clock, managerId, "a-password-from-the-new-link"));
    }

    // ------------------------------------------------------------ helpers

    private async Task<(Guid ManagerId, string Link)> ManagerWithLinkAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var branch = await AuthTestData.CreateBranchAsync(db);

        var link = Secrets.NewOpaqueToken();
        db.PasswordResetTokens.Add(new PasswordResetToken(branch.ManagerId, Secrets.Hash(link), clock.UtcNow));
        await db.SaveChangesAsync();

        return (branch.ManagerId, link);
    }

    private async Task<bool> PasswordIsAsync(TestClock clock, Guid staffMemberId, string password)
    {
        await using var verify = fixture.CreateContext(clock);
        var hash = await verify.StaffMembers.AsNoTracking().Where(s => s.Id == staffMemberId).Select(s => s.PasswordHash).SingleAsync();

        return new SecretHasher().Verify(hash!, password).Matches;
    }

    private DinerPhoneSignIn PhoneSignIn(YallaDbContext db, TestClock clock) =>
        new(
            db,
            new TokenIssuer(
                Microsoft.Extensions.Options.Options.Create(new JwtOptions
                {
                    SigningKey = "test-signing-key-that-is-comfortably-longer-than-thirty-two-bytes",
                }),
                clock),
            new RefreshTokenStore(db, clock),
            fixture.CreateTokenAuthority(db, clock),
            NullLogger<DinerPhoneSignIn>.Instance);
}
