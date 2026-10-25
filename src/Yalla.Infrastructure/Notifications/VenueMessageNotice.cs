namespace Yalla.Infrastructure.Notifications;

/// <summary>A venue message, as its thread's guest is told about it.</summary>
/// <param name="ConversationId">The thread - where a tap lands.</param>
/// <param name="MessageId">The message.</param>
/// <param name="DinerUserId">Who is told.</param>
/// <param name="VenueName">The venue, as named when the message was written - the title.</param>
/// <param name="ReservationId">The booking, for a booking's thread.</param>
/// <param name="TakeawayOrderId">The order, for a takeaway order's thread.</param>
/// <param name="Ask">The venue's "are you coming?" rather than words.</param>
/// <param name="Preview">The words, at most 140 characters. Null for an ask.</param>
/// <param name="LocalStartTime">The booking's start on the branch's wall clock, for the ask's "at 19:30".</param>
internal sealed record VenueMessageNotice(
    Guid ConversationId,
    Guid MessageId,
    Guid DinerUserId,
    string VenueName,
    Guid? ReservationId,
    Guid? TakeawayOrderId,
    bool Ask,
    string? Preview,
    TimeOnly? LocalStartTime);
