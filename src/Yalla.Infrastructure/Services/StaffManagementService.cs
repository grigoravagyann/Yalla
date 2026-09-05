using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Staff accounts within a venue, with the role-escalation guards enforced from the acting staff
/// member's stored row rather than from the token alone.
/// </summary>
internal sealed class StaffManagementService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    SecretHasher hasher,
    IOptions<AuthOptions> authOptions,
    ILogger<StaffManagementService> logger) : IStaffManagementService
{
    private readonly AuthOptions _options = authOptions.Value;

    public async Task<IReadOnlyList<StaffMemberView>> ListAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        await RequireActorInVenueAsync("List staff", venueId, cancellationToken);
        var nowUtc = clock.UtcNow;

        var rows = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.VenueId == venueId)
            .OrderBy(s => s.Role)
            .ThenBy(s => s.FullName)
            .ToListAsync(cancellationToken);

        return rows.Select(s => ToView(s, nowUtc)).ToList();
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

        var staff = new StaffMember(
            venueId, command.FullName, command.Phone, command.Role, hasher.Hash(ValidPin(command.Pin)), command.BranchId);

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

            staff.SetRole(role);
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

            staff.AssignToBranch(command.BranchId);
        }

        if (command.IsActive is { } active)
        {
            if (staff.Id == acting.Id && !active)
            {
                throw new StaffPermissionException("Deactivating your own account", acting.Role, StaffRole.PlatformAdmin);
            }

            staff.SetActive(active);
        }

        await SaveAsync(cancellationToken);

        return ToView(staff, clock.UtcNow);
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

        staff.SetPinHash(hasher.Hash(ValidPin(pin)));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("PIN reset for staff member {StaffMemberId} by {ActorId}.", staff.Id, acting.Id);

        return ToView(staff, clock.UtcNow);
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

        if (acting.VenueId != venueId || acting.Role is not (StaffRole.Owner or StaffRole.Manager))
        {
            throw new StaffPermissionException(operation, acting.Role, StaffRole.Manager);
        }

        return acting;
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
        if (target is not (StaffRole.Owner or StaffRole.Manager))
        {
            // Not a StaffPermissionException: nothing about the caller's rank would help, and
            // saying "requires the Manager role; the caller is a PlatformAdmin" to a platform admin
            // is nonsense. The refusal is about the person being created, not the person creating.
            _ = actorRole;

            throw new DomainStateException(
                $"A {target} signs in by tapping a PIN on a tablet a manager enrolled, so they cannot "
                + "have an email and password. Only an owner or a manager uses the admin panel. "
                + "Create them without credentials, or give them the Manager role.");
        }
    }

    /// <summary>The lowest rank that may assign or manage <paramref name="target"/>, for the error message.</summary>
    private static StaffRole RequiredRoleFor(StaffRole target) => target switch
    {
        StaffRole.Owner or StaffRole.Manager => StaffRole.Owner,
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
