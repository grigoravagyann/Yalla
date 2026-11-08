using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Tables;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>How every transition commits: the save, the conflicts, the replay, the loads and the results.</summary>
internal sealed partial class TableStateService
{
    // ---------------------------------------------------------------- commit and conflicts

    /// <summary>
    /// The single write point. Everything staged by the caller goes in one
    /// <c>SaveChanges</c>, and the two races that can lose are translated into answers a client
    /// can act on.
    /// </summary>
    private async Task<TableStateChangeResult> CommitAsync(
        DiningTable table,
        TableStatus attemptedFromStatus,
        Guid clientCommandId,
        Func<TableStateChangeResult> buildResult,
        CancellationToken cancellationToken,
        TableLockScope? locked = null,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try
        {
            // Built before the save, not after, so the answer can be stored in the same
            // SaveChanges as the work it describes. There is then no state in which the command
            // happened and the record of what it answered did not - which is the whole point: a
            // replayed seat-walk-in has to be able to return the session id it created, and the
            // audit row alone never knew it.
            var result = buildResult();
            RecordProcessedCommand(clientCommandId, CommandTypeFor(caller), result);

            // The change's events first - their handlers write the change stream's row and the History's line into this
            // save. This service's dispatcher, not the context's: in a request they are the same, and a test composing the
            // service over a bare context gets production's handlers for its caller (docs/domain-events.md).
            await domainEvents.DispatchAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            // Seatings hold the table's write lock; everything else runs in EF's implicit
            // transaction and has nothing to commit here.
            if (locked is not null)
            {
                await locked.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception ex) when (locked is not null && TableLock.IsTimeout(ex))
        {
            // Contention, not refusal: this writer never got its turn, so nothing was decided and
            // the same command sent again will very likely work. Kept distinct from the conflict
            // below, which will never succeed no matter how often it is repeated.
            throw new TableLockTimeoutException(locked.TableId, locked.TableLabel, locked.TimeoutMilliseconds);
        }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.Any(e => e.Entity is PreOrder))
        {
            // Not the table: the kitchen moved the booking's held pre-order ticket at the same moment the party was
            // seated with it. Nothing was saved, and "the table changed" would send the waiter looking for a change
            // that is not there - this is the concurrent update it is, and seating again puts the ticket on their tab
            // where the kitchen left it.
            logger.LogInformation(
                "Seating at table {TableId} ({TableLabel}) raced the kitchen moving its held pre-order; nothing saved.",
                table.Id,
                table.Label);

            throw new DbUpdateConcurrencyException(
                "The kitchen moved this booking's pre-order at the same moment. Seat the party again.", ex);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The table's RowVersion moved: somebody else changed this table first.
            throw await ConflictAsync(table, attemptedFromStatus, cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.OpenSessionPerTable))
        {
            // The database backstop fired: another session is already open on this table. Same
            // situation as a RowVersion clash, so the caller gets the same answer.
            throw await ConflictAsync(table, attemptedFromStatus, cancellationToken);
        }
        catch (DbUpdateException ex)
            when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TableStateChangeClientCommand)
                  || UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
        {
            // Two replays of the same offline command raced. The other one won and did the work;
            // this one reports its result.
            logger.LogInformation(
                "Command {ClientCommandId} was applied concurrently by another request; replaying its result.",
                clientCommandId);

            db.ChangeTracker.Clear();

            return await TryReplayAsync(clientCommandId, cancellationToken)
                   ?? throw new InvalidOperationException(
                       $"Command {clientCommandId} violated the idempotency index but no record was found.");
        }
    }

    /// <summary>
    /// Re-reads the table and builds the conflict, so the caller can tell the user what actually
    /// happened instead of that something failed.
    /// </summary>
    private async Task<TableStateConflictException> ConflictAsync(
        DiningTable table,
        TableStatus attemptedFromStatus,
        CancellationToken cancellationToken)
    {
        // AsNoTracking, because the change tracker is holding the failed attempt.
        var current = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == table.Id)
            .Select(t => new { t.Status, t.CurrentSessionId })
            .FirstOrDefaultAsync(cancellationToken);

        logger.LogInformation(
            "Lost a race on table {TableId} ({TableLabel}): attempted from {AttemptedFrom}, now {CurrentStatus}.",
            table.Id, table.Label, attemptedFromStatus, current?.Status);

        return new TableStateConflictException(
            table.Id,
            table.Label,
            attemptedFromStatus,
            current?.Status ?? table.Status,
            current?.CurrentSessionId);
    }

    // ---------------------------------------------------------------- idempotency

    private Task<TableStateChange?> FindReplayAsync(Guid clientCommandId, CancellationToken cancellationToken) =>
        db.TableStateChanges
            .AsNoTracking()
            // A table command's own row: a takeaway signal row has no table, and its command id is the server's.
            .FirstOrDefaultAsync(c => c.ClientCommandId == clientCommandId && c.DiningTableId != null, cancellationToken);

    /// <summary>
    /// Rebuilds the original answer from the audit row, which is why that row records the session
    /// it created. Reports the transition as it happened, not the table's state now.
    /// </summary>
    private async Task<TableStateChangeResult> BuildReplayResultAsync(
        TableStateChange change,
        CancellationToken cancellationToken)
    {
        // FindReplayAsync answers only rows that name a table.
        var tableId = change.DiningTableId.GetValueOrDefault();
        var table = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == tableId)
            .Select(t => new { t.Label, t.Branch.ReservationPolicy.BufferMinutes })
            .FirstOrDefaultAsync(cancellationToken);

        var nowUtc = clock.UtcNow;
        var next = await FindNextReservationAsync(tableId, nowUtc, null, cancellationToken);
        var bufferMinutes = table?.BufferMinutes ?? 0;

        logger.LogInformation(
            "Command {ClientCommandId} was already applied at {AtUtc}; returning the original result.",
            change.ClientCommandId, change.AtUtc);

        return new TableStateChangeResult
        {
            BranchId = change.BranchId,
            TableId = tableId,
            TableLabel = table?.Label ?? string.Empty,
            FromStatus = change.FromStatus,
            ToStatus = change.ToStatus,
            State = TableStateProjection.Derive(
                change.ToStatus, next.StartUtc, nowUtc, bufferMinutes),
            TableSessionId = change.TableSessionId,
            ReservationId = change.ReservationId,
            TabId = change.TabId,
            NextReservationStartUtc = next.StartUtc,
            FreeUntilUtc = TableStateProjection.FreeUntil(next.StartUtc, bufferMinutes),
            AtUtc = change.AtUtc,
            ClientCommandId = change.ClientCommandId,
            WasReplay = true,
        };
    }

    // ---------------------------------------------------------------- permissions

    private Guid RequireStaff(string operation)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } staffId)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        return staffId;
    }

    /// <summary>Branch managers, general managers and owners. Waiters and kitchen staff do not move money.</summary>
    private Guid RequireManager(string operation)
    {
        var staffId = RequireStaff(operation);

        if (actor.Role is not (StaffRole.Manager or StaffRole.GeneralManager or StaffRole.Owner))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        return staffId;
    }

    // ---------------------------------------------------------------- loading

    private async Task<DiningTable> LoadTableAsync(Guid branchId, Guid tableId, CancellationToken cancellationToken)
    {
        // Tracked, and with the branch so the policy thresholds are available. Tracking is what
        // makes the RowVersion check happen on save.
        var table = await db.DiningTables
            .Include(t => t.Branch)
            .FirstOrDefaultAsync(t => t.Id == tableId, cancellationToken);

        if (table is null)
        {
            throw new KeyNotFoundException($"Table {tableId} was not found.");
        }

        if (table.BranchId != branchId)
        {
            // A branch mismatch is a bad request, not a missing table: answering 404 would tell a
            // caller the table does not exist when it does.
            throw new ArgumentException(
                $"Table {tableId} does not belong to branch {branchId}.", nameof(branchId));
        }

        return table;
    }

    private async Task<Reservation> LoadReservationAsync(
        Guid reservationId,
        DiningTable table,
        CancellationToken cancellationToken,
        bool assignIfBookedWithoutTable = false)
    {
        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation is null)
        {
            throw new KeyNotFoundException($"Reservation {reservationId} was not found.");
        }

        // A booking made by time only gets its table as the party is seated: the one the waiter chose, at its branch.
        if (assignIfBookedWithoutTable && reservation.DiningTableId is null && reservation.BranchId == table.BranchId)
        {
            reservation.AssignTable(table.Id);
        }

        if (reservation.DiningTableId != table.Id)
        {
            throw new ArgumentException(
                $"Reservation {reservation.Code} is for a different table than {table.Label}.",
                nameof(reservationId));
        }

        return reservation;
    }

    /// <summary>
    /// The next booking that still matters for this table. This lookup is the whole reason
    /// <c>TableStatus</c> needs no stored <c>Reserved</c> member.
    /// </summary>
    /// <summary>
    /// The answer this command was already given, if it has been applied before.
    /// </summary>
    /// <remarks>
    /// The stored body is returned rather than a recomputed one. Recomputing would answer with the
    /// world as it is now, which is not what the caller asked: a tablet replaying its offline queue
    /// wants the result of the command it sent, and "the table is Free now" is a different fact
    /// from "your seating succeeded". Only <c>WasReplay</c> is changed, so the caller can tell.
    /// </remarks>
    private async Task<TableStateChangeResult?> TryReplayAsync(
        Guid clientCommandId,
        CancellationToken cancellationToken)
    {
        var processed = await db.ProcessedCommands
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientCommandId == clientCommandId, cancellationToken);

        if (processed is not null)
        {
            var stored = JsonSerializer.Deserialize<TableStateChangeResult>(processed.ResponseJson, ResponseJson);

            if (stored is not null)
            {
                logger.LogInformation(
                    "Command {ClientCommandId} was already applied; returning the stored answer.", clientCommandId);

                return stored with { WasReplay = true };
            }
        }

        // Commands applied before this store existed have only their audit row. Recomputing from it
        // is worse than the stored answer and better than refusing, so it stays as a fallback.
        if (await FindReplayAsync(clientCommandId, cancellationToken) is not { } audited)
        {
            return null;
        }

        // Loud on purpose. This path exists for rows written before ProcessedCommands did, and those
        // disappear the first time a developer resets their database - so in a month it should never
        // fire. If it still is, something is writing audit rows without recording their answer, and
        // the symptom would otherwise be a replay that quietly returns a recomputed body.
        logger.LogWarning(
            "Command {ClientCommandId} was replayed from its audit row because no ProcessedCommand "
            + "exists for it. Expected only for commands applied before that store was added; if this "
            + "is a recent command, a write path is skipping RecordProcessedCommand.",
            clientCommandId);

        return await BuildReplayResultAsync(audited, cancellationToken);
    }

    /// <summary>Adds the answer to the unit of work, alongside the change it describes.</summary>
    private void RecordProcessedCommand(Guid clientCommandId, string commandType, TableStateChangeResult result)
    {
        var (actorType, actorId) = TableStateActors.Resolve(actor);

        db.ProcessedCommands.Add(new ProcessedCommand(
            clientCommandId,
            commandType,
            actorType,
            actorId,
            JsonSerializer.Serialize(result, ResponseJson),
            StatusCodes.Ok,
            clock.UtcNow));
    }

    /// <summary>
    /// Refuses a queued command that describes a table which has since moved.
    /// </summary>
    /// <remarks>
    /// Idempotency stops a command being applied twice and says nothing about it being stale. A
    /// queued command must therefore say what the waiter was looking at; a live one need not,
    /// because it was made from a fresh read and the table's row version already guards the race.
    /// </remarks>
    private static void RequirePrecondition(ITableStateCommand command, DiningTable table)
    {
        if (command.ExpectedFromStatus is not { } expected)
        {
            if (command.Queued)
            {
                throw new ArgumentException(
                    "A queued command must carry the status the table had when it was tapped, so a "
                    + "stale replay can be told from a live one.",
                    nameof(command));
            }

            return;
        }

        if (table.Status != expected)
        {
            throw new CommandPreconditionFailedException(
                table.Id, table.Label, expected, table.Status, command.ClientCommandId,
                PreconditionFailure.StatusChanged);
        }

        // Both supplied means both must hold. The status matching is the interesting case: the
        // table looks exactly as the waiter left it and is not the same table - a party was seated,
        // fed, billed and cleared while this command sat in a queue. Status alone would apply it.
        if (command.ExpectedRowVersion is { Length: > 0 } expectedVersion
            && !string.Equals(expectedVersion, Convert.ToBase64String(table.RowVersion), StringComparison.Ordinal))
        {
            throw new CommandPreconditionFailedException(
                table.Id, table.Label, expected, table.Status, command.ClientCommandId,
                PreconditionFailure.TableChangedAndChangedBack);
        }
    }

    /// <summary>
    /// Bookings this table still has, so a waiter marking it broken can see who to telephone.
    /// </summary>
    /// <remarks>
    /// <b>Not a race - a missing behaviour.</b> Today a waiter can mark table 7 broken at six
    /// o'clock with three bookings on it tonight and nothing anywhere tells anybody. The list is
    /// returned and deliberately not acted on: ten minutes for a replacement chair and a week for a
    /// new floor look identical from here, and only the person in the room can tell them apart.
    /// </remarks>
    private async Task<IReadOnlyList<AffectedReservation>> FutureBookingsAsync(
        Guid tableId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await db.Reservations
            .AsNoTracking()
            .Where(r => r.DiningTableId == tableId
                        && r.EndUtc > nowUtc
                        && (r.Status == ReservationStatus.Confirmed
                            || r.Status == ReservationStatus.PendingApproval))
            .OrderBy(r => r.StartUtc)
            .Select(r => new AffectedReservation(
                r.Id,
                r.Code,
                r.GuestName,
                r.GuestPhone,
                r.PartySize,
                r.StartUtc,
                r.LocalDate,
                r.LocalStartTime,
                r.Status))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The command slug recorded against a processed command, derived from the method that ran it
    /// so the two can never disagree.
    /// </summary>
    private static string CommandTypeFor(string caller)
    {
        var name = caller.EndsWith("Async", StringComparison.Ordinal) ? caller[..^5] : caller;
        var slug = string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

        return $"table.{slug}";
    }

    private async Task<NextReservation> FindNextReservationAsync(
        Guid tableId,
        DateTime nowUtc,
        Guid? excludingReservationId,
        CancellationToken cancellationToken)
    {
        var next = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DiningTableId == tableId
                        && r.EndUtc > nowUtc
                        && (r.Status == ReservationStatus.Confirmed
                            || r.Status == ReservationStatus.PendingApproval)
                        && (excludingReservationId == null || r.Id != excludingReservationId))
            .OrderBy(r => r.StartUtc)
            .Select(r => new { r.Id, r.StartUtc, r.PartySize })
            .FirstOrDefaultAsync(cancellationToken);

        return next is null
            ? NextReservation.None
            : new NextReservation(next.Id, next.StartUtc, next.PartySize);
    }

    /// <summary>
    /// How a stored response is written and read. Fixed here so a replay deserialises what was
    /// serialised however the host's own JSON options change later.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);

    /// <summary>The status a successful transition answers with, stored alongside its body.</summary>
    private static class StatusCodes
    {
        public const int Ok = 200;
    }

    // ---------------------------------------------------------------- results

    private static TableStateChangeResult Success(
        DiningTable table,
        TableStatus fromStatus,
        TableStatus toStatus,
        DateTime nowUtc,
        Guid clientCommandId,
        NextReservation next,
        Guid? tableSessionId = null,
        Guid? reservationId = null,
        Guid? tabId = null,
        long? outstandingAmd = null,
        IReadOnlyList<TableStateWarning>? warnings = null)
    {
        var bufferMinutes = table.Branch.ReservationPolicy.BufferMinutes;

        return new TableStateChangeResult
        {
            BranchId = table.BranchId,
            TableId = table.Id,
            TableLabel = table.Label,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            State = TableStateProjection.Derive(toStatus, next.StartUtc, nowUtc, bufferMinutes),
            TableSessionId = tableSessionId,
            ReservationId = reservationId,
            TabId = tabId,
            NextReservationStartUtc = next.StartUtc,
            FreeUntilUtc = TableStateProjection.FreeUntil(next.StartUtc, bufferMinutes),
            AtUtc = nowUtc,
            ClientCommandId = clientCommandId,
            WasReplay = false,
            OutstandingAmd = outstandingAmd,
            Warnings = warnings ?? [],
        };
    }

    /// <summary>
    /// Warns, never blocks. The waiter may know the 20:00 party cancelled, or that these four are
    /// only having coffee.
    /// </summary>
    private static List<TableStateWarning> SeatingWarnings(
        DiningTable table,
        DateTime nowUtc,
        NextReservation next)
    {
        var warnings = new List<TableStateWarning>();
        var policy = table.Branch.ReservationPolicy;

        if (TableStateProjection.SeatingCollidesWithReservation(
                next.StartUtc, nowUtc, policy.TurnTimeMinutes))
        {
            warnings.Add(new TableStateWarning(
                TableStateWarning.UpcomingReservation,
                $"table {table.Label} reserved {FormatBranchLocalTime(next.StartUtc!.Value, table.Branch.TimeZoneId)}"));
        }

        // The tighter window: the booked party is nearly here. Carries the time and the size,
        // because "reserved 20:00 for 6" is what a waiter can actually act on - they know whether
        // six people will fit around the party they are about to seat.
        if (SessionOccupancy.WithinWalkInHoldback(next.StartUtc, nowUtc, policy.WalkInHoldbackMinutes))
        {
            var at = FormatBranchLocalTime(next.StartUtc!.Value, table.Branch.TimeZoneId);
            var forParty = next.PartySize is { } size ? $" for {size}" : string.Empty;

            warnings.Add(new TableStateWarning(
                TableStateWarning.WalkInHoldback,
                $"table {table.Label} reserved {at}{forParty}, within {policy.WalkInHoldbackMinutes} minutes"));
        }

        return warnings;
    }

    /// <summary>
    /// Renders an instant as wall-clock time at the branch, because "reserved 20:00" is what the
    /// waiter can act on and "reserved 16:00Z" is not.
    /// </summary>
    private static string FormatBranchLocalTime(DateTime utc, string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
                .ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A misconfigured zone must not break seating a table. Say plainly that this is UTC
            // rather than print a local-looking time that is wrong by four hours.
            return utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }
    }

    /// <summary>The next relevant booking for a table, or none.</summary>
    /// <remarks>
    /// <c>PartySize</c> is carried so the holdback warning can say "reserved 20:00 for 4" - the
    /// number is most of what a waiter needs to judge whether the seating is safe.
    /// </remarks>
    private readonly record struct NextReservation(Guid? ReservationId, DateTime? StartUtc, int? PartySize)
    {
        public static NextReservation None => new(null, null, null);
    }
}
