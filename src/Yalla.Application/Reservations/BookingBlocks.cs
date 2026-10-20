using Yalla.Domain.Occupancy;

namespace Yalla.Application.Reservations;

/// <summary>
/// The platform admin's view of no-show blocks (review B1f): the ones in force, and lifting one with a reason.
/// </summary>
public interface IBookingBlockService
{
    /// <summary>The blocks in force now, newest first. Platform admin only.</summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not an active platform admin.</exception>
    Task<IReadOnlyList<BookingBlockView>> ListInForceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lifts a block now, with a reason - audited as <c>booking-block.lift</c>, the guest told. Nothing it cancelled
    /// comes back. A block already lifted answers as it stands.
    /// </summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not an active platform admin.</exception>
    /// <exception cref="KeyNotFoundException">No such block.</exception>
    /// <exception cref="Domain.DomainStateException">It already ended on its own.</exception>
    /// <exception cref="Domain.FieldValidationException">No reason, or one longer than 500 characters.</exception>
    Task<BookingBlockView> LiftAsync(Guid blockId, LiftBookingBlockCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Lift a block: why, in words the audit keeps.</summary>
/// <param name="Reason">Required, 3 to 500 characters.</param>
public sealed record LiftBookingBlockCommand(string? Reason);

/// <summary>One no-show block as the platform console shows it.</summary>
/// <param name="BlockId">The block.</param>
/// <param name="DinerUserId">The account blocked.</param>
/// <param name="DinerDisplayName">Its display name, when it has one.</param>
/// <param name="PhoneE164">The proved number the block also holds, while it holds one.</param>
/// <param name="BlockedAtUtc">When it began.</param>
/// <param name="BlockedUntilUtc">When it ends on its own.</param>
/// <param name="LiftedAtUtc">When it was lifted early.</param>
/// <param name="LiftedBecause">Why: 1 a platform admin, 2 a no-show withdrawn.</param>
/// <param name="LiftReason">The reason given.</param>
public sealed record BookingBlockView(
    Guid BlockId,
    Guid DinerUserId,
    string? DinerDisplayName,
    string? PhoneE164,
    DateTime BlockedAtUtc,
    DateTime BlockedUntilUtc,
    DateTime? LiftedAtUtc = null,
    BookingBlockLiftCause? LiftedBecause = null,
    string? LiftReason = null);
