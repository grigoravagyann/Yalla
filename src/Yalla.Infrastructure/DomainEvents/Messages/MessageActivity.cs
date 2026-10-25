using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Messages;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Messages;

/// <summary>
/// The History's line for the venue's side of a thread (<c>docs/activity.md</c>): <c>message.sent</c> - who answered which
/// guest, with what, and how long the guest had waited - and <c>message.asked-on-my-way</c> - who asked "are you
/// coming?", and whether there was anybody to tell. Who acted and on which device is the request's, read by the activity
/// log. The guest's own words have no line, and their answers are the booking's (<c>booking.guest-answered</c>).
/// </summary>
internal sealed class MessageActivity(YallaDbContext db, IActivityLog activity, ConversationReads reads) :
    IDomainEventHandler<VenueMessageWritten>,
    IDomainEventHandler<VenueAskedComing>
{
    public async Task HandleAsync(VenueMessageWritten domainEvent, CancellationToken cancellationToken)
    {
        var (thread, message) = (domainEvent.Thread, domainEvent.Message);
        var facts = await reads.SubjectAsync(thread, message, cancellationToken);
        var waitingSince = await WaitingSinceAsync(thread, cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(
                ActivityCategory.Message,
                ActivityActions.MessageSent,
                facts.Kind == ConversationSubjectKind.Reservation ? ActivitySubjects.Reservation : ActivitySubjects.Takeaway,
                facts.Id)
            {
                BranchId = facts.BranchId,
                TableLabel = facts.TableLabel,
                GuestName = facts.GuestName,
                SubjectCode = facts.TakeawayCode,
                Summary = new
                {
                    conversationId = thread.Id,
                    messageId = message.Id,
                    text = message.Body,
                    replySeconds = waitingSince is { } since
                        ? (int?)Math.Max(0d, (domainEvent.OccurredAtUtc - since).TotalSeconds)
                        : null,
                },
            },
            cancellationToken);
    }

    /// <summary>The booking's line: who asked, the booking's code and start, and whether the guest could be told.</summary>
    public async Task HandleAsync(VenueAskedComing domainEvent, CancellationToken cancellationToken)
    {
        var reservationId = domainEvent.Thread.ReservationId!.Value;
        var reservation = db.Reservations.Local.FirstOrDefault(r => r.Id == reservationId)
                          ?? await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == reservationId, cancellationToken);
        var notified = await reads.GuestIsLiveAsync(domainEvent.Thread, domainEvent.Ask, cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(
                ActivityCategory.Message, ActivityActions.MessageAskedOnMyWay, ActivitySubjects.Reservation, reservation.Id)
            {
                BranchId = reservation.BranchId,
                GuestName = reservation.GuestName,
                SubjectCode = reservation.Code,
                TableLabel = db.DiningTables.Local.FirstOrDefault(t => t.Id == reservation.DiningTableId)?.Label
                             ?? await db.DiningTables
                                 .AsNoTracking()
                                 .Where(t => t.Id == reservation.DiningTableId)
                                 .Select(t => t.Label)
                                 .FirstOrDefaultAsync(cancellationToken),
                Summary = new { notified, startUtc = reservation.StartUtc },
            },
            cancellationToken);
    }

    /// <summary>
    /// How long the guest waited: from their first text the venue had not answered - nothing from the venue at or after
    /// it - to this message. Null when this answers nothing: the venue writing first (a thread new in this save), or again
    /// before the guest replied (<c>docs/activity.md</c>). Read from what is saved, which the message being written is not.
    /// </summary>
    private async Task<DateTime?> WaitingSinceAsync(Conversation thread, CancellationToken cancellationToken)
    {
        if (db.Entry(thread).State == EntityState.Added)
        {
            return null;
        }

        return await db.ConversationMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == thread.Id
                        && m.Author == MessageAuthor.Guest
                        && m.Kind == MessageKind.Text
                        && !db.ConversationMessages.Any(v => v.ConversationId == thread.Id
                                                             && v.Author == MessageAuthor.Venue
                                                             && v.CreatedAtUtc >= m.CreatedAtUtc))
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => (DateTime?)m.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
