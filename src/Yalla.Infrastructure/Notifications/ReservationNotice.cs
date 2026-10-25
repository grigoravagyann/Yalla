using Yalla.Domain.Enums;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// What the outbox carries for each message type.
/// </summary>
/// <remarks>
/// <b>Self-contained on purpose.</b> A payload of ids alone would have to re-read the world at send
/// time, and the world has moved on: the booking may have been cancelled, the venue renamed, the
/// diner's name changed. What was true when the message was written is what the message should say.
/// The one thing read at send time is the <i>state</i> that decides whether to send at all.
/// </remarks>
internal sealed record ReservationNotice(
    Guid ReservationId,
    Guid DinerUserId,
    string VenueName,
    string BranchName,
    string TableLabel,
    TimeOnly LocalStartTime,
    string ReservationCode,
    int GraceExtensionMinutes = 0,
    bool Approved = false,
    bool Expired = false,
    bool WasChange = false,
    bool OriginalKept = false,
    DeclineReason? DeclinedBecause = null,
    DateOnly? LocalDate = null,
    int PartySize = 0,
    ReservationStatus? Status = null,
    bool StaffAsked = false);
