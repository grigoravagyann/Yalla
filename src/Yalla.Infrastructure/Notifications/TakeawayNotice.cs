namespace Yalla.Infrastructure.Notifications;

/// <summary>A takeaway order's news, as its guest is told it.</summary>
/// <param name="OrderId">The order - where a tap lands.</param>
/// <param name="DinerUserId">Who is told.</param>
/// <param name="VenueName">The venue, as named when the message was written.</param>
/// <param name="BranchName">The branch.</param>
/// <param name="Code">"T-482".</param>
/// <param name="ReadyAtLocalTime">When it will be ready, on the branch's wall clock. Accepted only.</param>
/// <param name="Reason">What the venue said, declining. Null when it said nothing.</param>
/// <param name="NoAnswer">Declined because nobody at the venue answered in time.</param>
internal sealed record TakeawayNotice(
    Guid OrderId,
    Guid DinerUserId,
    string VenueName,
    string BranchName,
    string Code,
    TimeOnly? ReadyAtLocalTime = null,
    string? Reason = null,
    bool NoAnswer = false);
