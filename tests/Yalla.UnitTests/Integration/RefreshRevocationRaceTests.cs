using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A refresh in flight against a revocation of the same account - a password change, signing out
/// everywhere else, a deletion - for a venue user and for a diner, in both orders.
/// </summary>
/// <remarks>
/// <para>
/// The two used to read and write the same rows with nothing between them: a rotation that read the
/// token before a revocation committed went on to mint a successor the revocation never saw, and the
/// sign-in that was meant to end carried on for thirty days. Neither order shows up in a sequential
/// test, so each side is held open in its own transaction while the other runs.
/// </para>
/// <para>
/// Where the fix makes the second side wait, it is started, given <see cref="LockWait"/> to get as far
/// as it can, and awaited once the first side has committed.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class RefreshRevocationRaceTests(SqlServerFixture fixture)
{
    private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(750);

    public static TheoryData<RefreshTokenSubject> Subjects => new() { RefreshTokenSubject.VenueUser, RefreshTokenSubject.Diner };

    [SkippableTheory]
    [MemberData(nameof(Subjects))]
    public async Task A_refresh_that_arrives_during_a_revocation_mints_nothing(RefreshTokenSubject subject)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var (subjectId, presented) = await SignedInAsync(subject, clock);

        // The revocation reads what to end, and has not committed yet.
        await using var revoking = fixture.CreateContext(clock);
        await using var transaction = await revoking.Database.BeginTransactionAsync();
        await new RefreshTokenStore(revoking, clock).RevokeAllForSubjectAsync(subject, subjectId, "password-changed", CancellationToken.None);

        // The refresh comes in now.
        await using var refreshing = fixture.CreateContext(clock);
        var refresh = Task.Run(() => RefreshAsync(refreshing, clock, subject, presented));
        await Task.WhenAny(refresh, Task.Delay(LockWait));

        await revoking.SaveChangesAsync();
        await transaction.CommitAsync();

        var failure = await Record.ExceptionAsync(() => refresh);

        Assert.IsType<AuthenticationFailedException>(failure);
        Assert.Equal(0, await LiveTokensAsync(clock, subject, subjectId));
    }

    [SkippableTheory]
    [MemberData(nameof(Subjects))]
    public async Task A_revocation_that_arrives_during_a_refresh_ends_the_successor_too(RefreshTokenSubject subject)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var (subjectId, presented) = await SignedInAsync(subject, clock);

        // The rotation has written the successor, and has not committed yet.
        await using var refreshing = fixture.CreateContext(clock);
        await using var transaction = await refreshing.Database.BeginTransactionAsync();
        await new RefreshTokenStore(refreshing, clock).RotateAsync(presented, subject, CancellationToken.None);
        await refreshing.SaveChangesAsync();

        // The revocation comes in now.
        await using var revoking = fixture.CreateContext(clock);
        var revoke = Task.Run(async () =>
        {
            await using var own = await revoking.Database.BeginTransactionAsync();
            await new RefreshTokenStore(revoking, clock).RevokeAllForSubjectAsync(subject, subjectId, "password-changed", CancellationToken.None);
            await revoking.SaveChangesAsync();
            await own.CommitAsync();
        });
        await Task.WhenAny(revoke, Task.Delay(LockWait));

        await transaction.CommitAsync();
        await revoke;

        Assert.Equal(0, await LiveTokensAsync(clock, subject, subjectId));
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A venue manager or a diner with one sign-in, and that sign-in's refresh token.</summary>
    private async Task<(Guid SubjectId, string RefreshToken)> SignedInAsync(RefreshTokenSubject subject, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var subjectId = subject == RefreshTokenSubject.VenueUser
            ? (await AuthTestData.CreateBranchAsync(db)).ManagerId
            : await ReviewTestData.SeedDinerAsync(db, "Race Diner", clock.UtcNow);

        var (_, plain) = new RefreshTokenStore(db, clock).Issue(subject, subjectId);
        await db.SaveChangesAsync();

        return (subjectId, plain);
    }

    private static async Task RefreshAsync(YallaDbContext db, TestClock clock, RefreshTokenSubject subject, string presented)
    {
        var refresh = new TokenRefreshService(
            db,
            new TokenIssuer(
                Microsoft.Extensions.Options.Options.Create(new JwtOptions
                {
                    SigningKey = "test-signing-key-that-is-comfortably-longer-than-thirty-two-bytes",
                }),
                clock),
            new RefreshTokenStore(db, clock));

        _ = subject == RefreshTokenSubject.VenueUser
            ? await refresh.RefreshVenueUserAsync(presented)
            : await refresh.RefreshDinerAsync(presented);
    }

    /// <summary>Tokens that could still be exchanged.</summary>
    private async Task<int> LiveTokensAsync(TestClock clock, RefreshTokenSubject subject, Guid subjectId)
    {
        await using var verify = fixture.CreateContext(clock);
        var now = clock.UtcNow;

        return await verify.RefreshTokens.AsNoTracking().CountAsync(t =>
            t.SubjectType == subject && t.SubjectId == subjectId
            && t.RevokedAtUtc == null && t.RotatedAtUtc == null && t.ExpiresAtUtc > now);
    }
}
