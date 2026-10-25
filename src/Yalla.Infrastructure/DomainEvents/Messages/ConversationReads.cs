using Yalla.Application.Messages;
using Yalla.Domain.Messages;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Messages;

/// <summary>
/// What the message handlers read about a thread, once per message: the subject's facts (<see cref="GuestThreads.Subject"/>)
/// and whether the thread's guest has a live account to tell. One event has two or three handlers that each need one of
/// them - the History's line and the guest's notice - and reading it once for all of them keeps the round trips what the
/// hand-written code made.
/// </summary>
/// <remarks>
/// Scoped, and keyed by the message: a second message in the same scope (a retry after a lost race, on a cleared tracker)
/// reads afresh. The account's lock, once taken, is held to the commit, so its answer cannot change while the message's
/// handlers run.
/// </remarks>
internal sealed class ConversationReads(YallaDbContext db)
{
    private Guid? _subjectOf;
    private GuestThreads.Subject? _subject;
    private Guid? _liveOf;
    private bool _live;

    /// <summary>The thread's booking or order, as the inbox reads it.</summary>
    public async Task<GuestThreads.Subject> SubjectAsync(
        Conversation thread, ConversationMessage message, CancellationToken cancellationToken)
    {
        if (_subjectOf != message.Id || _subject is null)
        {
            var subject = thread.ReservationId is { } reservationId
                ? ConversationSubject.Reservation(reservationId)
                : ConversationSubject.Takeaway(thread.TakeawayOrderId!.Value);

            _subject = await GuestThreads.SubjectAsync(db, subject, cancellationToken)
                       ?? throw new InvalidOperationException($"The thread {thread.Id} has no subject.");
            _subjectOf = message.Id;
        }

        return _subject;
    }

    /// <summary>
    /// The thread has an account and it is live - under the account's lock (<see cref="DinerAccountLock.IsLiveAsync"/>),
    /// held to the commit: a deletion that comes after waits, and removes what the guest is told. A thread with no
    /// account, or one already gone, has nobody to tell.
    /// </summary>
    public async Task<bool> GuestIsLiveAsync(
        Conversation thread, ConversationMessage message, CancellationToken cancellationToken)
    {
        if (_liveOf == message.Id)
        {
            return _live;
        }

        var live = false;

        if (thread.DinerUserId is { } dinerUserId)
        {
            await db.EnsureTransactionAsync(cancellationToken);
            live = await DinerAccountLock.IsLiveAsync(db, dinerUserId, cancellationToken);
        }

        (_liveOf, _live) = (message.Id, live);

        return live;
    }
}
