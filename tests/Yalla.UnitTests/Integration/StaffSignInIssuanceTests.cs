using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Giving a manager or owner their admin-panel sign-in: the address is typed by the person
/// issuing it, the password only ever by its holder, through a link that exists once.
/// </summary>
/// <remarks>
/// <para>
/// Every test here hands the link the staff service returned to the <i>real</i> password-reset
/// consumer, because that hand-off is the feature: a link whose hash the reset endpoint cannot
/// find is a manager with an address and no way in, which is exactly the state this exists to
/// end. Nothing below the two services put together can see that.
/// </para>
/// <para>
/// What is pinned as hard as the happy path is what the server keeps: the token's hash and a row
/// saying who gave whom a way in - and never the token, its hash or the link, in the audit row or
/// in a log line. A copy anywhere on the server is a copy somebody with read access can use.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class StaffSignInIssuanceTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);

    private const string Password = "a-perfectly-long-password";

    /// <summary>
    /// The address is stored the way sign-in reads it, no password appears, exactly one live
    /// token is written as a hash, and the audit row names everything except the credential.
    /// </summary>
    [SkippableFact]
    public async Task An_owner_issues_a_managers_sign_in_and_the_link_is_the_only_copy()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await SeedOwnerAsync(db, branch.VenueId);

        var log = new CapturingLogger();
        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id), log);

        var typed = $"  New.Manager-{Guid.NewGuid():N}@Example.Test ";
        var expected = typed.Trim().ToLowerInvariant();

        var issued = await asOwner.IssueSignInAsync(branch.VenueId, branch.ManagerId, new IssueSignInCommand(typed));

        Assert.Equal(branch.ManagerId, issued.StaffMemberId);
        Assert.Equal(expected, issued.Email);
        Assert.False(issued.ReplacedExistingSignIn);
        Assert.Equal(Now.Add(PasswordResetToken.InvitationLifetime), issued.ExpiresAtUtc);

        var plain = TokenFrom(issued.ResetLink);
        var hash = Secrets.Hash(plain);

        await using var verify = fixture.CreateContext(clock);

        var manager = await verify.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == branch.ManagerId);

        Assert.Equal(expected, manager.Email);
        Assert.Null(manager.PasswordHash);
        Assert.False(manager.HasPasswordCredentials);

        var token = await verify.PasswordResetTokens.AsNoTracking()
            .SingleAsync(t => t.StaffMemberId == branch.ManagerId && t.ConsumedAtUtc == null);

        Assert.Equal(hash, token.TokenHash);
        Assert.Equal(Now.AddHours(24), token.ExpiresAtUtc);

        var audit = await verify.PlatformAuditLogs.AsNoTracking()
            .SingleAsync(l => l.TargetId == branch.ManagerId && l.Action == "staff.sign-in-issued");

        Assert.Equal(owner.Id, audit.ActorStaffMemberId);
        Assert.Contains(expected, audit.ChangesJson, StringComparison.Ordinal);
        Assert.Contains("\"replacedExistingSignIn\":false", audit.ChangesJson, StringComparison.Ordinal);

        // The row is what somebody with read access to the log can see, so it carries nothing
        // that would let them in.
        Assert.DoesNotContain(plain, audit.ChangesJson, StringComparison.Ordinal);
        Assert.DoesNotContain(hash, audit.ChangesJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(issued.ResetLink, audit.ChangesJson, StringComparison.Ordinal);

        // And neither does any log line - production logs would keep it for a year.
        Assert.NotEmpty(log.Lines);
        Assert.All(log.Lines, line =>
        {
            Assert.DoesNotContain(plain, line, StringComparison.Ordinal);
            Assert.DoesNotContain(hash, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(issued.ResetLink, line, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The link the owner was given is the one the reset endpoint consumes: it sets a password of
    /// the manager's choosing, sign-in works with it, and the link is spent.
    /// </summary>
    [SkippableFact]
    public async Task The_link_sets_a_password_once_and_the_person_signs_in_with_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await SeedOwnerAsync(db, branch.VenueId);
        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id));
        var auth = fixture.CreateVenueUserAuthService(db, clock);

        var email = Address("consume");
        var issued = await asOwner.IssueSignInAsync(branch.VenueId, branch.ManagerId, new IssueSignInCommand(email));
        var plain = TokenFrom(issued.ResetLink);

        // Before the link is used there is no way in: the address alone is not a sign-in.
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.SignInAsync(email, Password));

        await auth.ResetPasswordAsync(plain, Password);

        var signedIn = await auth.SignInAsync(email, Password);

        Assert.Equal(branch.ManagerId, signedIn.StaffMemberId);
        Assert.Equal(StaffRole.Manager, signedIn.Role);

        var reused = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => auth.ResetPasswordAsync(plain, "another-long-enough-password"));

        Assert.Equal("reset-token-invalid", reused.ReasonCode);

        await using var verify = fixture.CreateContext(clock);
        Assert.True((await verify.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == branch.ManagerId)).HasPasswordCredentials);
    }

    /// <summary>
    /// A hand-delivered link lives a day, not the mailed link's hour: it is opened from a chat
    /// after a shift, not from a screen somebody is sitting at.
    /// </summary>
    [SkippableFact]
    public async Task A_hand_delivered_link_is_good_for_a_day()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await SeedOwnerAsync(db, branch.VenueId);
        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id));
        var auth = fixture.CreateVenueUserAuthService(db, clock);

        var late = TokenFrom((await asOwner.IssueSignInAsync(
            branch.VenueId, branch.ManagerId, new IssueSignInCommand(Address("day")))).ResetLink);

        clock.Advance(TimeSpan.FromHours(25));

        var expired = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => auth.ResetPasswordAsync(late, Password));

        Assert.Equal("reset-token-invalid", expired.ReasonCode);

        var fresh = TokenFrom((await asOwner.IssueSignInAsync(
            branch.VenueId, branch.ManagerId, new IssueSignInCommand(Address("day")))).ResetLink);

        clock.Advance(TimeSpan.FromHours(23));

        // Well past the hour a mailed link would have had, and still good.
        await auth.ResetPasswordAsync(fresh, Password);
    }

    /// <summary>
    /// Sending a new link is how a lost one is taken back - and nothing else moves until the new
    /// one is used: the password they had keeps working, at the address they now have.
    /// </summary>
    [SkippableFact]
    public async Task Issuing_again_retires_the_earlier_link_and_leaves_a_working_password_alone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await AuthTestData.CreateBranchAsync(db);
        var owner = await SeedOwnerAsync(db, branch.VenueId);
        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id));
        var auth = fixture.CreateVenueUserAuthService(db, clock);

        var hashBefore = (await db.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == branch.ManagerId)).PasswordHash;
        var changed = Address("changed");

        var first = await asOwner.IssueSignInAsync(branch.VenueId, branch.ManagerId, new IssueSignInCommand(changed));

        Assert.True(first.ReplacedExistingSignIn);

        // The address moved the moment the owner submitted; the password did not.
        var stillIn = await auth.SignInAsync(changed, AuthTestData.ManagerPassword);
        Assert.Equal(branch.ManagerId, stillIn.StaffMemberId);

        clock.Advance(TimeSpan.FromMinutes(1));

        var second = await asOwner.IssueSignInAsync(branch.VenueId, branch.ManagerId, new IssueSignInCommand(changed));

        var lost = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => auth.ResetPasswordAsync(TokenFrom(first.ResetLink), Password));

        Assert.Equal("reset-token-invalid", lost.ReasonCode);

        await using (var verify = fixture.CreateContext(clock))
        {
            var tokens = await verify.PasswordResetTokens.AsNoTracking()
                .Where(t => t.StaffMemberId == branch.ManagerId)
                .OrderBy(t => t.CreatedAtUtc)
                .ToListAsync();

            Assert.Equal(2, tokens.Count);
            Assert.NotNull(tokens[0].ConsumedAtUtc);
            Assert.Null(tokens[1].ConsumedAtUtc);

            // Retired is not reset: the hash on the row is the one they signed in with a moment ago.
            var manager = await verify.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == branch.ManagerId);
            Assert.Equal(hashBefore, manager.PasswordHash);

            var audits = await verify.PlatformAuditLogs.AsNoTracking()
                .Where(l => l.TargetId == branch.ManagerId && l.Action == "staff.sign-in-issued")
                .OrderBy(l => l.AtUtc)
                .ToListAsync();

            Assert.Equal(2, audits.Count);
            Assert.Contains($"\"emailBefore\":\"{branch.ManagerEmail}\"", audits[0].ChangesJson, StringComparison.Ordinal);
            Assert.Contains($"\"emailBefore\":\"{changed}\"", audits[1].ChangesJson, StringComparison.Ordinal);
        }

        await auth.ResetPasswordAsync(TokenFrom(second.ResetLink), Password);

        // Now the old password is gone and the chosen one is the sign-in.
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.SignInAsync(changed, AuthTestData.ManagerPassword));
        Assert.Equal(branch.ManagerId, (await auth.SignInAsync(changed, Password)).StaffMemberId);
    }

    /// <summary>
    /// A sign-in is the whole account, so only somebody strictly above may hand one out: not a
    /// co-owner, not a fellow manager, and never oneself.
    /// </summary>
    [SkippableFact]
    public async Task A_peer_and_oneself_are_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await SeedOwnerAsync(db, branch.VenueId);
        var coOwner = await SeedOwnerAsync(db, branch.VenueId);

        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id));
        var asManager = fixture.CreateStaffManagementService(db, clock, TestActor.Manager(branch.ManagerId));

        var secondManager = await asOwner.CreateAsync(branch.VenueId, Staff("Second Manager", StaffRole.Manager));

        var peerOwner = await Assert.ThrowsAsync<StaffPermissionException>(
            () => asOwner.IssueSignInAsync(branch.VenueId, coOwner.Id, new IssueSignInCommand(Address("peer"))));
        Assert.Contains("for a Owner", peerOwner.Message, StringComparison.Ordinal);

        var peerManager = await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.IssueSignInAsync(branch.VenueId, secondManager.Id, new IssueSignInCommand(Address("peer"))));
        Assert.Contains("for a Manager", peerManager.Message, StringComparison.Ordinal);

        var self = await Assert.ThrowsAsync<StaffPermissionException>(
            () => asOwner.IssueSignInAsync(branch.VenueId, owner.Id, new IssueSignInCommand(Address("self"))));
        Assert.Contains("your own", self.Message, StringComparison.Ordinal);

        // A platform admin sits above an owner, so the venue's first owner can be given a way in.
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreateStaffManagementService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var issued = await platform.IssueSignInAsync(branch.VenueId, coOwner.Id, new IssueSignInCommand(Address("first-owner")));
        Assert.Equal(coOwner.Id, issued.StaffMemberId);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(0, await verify.PasswordResetTokens.CountAsync(t => t.StaffMemberId == secondManager.Id || t.StaffMemberId == owner.Id));
    }

    /// <summary>
    /// A waiter never holds an address, and a deactivated person is not given a way in until they
    /// are put back - and in neither case does the address get written.
    /// </summary>
    [SkippableFact]
    public async Task A_pin_only_role_and_a_deactivated_person_are_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await SeedOwnerAsync(db, branch.VenueId);
        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id));

        var waiter = await Assert.ThrowsAsync<DomainStateException>(
            () => asOwner.IssueSignInAsync(branch.VenueId, branch.WaiterId, new IssueSignInCommand(Address("waiter"))));

        Assert.Contains("tapping a PIN", waiter.Message, StringComparison.OrdinalIgnoreCase);

        await asOwner.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(IsActive: false));

        var inactive = await Assert.ThrowsAsync<DomainStateException>(
            () => asOwner.IssueSignInAsync(branch.VenueId, branch.ManagerId, new IssueSignInCommand(Address("inactive"))));

        Assert.Contains("deactivated", inactive.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(0, await verify.PasswordResetTokens.CountAsync(t => t.StaffMemberId == branch.WaiterId || t.StaffMemberId == branch.ManagerId));
        Assert.Equal(0, await verify.StaffMembers.CountAsync(s => s.VenueId == branch.VenueId && s.Email != null));
    }

    /// <summary>
    /// One address, one account, across the whole system - and a member who is not in this venue
    /// is not found rather than reached.
    /// </summary>
    [SkippableFact]
    public async Task A_taken_address_an_unknown_member_and_another_venues_member_are_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var mine = await AuthTestData.CreateBranchAsync(db);
        var theirs = await TestBranchBuilder.CreateAsync(db);
        var owner = await SeedOwnerAsync(db, mine.VenueId);
        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id));

        var newcomer = await asOwner.CreateAsync(mine.VenueId, Staff("Newcomer", StaffRole.Manager));

        var taken = await Assert.ThrowsAsync<DomainStateException>(
            () => asOwner.IssueSignInAsync(mine.VenueId, newcomer.Id, new IssueSignInCommand(mine.ManagerEmail)));

        Assert.Contains("already has an account", taken.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => asOwner.IssueSignInAsync(mine.VenueId, Guid.CreateVersion7(), new IssueSignInCommand(Address("nobody"))));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => asOwner.IssueSignInAsync(mine.VenueId, theirs.ManagerId, new IssueSignInCommand(Address("elsewhere"))));

        await using var verify = fixture.CreateContext(clock);

        // The refused save took the token row down with it; nothing half-issued is left behind.
        Assert.Equal(0, await verify.PasswordResetTokens.CountAsync(t => t.StaffMemberId == newcomer.Id || t.StaffMemberId == theirs.ManagerId));
        Assert.Null((await verify.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == newcomer.Id)).Email);
    }

    // ------------------------------------------------------------ helpers

    private static async Task<StaffMember> SeedOwnerAsync(YallaDbContext db, Guid venueId)
    {
        var owner = new StaffMember(venueId, "Founder", $"+374{Random.Shared.Next(10_000_000, 99_999_999)}", StaffRole.Owner, "hash");
        db.StaffMembers.Add(owner);
        await db.SaveChangesAsync();

        return owner;
    }

    private static CreateStaffCommand Staff(string name, StaffRole role) => new(
        FullName: name,
        Phone: $"+374{Random.Shared.Next(10_000_000, 99_999_999)}",
        Role: role,
        Pin: "4321");

    private static string Address(string part) => $"{part}-{Guid.NewGuid():N}@example.test";

    /// <summary>The handle out of the link, the way the console's reset page reads it.</summary>
    private static string TokenFrom(string resetLink)
    {
        const string marker = "#token=";
        var at = resetLink.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(at >= 0, $"The link does not carry the token in its fragment: {resetLink}");

        return Uri.UnescapeDataString(resetLink[(at + marker.Length)..]);
    }

    /// <summary>Keeps every line the service logged, so the test can read what production would keep.</summary>
    private sealed class CapturingLogger : ILogger<StaffManagementService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
