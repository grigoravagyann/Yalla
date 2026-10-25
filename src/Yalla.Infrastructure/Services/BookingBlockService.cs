using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The platform admin's no-show blocks (review B1f): the ones in force, and lifting one - audited as
/// <c>booking-block.lift</c> with the reason, the guest told. A lift never brings back what the block cancelled.
/// </summary>
/// <remarks>
/// The lift takes the guest's account lock - the lock a no-show and every booking of theirs take - so it cannot cross
/// a no-show being recorded for them the same moment.
/// </remarks>
internal sealed class BookingBlockService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ILogger<BookingBlockService> logger) : IBookingBlockService
{
    /// <summary>The audit action.</summary>
    public const string LiftAuditAction = "booking-block.lift";

    /// <summary>A reason this short says nothing.</summary>
    public const int MinReasonLength = 3;

    public async Task<IReadOnlyList<BookingBlockView>> ListInForceAsync(CancellationToken cancellationToken = default)
    {
        await RequirePlatformAdminAsync("List booking blocks", cancellationToken);

        var nowUtc = clock.UtcNow;

        return await db.BookingBlocks
            .AsNoTracking()
            .Where(b => b.LiftedAtUtc == null && b.BlockedUntilUtc > nowUtc)
            .OrderByDescending(b => b.BlockedAtUtc)
            .Select(b => new BookingBlockView(
                b.Id,
                b.DinerUserId,
                db.DinerUsers.Where(d => d.Id == b.DinerUserId).Select(d => d.DisplayName).FirstOrDefault(),
                b.PhoneE164,
                b.BlockedAtUtc,
                b.BlockedUntilUtc,
                b.LiftedAtUtc,
                b.LiftedBecause,
                b.LiftReason))
            .ToListAsync(cancellationToken);
    }

    public async Task<BookingBlockView> LiftAsync(
        Guid blockId,
        LiftBookingBlockCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var staffId = await RequirePlatformAdminAsync("Lift a booking block", cancellationToken);
        var reason = command.Reason?.Trim();

        if (string.IsNullOrEmpty(reason) || reason.Length < MinReasonLength || reason.Length > FieldLengths.Reason)
        {
            throw new FieldValidationException(new FieldViolation(
                "reason",
                $"Say why, in {MinReasonLength} to {FieldLengths.Reason} characters.",
                FieldBounds.Range,
                Min: MinReasonLength,
                Max: FieldLengths.Reason,
                Value: reason?.Length ?? 0));
        }

        var accountId = await db.BookingBlocks
            .AsNoTracking()
            .Where(b => b.Id == blockId)
            .Select(b => (Guid?)b.DinerUserId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Booking block {blockId} was not found.");

        var nowUtc = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.IsLiveAsync(db, accountId, cancellationToken);

        var block = await db.BookingBlocks.FirstAsync(b => b.Id == blockId, cancellationToken);

        if (block.LiftedAtUtc is null && block.BlockedUntilUtc <= nowUtc)
        {
            throw new DomainStateException("This block has already ended on its own; there is nothing to lift.");
        }

        // The guest is told they can book again by the lift's handler; the platform's audit of it is this service's.
        if (block.Lift(nowUtc, BookingBlockLiftCause.PlatformAdmin, staffId, reason))
        {
            await PlatformAudit.RecordAsync(
                db,
                actor,
                clock,
                LiftAuditAction,
                new AuditTarget("booking-block", block.Id, null, null),
                new { dinerUserId = block.DinerUserId, block.BlockedAtUtc, block.BlockedUntilUtc, reason },
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Platform admin {StaffId} lifted the no-show block on diner {DinerUserId}: {Reason}",
                staffId, block.DinerUserId, reason);
        }

        await transaction.CommitAsync(cancellationToken);

        return new BookingBlockView(
            block.Id,
            block.DinerUserId,
            await db.DinerUsers.Where(d => d.Id == block.DinerUserId).Select(d => d.DisplayName).FirstOrDefaultAsync(cancellationToken),
            block.PhoneE164,
            block.BlockedAtUtc,
            block.BlockedUntilUtc,
            block.LiftedAtUtc,
            block.LiftedBecause,
            block.LiftReason);
    }

    private async Task<Guid> RequirePlatformAdminAsync(string operation, CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff
            || actor.StaffMemberId is not { } staffId
            || actor.Role != StaffRole.PlatformAdmin
            || !await db.StaffMembers.AsNoTracking().AnyAsync(
                s => s.Id == staffId && s.IsActive && s.Role == StaffRole.PlatformAdmin, cancellationToken))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.PlatformAdmin);
        }

        return staffId;
    }
}
