namespace Yalla.Domain.Messages;

/// <summary>
/// Who wrote a message in a booking's or a takeaway order's thread. Stored as an int, so every number is pinned
/// (<c>PersistedEnumValueTests</c>).
/// </summary>
public enum MessageAuthor
{
    /// <summary>The guest: the subject's own diner account (or its manage link, for an answer).</summary>
    Guest = 1,

    /// <summary>Somebody at the venue. Staff see who - the message keeps their name; the guest sees only the venue's.</summary>
    Venue = 2,

    /// <summary>Nobody: a line the server writes when an answer changed the booking ("held until 19:45").</summary>
    System = 3,
}

/// <summary>What a message is. Stored as an int, so every number is pinned (<c>PersistedEnumValueTests</c>).</summary>
/// <remarks>
/// Only <see cref="Text"/> carries words of its own. The other three are structured - the answer, the minutes, the time
/// held until - and every client writes their sentence in its own language.
/// </remarks>
public enum MessageKind
{
    /// <summary>Plain text somebody typed: 1 to 500 characters, never linkified.</summary>
    Text = 1,

    /// <summary>The venue's "Ask: are you coming?" (<c>POST /api/reservations/{id}/ask-on-my-way</c>). Bookings only.</summary>
    AskComing = 2,

    /// <summary>The guest's answer: on my way, running late (with the minutes), or not coming.</summary>
    Answer = 3,

    /// <summary>The system line after an answer: what the booking is now.</summary>
    Status = 4,
}

/// <summary>The guest's answer to "are you coming?". Stored as an int, so every number is pinned.</summary>
public enum GuestAnswer
{
    /// <summary>"I'm coming" - <c>on-my-way</c>.</summary>
    Coming = 1,

    /// <summary>"Running late" by 10, 15 or 30 minutes - <c>running-late</c>.</summary>
    Late = 2,

    /// <summary>"Not coming" - the booking's own cancel.</summary>
    NotComing = 3,
}
