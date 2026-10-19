using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Staff accounts within a venue, with the role-escalation guards enforced from the acting staff
/// member's stored row rather than from the token alone.
/// </summary>
/// <remarks>
/// Two checks on every write, both from stored rows: <b>rank</b> (<see cref="StaffRoleRules"/>) and
/// <b>reach</b>. An owner, a general manager and a platform admin reach every branch of the venue; a
/// branch manager reaches their own branch's people and nobody else's, and may only put the people
/// they create there. See <see cref="RequireReach"/>.
/// </remarks>
internal sealed class StaffManagementService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    SecretHasher hasher,
    RefreshTokenStore accountLock,
    IOptions<AuthOptions> authOptions,
    ILogger<StaffManagementService> logger) : IStaffManagementService
{
    /// <summary>The audit action for adding somebody to a venue's staff.</summary>
    public const string CreateAuditAction = "staff.create";

    /// <summary>The audit action for handing somebody a sign-in link.</summary>
    public const string SignInIssuedAuditAction = "staff.sign-in-issued";

    private const string StaffTargetType = "StaffMember";

    private readonly AuthOptions _options = authOptions.Value;

    public async Task<IReadOnlyList<StaffMemberView>> ListAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        var acting = await RequireActorInVenueAsync("List staff", venueId, cancellationToken);
        var nowUtc = clock.UtcNow;

        var query = db.StaffMembers
            .AsNoTracking()
            .Where(s => s.VenueId == venueId);

        // A branch manager sees their own branch's people - the same reach they have to change them.
        // Everyone venue-wide sees the venue.
        if (!ReachesWholeVenue(acting))
        {
            query = query.Where(s => s.BranchId == acting.BranchId);
        }

        var rows = await query.ToListAsync(cancellationToken);

        // Seniority, not the stored number: GeneralManager is 6 and sorts after Kitchen by value.
        return rows
            .OrderBy(s => StaffRoleRules.RankOf(s.Role))
            .ThenBy(s => s.FullName, StringComparer.CurrentCulture)
            .Select(s => ToView(s, nowUtc))
            .ToList();
    }

    public async Task<StaffMemberView> CreateAsync(
        Guid venueId,
        CreateStaffCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var acting = await RequireActorInVenueAsync("Create staff", venueId, cancellationToken);

        if (!StaffRoleRules.MayAssign(acting.Role, command.Role))
        {
            throw new StaffPermissionException(
                $"Creating a {command.Role}", acting.Role, RequiredRoleFor(command.Role));
        }

        if (command.BranchId is { } branchId
            && !await db.Branches.AnyAsync(b => b.Id == branchId && b.VenueId == venueId, cancellationToken))
        {
            throw new KeyNotFoundException($"Branch {branchId} does not belong to this venue.");
        }

        // A branch manager adds people to their own branch. A branchless waiter would be on every
        // tablet in the venue, which is further than the manager who made them reaches.
        RequireReach(acting, command.BranchId, $"Creating a {command.Role} at another branch");

        var pin = ValidPin(command.Pin);
        var staff = new StaffMember(
            venueId, command.FullName, command.Phone, command.Role, hasher.Hash(pin), command.BranchId);

        await RequirePinFreeAsync(venueId, command.BranchId, staff.Id, pin, cancellationToken);

        if (command.Email is not null || command.Password is not null)
        {
            if (command.Email is null || command.Password is null)
            {
                throw new ArgumentException("An admin-panel sign-in needs both an email address and a password.", nameof(command));
            }

            RequireCanHoldPassword(command.Role, acting.Role);

            staff.SetPasswordCredentials(command.Email, hasher.Hash(ValidPassword(command.Password)));
        }

        db.StaffMembers.Add(staff);

        // Whoever adds them - an owner, a manager or the platform - so the console's activity feed can
        // say "Aram Hakobyan added Mariam Avetisyan as branch manager". Who and where, never the PIN,
        // the password or its hash.
        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            CreateAuditAction,
            new AuditTarget(StaffTargetType, staff.Id, staff.FullName, venueId),
            new
            {
                role = staff.Role,
                branchId = staff.BranchId,
                canSignIn = staff.HasPasswordCredentials,
            },
            cancellationToken);

        await SaveAsync(cancellationToken);

        logger.LogInformation(
            "Staff member {StaffMemberId} ({Role}) created in venue {VenueId} by {ActorId}.",
            staff.Id, staff.Role, venueId, acting.Id);

        return ToView(staff, clock.UtcNow);
    }

    public async Task<StaffMemberView> UpdateAsync(
        Guid venueId,
        Guid staffMemberId,
        UpdateStaffCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var acting = await RequireActorInVenueAsync("Update staff", venueId, cancellationToken);
        var staff = await LoadStaffAsync(venueId, staffMemberId, cancellationToken);

        // You may touch the people you could have created. A manager editing an owner is refused
        // before any field is read.
        if (staff.Id != acting.Id && !StaffRoleRules.MayManage(acting.Role, staff.Role))
        {
            throw new StaffPermissionException($"Editing a {staff.Role}", acting.Role, RequiredRoleFor(staff.Role));
        }

        if (staff.Id != acting.Id)
        {
            RequireReach(acting, staff.BranchId, $"Editing a {staff.Role} at another branch");
        }

        var newRole = staff.Role;
        var newBranchId = staff.BranchId;

        if (command.Role is { } role && role != staff.Role)
        {
            // Nobody changes their own role, whatever rank they hold.
            if (staff.Id == acting.Id)
            {
                throw new StaffPermissionException("Changing your own role", acting.Role, StaffRole.PlatformAdmin);
            }

            if (!StaffRoleRules.MayAssign(acting.Role, role))
            {
                throw new StaffPermissionException($"Promoting to {role}", acting.Role, RequiredRoleFor(role));
            }

            newRole = role;
        }

        if (command.FullName is { } name)
        {
            staff.Rename(name);
        }

        if (command.Phone is { } phone)
        {
            staff.SetPhone(phone);
        }

        if (command.SetBranch)
        {
            // Nobody widens their own scope. A manager confined to one branch who could clear their
            // own branch would sign in on every branch's tablets, which is the same escalation the
            // own-role rule exists to stop.
            if (staff.Id == acting.Id && staff.BranchId != command.BranchId)
            {
                throw new StaffPermissionException(
                    "Changing your own branch assignment", acting.Role, StaffRole.PlatformAdmin);
            }

            if (command.BranchId is { } branchId
                && !await db.Branches.AnyAsync(b => b.Id == branchId && b.VenueId == venueId, cancellationToken))
            {
                throw new KeyNotFoundException($"Branch {branchId} does not belong to this venue.");
            }

            if (staff.Id != acting.Id)
            {
                RequireReach(acting, command.BranchId, $"Moving a {staff.Role} to another branch");
            }

            newBranchId = command.BranchId;
        }

        // The role and the branch as one pair: promoting a branch manager to general manager clears
        // their branch in the same request, and checking either half alone would refuse it.
        // Confined to one branch they did not work at only before: the shifts they had planned anywhere else stop
        // being theirs to work. Moving to venue-wide covers every branch, so nothing goes.
        var leavesBranches = newBranchId is not null && newBranchId != staff.BranchId;

        if (newRole != staff.Role || newBranchId != staff.BranchId)
        {
            staff.Reassign(newRole, newBranchId);
        }

        if (command.IsActive is { } active)
        {
            if (staff.Id == acting.Id && !active)
            {
                throw new StaffPermissionException("Deactivating your own account", acting.Role, StaffRole.PlatformAdmin);
            }

            staff.SetActive(active);
        }

        // The move and its shifts in one save, under the person's lock - the one shift planning takes - so a shift
        // planned at the old branch while this runs is either seen and removed here or refused there.
        await using var transaction = leavesBranches ? await db.Database.BeginTransactionAsync(cancellationToken) : null;

        var droppedShifts = leavesBranches
            ? await DropFutureShiftsElsewhereAsync(staff.Id, newBranchId!.Value, cancellationToken)
            : 0;

        await SaveAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        if (droppedShifts > 0)
        {
            logger.LogInformation(
                "Staff member {StaffMemberId} moved to branch {BranchId} by {ActorId}; {Count} future shift(s) elsewhere removed.",
                staff.Id, newBranchId, acting.Id, droppedShifts);
        }

        return ToView(staff, clock.UtcNow);
    }

    /// <summary>
    /// Takes <paramref name="staffMemberId"/>'s shifts at every branch but <paramref name="stayingAt"/> off, from each
    /// branch's own service day on. History stays, and so does a shift from before the service day still running.
    /// </summary>
    /// <returns>How many were removed - tracked deletes, committed by the caller's save.</returns>
    private async Task<int> DropFutureShiftsElsewhereAsync(
        Guid staffMemberId,
        Guid stayingAt,
        CancellationToken cancellationToken)
    {
        await accountLock.LockSubjectAsync(RefreshTokenSubject.VenueUser, staffMemberId, cancellationToken);

        var nowUtc = clock.UtcNow;

        // No branch's service day is earlier than two UTC days back, whatever its zone.
        var earliest = DateOnly.FromDateTime(nowUtc).AddDays(-2);

        var shifts = await db.StaffShifts
            .Where(s => s.StaffMemberId == staffMemberId && s.BranchId != stayingAt && s.Date >= earliest)
            .ToListAsync(cancellationToken);

        if (shifts.Count == 0)
        {
            return 0;
        }

        var branchIds = shifts.Select(s => s.BranchId).Distinct().ToList();
        var branches = await db.Branches
            .AsNoTracking()
            .Where(b => branchIds.Contains(b.Id))
            .Select(b => new { b.Id, b.TimeZoneId })
            .ToListAsync(cancellationToken);

        var removed = 0;

        foreach (var branch in branches)
        {
            var (serviceDate, _) = await OpeningCalendars.ServiceDayAsync(
                db, branch.Id, branch.TimeZoneId, nowUtc, cancellationToken);

            foreach (var shift in shifts.Where(s => s.BranchId == branch.Id && s.Date >= serviceDate))
            {
                db.StaffShifts.Remove(shift);
                removed++;
            }
        }

        return removed;
    }

    public async Task<StaffMemberView> SetPinAsync(
        Guid venueId,
        Guid staffMemberId,
        string pin,
        CancellationToken cancellationToken = default)
    {
        var acting = await RequireActorInVenueAsync("Set PIN", venueId, cancellationToken);
        var staff = await LoadStaffAsync(venueId, staffMemberId, cancellationToken);

        if (staff.Id != acting.Id && !StaffRoleRules.MayManage(acting.Role, staff.Role))
        {
            throw new StaffPermissionException($"Resetting a {staff.Role}'s PIN", acting.Role, RequiredRoleFor(staff.Role));
        }

        if (staff.Id != acting.Id)
        {
            RequireReach(acting, staff.BranchId, $"Resetting a {staff.Role}'s PIN at another branch");
        }

        var validPin = ValidPin(pin);
        await RequirePinFreeAsync(venueId, staff.BranchId, staff.Id, validPin, cancellationToken);

        staff.SetPinHash(hasher.Hash(validPin));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("PIN reset for staff member {StaffMemberId} by {ActorId}.", staff.Id, acting.Id);

        return ToView(staff, clock.UtcNow);
    }

    /// <summary>
    /// The address is typed by the person issuing the sign-in; the password never is. The link
    /// this returns is the same single-use handle the forgot-password mail would carry, and the
    /// reset endpoint that consumes it is the only thing that ever sets a password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stricter than editing. A sign-in is the whole account, so it is refused for anyone the
    /// caller does not strictly outrank: an owner may not issue one for a co-owner, because that
    /// is taking over a peer's account and the audit log would then name the wrong person for
    /// whatever came next. Refused for oneself for the same reason in the other direction - the
    /// bearer of a fifteen-minute access token must not be able to turn it into a password.
    /// </para>
    /// <para>
    /// Nothing about the person changes until the link is used. A password they already have keeps
    /// working and their sessions stay open; consumption is what replaces the one and ends the
    /// other. What does change now is that every earlier unused link for them dies, so "send a new
    /// one" is a way to take a lost one back.
    /// </para>
    /// </remarks>
    public async Task<StaffSignInLink> IssueSignInAsync(
        Guid venueId,
        Guid staffMemberId,
        IssueSignInCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var acting = await RequireActorInVenueAsync("Issue a sign-in", venueId, cancellationToken);
        var staff = await LoadStaffAsync(venueId, staffMemberId, cancellationToken);

        // Under the person's account lock, held to the commit - the lock a reset from a link takes before
        // it reads the link. So a reset of the old link either committed first, and there is nothing of
        // it left to retire, or waits and finds it retired: the old link cannot set a password once the
        // new one exists. The row, and the links below, are read again under it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await accountLock.LockSubjectAsync(RefreshTokenSubject.VenueUser, staff.Id, cancellationToken);
        await db.Entry(staff).ReloadAsync(cancellationToken);

        if (staff.Id == acting.Id)
        {
            throw new StaffPermissionException("Issuing your own sign-in", acting.Role, StaffRole.PlatformAdmin);
        }

        RequireCanHoldPassword(staff.Role, acting.Role);

        if (!StaffRoleRules.Outranks(acting.Role, staff.Role))
        {
            throw new StaffPermissionException(
                $"Issuing a sign-in for a {staff.Role}", acting.Role, StaffRole.PlatformAdmin);
        }

        RequireReach(acting, staff.BranchId, $"Issuing a sign-in for a {staff.Role} at another branch");

        if (!staff.IsActive)
        {
            throw new DomainStateException(
                $"{staff.FullName} is deactivated. Reactivate them first, then send a sign-in link.");
        }

        var emailBefore = staff.Email;
        var hadPassword = staff.HasPasswordCredentials;
        staff.SetSignInEmail(command.Email);

        var now = clock.UtcNow;

        var outstanding = await db.PasswordResetTokens
            .Where(t => t.StaffMemberId == staff.Id && t.ConsumedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in outstanding)
        {
            token.Supersede(now);
        }

        var plain = Secrets.NewOpaqueToken();
        var issued = new PasswordResetToken(staff.Id, Secrets.Hash(plain), now, PasswordResetToken.InvitationLifetime);
        db.PasswordResetTokens.Add(issued);

        var link = _options.PasswordResetUrlTemplate.Replace("{token}", Uri.EscapeDataString(plain));

        // Who gave whom a way in, and to which address - never the link, its token or the hash.
        // The row is the durable record; production logs at Error and keep none of the lines below.
        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            SignInIssuedAuditAction,
            new AuditTarget(StaffTargetType, staff.Id, staff.FullName, staff.VenueId),
            new
            {
                email = staff.Email,
                emailBefore,
                expiresAtUtc = issued.ExpiresAtUtc,
                replacedExistingSignIn = hadPassword,
            },
            cancellationToken);

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Sign-in issued for staff member {StaffMemberId} by {ActorId}.", staff.Id, acting.Id);

        // The only time the plaintext exists. Nothing on the server can produce it again, so
        // whoever loses it issues another rather than looking it up.
        return new StaffSignInLink(staff.Id, staff.Email!, link, issued.ExpiresAtUtc, hadPassword);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// The acting staff member's own row: their stored role and venue, not the token's claim.
    /// A platform admin belongs to no venue and may act in any.
    /// </summary>
    private async Task<StaffMember> RequireActorInVenueAsync(string operation, Guid venueId, CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } actingId)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        var acting = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == actingId, cancellationToken)
                     ?? throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);

        if (!acting.IsActive)
        {
            throw new StaffPermissionException(operation, acting.Role, StaffRole.Manager);
        }

        if (acting.IsPlatformAdmin)
        {
            return acting;
        }

        if (acting.VenueId != venueId || !StaffRoleRules.ManagesAtVenue(acting.Role))
        {
            throw new StaffPermissionException(operation, acting.Role, StaffRole.Manager);
        }

        return acting;
    }

    /// <summary>Whether the acting row reaches every branch: the platform tier, an owner, a general manager.</summary>
    private static bool ReachesWholeVenue(StaffMember acting) =>
        acting.IsPlatformAdmin || StaffRoleRules.CoversWholeVenue(acting.Role);

    /// <summary>
    /// Refuses a branch manager acting on somebody - or putting somebody - anywhere but their own
    /// branch. Venue-wide callers pass.
    /// </summary>
    /// <param name="acting">The caller's stored row.</param>
    /// <param name="branchId">The branch the person is at, or is being put at. Null is venue-wide.</param>
    /// <param name="operation">What is being attempted, for the 403.</param>
    /// <exception cref="StaffPermissionException">
    /// A branch manager and a different branch, or none. Names <c>GeneralManager</c> as the role that
    /// would reach it.
    /// </exception>
    private static void RequireReach(StaffMember acting, Guid? branchId, string operation)
    {
        if (ReachesWholeVenue(acting))
        {
            return;
        }

        if (acting.BranchId is not { } home || branchId != home)
        {
            throw new StaffPermissionException(operation, acting.Role, StaffRole.GeneralManager);
        }
    }

    /// <summary>
    /// Refuses <paramref name="pin"/> when anybody else who signs in on the same tablet already taps it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The roster rule is <c>StaffAuthService.SignsInOn</c>'s, seen from the person's side. Somebody at a
    /// branch shares that branch's tablets with its own people and with everyone venue-wide; somebody
    /// venue-wide (no branch on their row) is on every tablet of the venue, so shares one with
    /// everybody. Active people only, and never the person themselves.
    /// </para>
    /// <para>
    /// PINs are salted hashes, so there is nothing to look up: the candidate is verified against each
    /// hash on the roster. A roster is a few dozen people, and this runs when a PIN is set, not on
    /// every sign-in. A hash that is not one this system wrote (the <c>no-pin</c> marker, a fixture's
    /// placeholder) simply does not match.
    /// </para>
    /// </remarks>
    /// <exception cref="PinTakenException">Somebody else on the same tablet uses it.</exception>
    private async Task RequirePinFreeAsync(
        Guid venueId,
        Guid? branchId,
        Guid staffMemberId,
        string pin,
        CancellationToken cancellationToken)
    {
        var sharingATablet = db.StaffMembers
            .AsNoTracking()
            .Where(s => s.VenueId == venueId && s.IsActive && s.Id != staffMemberId && s.PinHash != StaffMember.NoPin);

        if (branchId is { } branch)
        {
            sharingATablet = sharingATablet.Where(s => s.BranchId == null || s.BranchId == branch);
        }

        var hashes = await sharingATablet.Select(s => s.PinHash).ToListAsync(cancellationToken);

        foreach (var hash in hashes)
        {
            if (PinMatches(hash, pin))
            {
                throw new PinTakenException();
            }
        }
    }

    private bool PinMatches(string hash, string pin)
    {
        try
        {
            return hasher.Verify(hash, pin).Matches;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<StaffMember> LoadStaffAsync(Guid venueId, Guid staffMemberId, CancellationToken cancellationToken) =>
        await db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staffMemberId && s.VenueId == venueId, cancellationToken)
        ?? throw new KeyNotFoundException($"Staff member {staffMemberId} was not found in this venue.");

    /// <summary>
    /// Commits, turning the unique-email violation into an answer the panel can show.
    /// </summary>
    /// <remarks>
    /// One address, one account, across the whole system - the sign-in form has no venue field to
    /// disambiguate with. Relying on the index rather than a check-then-insert is what makes two
    /// managers adding the same address at once safe.
    /// </remarks>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.StaffMemberEmail))
        {
            db.ChangeTracker.Clear();

            throw new DomainStateException(
                "That email address already has an account. Every address signs in to one account, "
                + "so use a different one - or edit the existing account instead.");
        }
    }

    /// <summary>
    /// Only the roles that actually use the admin panel may hold an email and password.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. A password mints a <c>VenueUser</c> token, and that identity is venue-scoped
    /// rather than branch-scoped: giving one to a waiter would hand them, off a browser and with no
    /// enrolled tablet, the whole venue that their PIN on the floor deliberately does not reach.
    /// A waiter or kitchen hand taps a PIN on a device a manager enrolled; that is the whole flow.
    /// </remarks>
    private static void RequireCanHoldPassword(StaffRole target, StaffRole actorRole)
    {
        if (!StaffRoleRules.ManagesAtVenue(target))
        {
            // Not a StaffPermissionException: nothing about the caller's rank would help, and
            // saying "requires the Manager role; the caller is a PlatformAdmin" to a platform admin
            // is nonsense. The refusal is about the person being created, not the person creating.
            _ = actorRole;

            throw new DomainStateException(
                $"A {target} signs in by tapping a PIN on a tablet a manager enrolled, so they cannot "
                + "have an email and password. Only an owner, a general manager or a branch manager "
                + "uses the admin panel. Create them without credentials, or make them a manager.");
        }
    }

    /// <summary>The lowest rank that may assign or manage <paramref name="target"/>, for the error message.</summary>
    private static StaffRole RequiredRoleFor(StaffRole target) => target switch
    {
        StaffRole.Owner or StaffRole.GeneralManager => StaffRole.Owner,
        StaffRole.Manager => StaffRole.GeneralManager,
        StaffRole.PlatformAdmin => StaffRole.PlatformAdmin,
        _ => StaffRole.Manager,
    };

    private static string ValidPin(string? pin)
    {
        var value = (pin ?? string.Empty).Trim();

        if (value.Length is < 4 or > 8 || !value.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A PIN is 4 to 8 digits.", nameof(pin));
        }

        return value;
    }

    private string ValidPassword(string password) =>
        password.Length < _options.MinimumPasswordLength
            ? throw new ArgumentException($"Password must be at least {_options.MinimumPasswordLength} characters.", nameof(password))
            : password;

    private static StaffMemberView ToView(StaffMember s, DateTime nowUtc) => new(
        s.Id, s.VenueId, s.BranchId, s.FullName, s.Phone, s.Role, s.IsActive, s.Email,
        s.HasPasswordCredentials, s.IsPinLockedAt(nowUtc));
}
