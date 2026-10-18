using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Platform;
using Yalla.Domain;

using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The platform admin's own account: reading it, renaming it, changing the password after proving the
/// current one - which signs every other sign-in out and keeps this one - and the seed that never
/// overwrites either on the next start.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformAdminAccountTests(SqlServerFixture fixture)
{
    private const string NewPassword = "a-new-password-long-enough";

    [SkippableFact]
    public async Task The_admin_reads_their_own_account_with_the_configured_address()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var account = fixture.CreatePlatformAdminAccountService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var me = await account.GetAsync();

        Assert.Equal(admin.StaffMemberId, me.StaffMemberId);
        Assert.Equal("Platform Administrator", me.FullName);
        Assert.Equal(admin.Email, me.Email);
    }

    [SkippableFact]
    public async Task Renaming_trims_saves_audits_once_and_refuses_a_blank_or_long_name()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var account = fixture.CreatePlatformAdminAccountService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var renamed = await account.UpdateAsync(new UpdatePlatformAdminProfileCommand("  Grigor Avagyan  "));
        Assert.Equal("Grigor Avagyan", renamed.FullName);

        // The same name again changes nothing and writes nothing.
        await account.UpdateAsync(new UpdatePlatformAdminProfileCommand("Grigor Avagyan"));

        var blank = await Assert.ThrowsAsync<FieldValidationException>(() => account.UpdateAsync(new UpdatePlatformAdminProfileCommand("   ")));
        Assert.Equal("fullName", blank.Field);

        var tooLong = await Assert.ThrowsAsync<FieldValidationException>(
            () => account.UpdateAsync(new UpdatePlatformAdminProfileCommand(new string('x', 201))));
        Assert.Equal("fullName", tooLong.Field);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal("Grigor Avagyan", await verify.StaffMembers.Where(s => s.Id == admin.StaffMemberId).Select(s => s.FullName).FirstAsync());

        var audit = await verify.PlatformAuditLogs.AsNoTracking().SingleAsync(l => l.TargetId == admin.StaffMemberId);
        Assert.Equal("admin.rename", audit.Action);
        Assert.Equal("StaffMember", audit.TargetType);
        Assert.Equal("Grigor Avagyan", audit.TargetDisplay);
        Assert.Null(audit.VenueId);
        Assert.Contains("Platform Administrator", audit.ChangesJson, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Changing_the_password_needs_the_current_one_and_reports_every_problem_at_once()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var account = fixture.CreatePlatformAdminAccountService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var both = await Assert.ThrowsAsync<FieldValidationException>(
            () => account.ChangePasswordAsync(new ChangePlatformAdminPasswordCommand("wrong-password-entirely", "short", null)));

        Assert.Equal(["currentPassword", "newPassword"], both.Violations.Select(v => v.Field));
        Assert.Equal(FieldBounds.Min, both.Violations[1].Bound);
        Assert.Equal(12, both.Violations[1].Min);

        var missing = await Assert.ThrowsAsync<FieldValidationException>(
            () => account.ChangePasswordAsync(new ChangePlatformAdminPasswordCommand(null, NewPassword, null)));

        Assert.Equal("currentPassword", Assert.Single(missing.Violations).Field);
        Assert.Equal(FieldBounds.Required, missing.Violations[0].Bound);

        // Nothing changed: the old password still signs in.
        var signIn = fixture.CreateVenueUserAuthService(db, clock);
        await signIn.SignInAsync(admin.Email, admin.Password);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.PlatformAuditLogs.AnyAsync(l => l.TargetId == admin.StaffMemberId));
    }

    [SkippableFact]
    public async Task A_changed_password_signs_in_the_old_one_does_not_and_only_this_sign_in_survives()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var signIn = fixture.CreateVenueUserAuthService(db, clock);

        // Two sign-ins: this browser, and another one somewhere.
        await signIn.SignInAsync(admin.Email, admin.Password);
        await signIn.SignInAsync(admin.Email, admin.Password);

        var chains = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.SubjectType == RefreshTokenSubject.VenueUser && t.SubjectId == admin.StaffMemberId)
            .Select(t => t.ChainId)
            .ToListAsync();
        Assert.Equal(2, chains.Count);
        var here = chains[0];

        var account = fixture.CreatePlatformAdminAccountService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        await account.ChangePasswordAsync(new ChangePlatformAdminPasswordCommand(admin.Password, NewPassword, here));

        await Assert.ThrowsAsync<AuthenticationFailedException>(() => signIn.SignInAsync(admin.Email, admin.Password));
        await signIn.SignInAsync(admin.Email, NewPassword);

        await using var verify = fixture.CreateContext(clock);
        var tokens = await verify.RefreshTokens.AsNoTracking()
            .Where(t => t.SubjectType == RefreshTokenSubject.VenueUser && t.SubjectId == admin.StaffMemberId && chains.Contains(t.ChainId))
            .ToListAsync();

        Assert.Null(Assert.Single(tokens, t => t.ChainId == here).RevokedAtUtc);
        Assert.Equal("password-changed", Assert.Single(tokens, t => t.ChainId != here).RevokedReason);

        // Audited, and the row says nothing about either password.
        var audit = await verify.PlatformAuditLogs.AsNoTracking().SingleAsync(l => l.TargetId == admin.StaffMemberId);
        Assert.Equal("admin.password-change", audit.Action);
        Assert.DoesNotContain(admin.Password, audit.ChangesJson, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPassword, audit.ChangesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", audit.ChangesJson, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task The_seed_on_the_next_start_leaves_a_changed_name_and_password_alone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var account = fixture.CreatePlatformAdminAccountService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        await account.UpdateAsync(new UpdatePlatformAdminProfileCommand("Grigor Avagyan"));
        await account.ChangePasswordAsync(new ChangePlatformAdminPasswordCommand(admin.Password, NewPassword, null));

        // The next start: the same configured address and the original configured password and name.
        await using (var restart = fixture.CreateContext(clock))
        {
            var seeder = new PlatformAdminSeeder(
                restart,
                new SecretHasher(),
                Microsoft.Extensions.Options.Options.Create(new PlatformAdminOptions
                {
                    Email = admin.Email.ToUpperInvariant(),
                    Password = admin.Password,
                    FullName = "Platform Administrator",
                }),
                NullLogger<PlatformAdminSeeder>.Instance);

            Assert.Equal(admin.StaffMemberId, await seeder.SeedAsync());
        }

        await using var verify = fixture.CreateContext(clock);
        var row = await verify.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == admin.StaffMemberId);
        Assert.Equal("Grigor Avagyan", row.FullName);
        Assert.Equal(StaffRole.PlatformAdmin, row.Role);
        Assert.Equal(1, await verify.StaffMembers.CountAsync(s => s.Email == admin.Email));

        var signIn = fixture.CreateVenueUserAuthService(verify, clock);
        await signIn.SignInAsync(admin.Email, NewPassword);
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => signIn.SignInAsync(admin.Email, admin.Password));
    }

    [SkippableFact]
    public async Task Only_an_active_platform_admin_has_an_account_here()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);

        var asOwner = fixture.CreatePlatformAdminAccountService(db, clock, TestActor.Owner(owner.StaffMemberId));
        await Assert.ThrowsAsync<StaffPermissionException>(() => asOwner.GetAsync());

        // A token claiming the role over a venue owner's row is not a platform admin either.
        var impostor = fixture.CreatePlatformAdminAccountService(db, clock, TestActor.PlatformAdmin(owner.StaffMemberId));
        await Assert.ThrowsAsync<StaffPermissionException>(() => impostor.UpdateAsync(new UpdatePlatformAdminProfileCommand("Taken Over")));
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => impostor.ChangePasswordAsync(new ChangePlatformAdminPasswordCommand(owner.Password, NewPassword, null)));

        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var stored = await db.StaffMembers.FirstAsync(s => s.Id == admin.StaffMemberId);
        stored.SetActive(false);
        await db.SaveChangesAsync();

        var inactive = fixture.CreatePlatformAdminAccountService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        await Assert.ThrowsAsync<StaffPermissionException>(() => inactive.GetAsync());
    }
}
