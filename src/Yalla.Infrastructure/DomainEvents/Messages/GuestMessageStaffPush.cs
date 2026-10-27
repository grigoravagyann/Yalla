using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Messages;

/// <summary>
/// The <c>guestMessages</c> staff push: a guest's words, or their answer to "are you coming?" (coming, late, not coming -
/// from the app, the manage link or the notification), landed in a booking's or a takeaway order's thread, so the floor's
/// phones and tablets buzz (<c>docs/notifications.md</c> "Staff push"). The status line after an answer is the server's
/// and is not pushed. Only the kind and the message go in the outbox; who is told, and whether nobody at the venue has
/// opened the thread since, is decided at send time by <c>StaffPushHandler</c>.
/// </summary>
/// <remarks>
/// It used to be detected by <see cref="StaffPushSignals"/> from the change tracker - a guest's text or answer added -
/// and keeps the key the tracker wrote: a message is written once.
/// </remarks>
internal sealed class GuestMessageStaffPush(YallaDbContext db, IClock clock) :
    IDomainEventHandler<GuestMessageWritten>,
    IDomainEventHandler<GuestAnswered>
{
    public Task HandleAsync(GuestMessageWritten domainEvent, CancellationToken cancellationToken) =>
        StageAsync(domainEvent.Thread.BranchId, domainEvent.Message.Id, cancellationToken);

    public Task HandleAsync(GuestAnswered domainEvent, CancellationToken cancellationToken) =>
        StageAsync(domainEvent.Thread.BranchId, domainEvent.Answer.Id, cancellationToken);

    /// <remarks>
    /// Not at a branch the platform has not allowed guest messages at (<c>docs/branch-features.md</c>): the floor's
    /// Messages tab is locked there, so nothing buzzes about a thread nobody may answer - a guest's answer to "are you
    /// coming?" included, which the floor still sees on the booking's tile.
    /// </remarks>
    private async Task StageAsync(Guid branchId, Guid messageId, CancellationToken cancellationToken)
    {
        if (!await BranchFeatureGate.AllowsAsync(db, branchId, BranchFeature.GuestMessages, cancellationToken))
        {
            return;
        }

        StaffPushSignals.StageOne(
            db, StaffPushKinds.GuestMessages, messageId, $"{StaffPushSignals.KeyPrefix}message:{messageId}", clock.UtcNow);
    }
}
