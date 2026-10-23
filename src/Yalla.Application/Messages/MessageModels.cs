namespace Yalla.Application.Messages;

// ---------------------------------------------------------------- the subject

/// <summary>What a thread is about: a booking or a takeaway order.</summary>
public enum ConversationSubjectKind
{
    Reservation = 1,
    Takeaway = 2,
}

/// <summary>A thread's subject, as a route names it.</summary>
/// <param name="Kind">A booking or a takeaway order.</param>
/// <param name="Id">The booking's or the order's id.</param>
public sealed record ConversationSubject(ConversationSubjectKind Kind, Guid Id)
{
    public static ConversationSubject Reservation(Guid id) => new(ConversationSubjectKind.Reservation, id);

    public static ConversationSubject Takeaway(Guid id) => new(ConversationSubjectKind.Takeaway, id);
}

/// <summary>
/// A thread's subject as the header shows it - "Today 19:30 · 4 guests · table 6", or "Takeaway · T-482 · ready 19:10" -
/// in parts, so each client writes the line in its own language.
/// </summary>
/// <param name="Kind"><c>reservation</c> or <c>takeaway</c>.</param>
/// <param name="Id">The booking's or the order's id.</param>
/// <param name="VenueName">The venue - the only name the guest sees on the venue's side.</param>
/// <param name="BranchId">The branch.</param>
/// <param name="StartUtc">A booking's start. Absent on a takeaway order.</param>
/// <param name="LocalDate">A booking's day on the branch's wall clock, <c>yyyy-MM-dd</c>. Absent on a takeaway order.</param>
/// <param name="LocalTime">A booking's start on the branch's wall clock, <c>HH:mm</c>. Absent on a takeaway order.</param>
/// <param name="PartySize">A booking's party. Absent on a takeaway order.</param>
/// <param name="TableLabel">A booking's table. Absent on a takeaway order.</param>
/// <param name="TakeawayCode">An order's code, "T-482". Absent on a booking.</param>
/// <param name="ReadyAtLocal">
/// An order's ready time on the branch's wall clock, <c>HH:mm</c> - the promised time once accepted, the requested one
/// before. Absent on a booking.
/// </param>
/// <param name="ReadyAtUtc">The same instant. Absent on a booking.</param>
public sealed record ConversationSubjectView(
    string Kind,
    Guid Id,
    string VenueName,
    Guid BranchId,
    DateTime? StartUtc,
    string? LocalDate,
    string? LocalTime,
    int? PartySize,
    string? TableLabel,
    string? TakeawayCode,
    string? ReadyAtLocal,
    DateTime? ReadyAtUtc);

// ---------------------------------------------------------------- a message

/// <summary>The guest's answer to "are you coming?".</summary>
/// <param name="Kind"><c>coming</c>, <c>late</c> or <c>not-coming</c>.</param>
/// <param name="LateMinutes">For <c>late</c>: 10, 15 or 30. Absent otherwise.</param>
public sealed record MessageAnswerView(string Kind, int? LateMinutes);

/// <summary>What the booking is after the guest's answer - the system line "Booking updated · held until 19:45".</summary>
/// <param name="Answer">The answer it follows: <c>coming</c>, <c>late</c> or <c>not-coming</c>.</param>
/// <param name="HeldUntilLocal">After <c>late</c>: the start plus the minutes, <c>HH:mm</c> on the branch's clock. Absent otherwise.</param>
/// <param name="Cancelled">True after <c>not-coming</c>: the booking was cancelled.</param>
public sealed record MessageStatusView(string Answer, string? HeldUntilLocal, bool Cancelled);

/// <summary>
/// One message as the guest sees it. No staff names: the venue's side is the venue.
/// </summary>
/// <param name="Id">The message.</param>
/// <param name="Author"><c>guest</c>, <c>venue</c> or <c>system</c>.</param>
/// <param name="Kind">
/// <c>text</c> (has <c>text</c>), <c>ask-coming</c> (the venue's "are you coming?"), <c>answer</c> (has <c>answer</c>) or
/// <c>status</c> (has <c>status</c>). Write the words for the last three in the reader's language.
/// </param>
/// <param name="Text">The words of a <c>text</c> message - plain text, never to be linkified. Absent for the other kinds.</param>
/// <param name="Answer">An <c>answer</c>'s facts. Absent for the other kinds.</param>
/// <param name="Status">A <c>status</c> line's facts. Absent for the other kinds.</param>
/// <param name="CreatedAtUtc">When it was written. "Seen" under a guest message: it is not after <c>venueLastReadUtc</c>.</param>
public sealed record DinerConversationMessage(
    Guid Id,
    string Author,
    string Kind,
    string? Text,
    MessageAnswerView? Answer,
    MessageStatusView? Status,
    DateTime CreatedAtUtc);

/// <summary>One message as the venue sees it: the guest's view plus who at the venue wrote it.</summary>
/// <param name="Id">The message.</param>
/// <param name="Author"><c>guest</c>, <c>venue</c> or <c>system</c>.</param>
/// <param name="Kind"><c>text</c>, <c>ask-coming</c>, <c>answer</c> or <c>status</c>.</param>
/// <param name="Text">The words of a <c>text</c> message. Absent for the other kinds.</param>
/// <param name="Answer">An <c>answer</c>'s facts.</param>
/// <param name="Status">A <c>status</c> line's facts.</param>
/// <param name="CreatedAtUtc">When it was written.</param>
/// <param name="StaffName">On a venue message: who wrote it, as they were named then. Absent on the others.</param>
public sealed record StaffConversationMessage(
    Guid Id,
    string Author,
    string Kind,
    string? Text,
    MessageAnswerView? Answer,
    MessageStatusView? Status,
    DateTime CreatedAtUtc,
    string? StaffName);

// ---------------------------------------------------------------- a thread

/// <summary>
/// A booking's or a takeaway order's thread, as its guest reads it.
/// </summary>
/// <param name="ConversationId">The thread; absent while nobody has written yet (and <c>messages</c> is empty).</param>
/// <param name="Subject">What it is about - the header.</param>
/// <param name="Open">
/// Takes messages now: a booking until two hours after it ends (or after it was cancelled, declined, missed or
/// completed); an order until two hours after it was collected, declined, cancelled or not collected. Closed: no
/// composer - the "closed" note instead; the thread stays readable for 30 days.
/// </param>
/// <param name="ClosesAtUtc">When it closes, or closed. Absent while a takeaway order is still moving.</param>
/// <param name="GuestsCanWrite">The venue takes typed messages. False: no composer - "reply with the buttons or call".</param>
/// <param name="VenuePhone">The branch's number for the call button, E.164. Absent when it has none on file.</param>
/// <param name="PendingAsk">
/// The venue asked "are you coming?", nobody has answered since, and the answer is still taken (a confirmed booking,
/// from two hours before its start until its end): show the answer bar.
/// </param>
/// <param name="VenueLastReadUtc">When the venue last read the thread: a guest message not after it is "seen".</param>
/// <param name="UnreadCount">What the venue wrote since the guest last read the thread.</param>
/// <param name="Messages">Oldest first.</param>
public sealed record DinerConversationView(
    Guid? ConversationId,
    ConversationSubjectView Subject,
    bool Open,
    DateTime? ClosesAtUtc,
    bool GuestsCanWrite,
    string? VenuePhone,
    bool PendingAsk,
    DateTime? VenueLastReadUtc,
    int UnreadCount,
    IReadOnlyList<DinerConversationMessage> Messages);

/// <summary>A thread as the venue reads it: the guest's view, plus who wrote each venue message and whether the caller may write.</summary>
/// <param name="ConversationId">The thread; absent while nobody has written yet.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="GuestName">The guest, as the booking or order names them. Never their number - the tablet shows it masked elsewhere.</param>
/// <param name="Open">Takes messages now.</param>
/// <param name="ClosesAtUtc">When it closes, or closed. Absent while a takeaway order is still moving.</param>
/// <param name="GuestsCanWrite">Whether the guest may type (the venue's setting).</param>
/// <param name="CanWrite">
/// The caller may write here now: the thread is open and they are a manager, the general manager or the owner - or a
/// waiter while the venue lets waiters write. Never a platform admin, never the kitchen.
/// </param>
/// <param name="VenuePhone">The branch's own number.</param>
/// <param name="PendingAsk">The venue asked "are you coming?" and the guest has not answered, while the answer is still taken.</param>
/// <param name="VenueLastReadUtc">When anybody at the venue last read it.</param>
/// <param name="GuestLastReadUtc">When the guest last read it: a venue message not after it is "seen".</param>
/// <param name="UnreadCount">What the guest wrote since the venue last read the thread.</param>
/// <param name="Messages">Oldest first.</param>
public sealed record StaffConversationView(
    Guid? ConversationId,
    ConversationSubjectView Subject,
    string GuestName,
    bool Open,
    DateTime? ClosesAtUtc,
    bool GuestsCanWrite,
    bool CanWrite,
    string? VenuePhone,
    bool PendingAsk,
    DateTime? VenueLastReadUtc,
    DateTime? GuestLastReadUtc,
    int UnreadCount,
    IReadOnlyList<StaffConversationMessage> Messages);

/// <summary>The answer to the guest's send: the message as written, and its thread.</summary>
/// <param name="ConversationId">The thread - made by this message when it was the first.</param>
/// <param name="Id">The message.</param>
/// <param name="Author">Always <c>guest</c>.</param>
/// <param name="Kind">Always <c>text</c>.</param>
/// <param name="Text">What was stored: the text trimmed.</param>
/// <param name="CreatedAtUtc">When it was written.</param>
/// <param name="WasReplay">The same <c>clientCommandId</c> wrote it before: nothing new was written.</param>
public sealed record DinerMessageResult(
    Guid ConversationId,
    Guid Id,
    string Author,
    string Kind,
    string? Text,
    DateTime CreatedAtUtc,
    bool WasReplay);

/// <summary>The answer to staff's send: the message as written, with who wrote it.</summary>
/// <param name="ConversationId">The thread - made by this message when it was the first.</param>
/// <param name="Id">The message.</param>
/// <param name="Author">Always <c>venue</c>.</param>
/// <param name="Kind">Always <c>text</c>.</param>
/// <param name="Text">What was stored: the text trimmed.</param>
/// <param name="CreatedAtUtc">When it was written.</param>
/// <param name="StaffName">Who wrote it.</param>
/// <param name="WasReplay">The same <c>clientCommandId</c> wrote it before: nothing new was written, nobody told twice.</param>
public sealed record StaffMessageResult(
    Guid ConversationId,
    Guid Id,
    string Author,
    string Kind,
    string? Text,
    DateTime CreatedAtUtc,
    string? StaffName,
    bool WasReplay);

// ---------------------------------------------------------------- summaries

/// <summary>
/// A thread in one line, on a booking (list and detail) and on an Orders-tab takeaway order - the "Messages with the
/// venue" card. Absent when the subject has no thread and takes none.
/// </summary>
/// <param name="Unread">What the venue wrote since the guest last read the thread - "1 new".</param>
/// <param name="LastAuthor"><c>guest</c>, <c>venue</c> or <c>system</c>; absent while nobody has written.</param>
/// <param name="LastKind"><c>text</c>, <c>ask-coming</c>, <c>answer</c> or <c>status</c>; absent while nobody has written.</param>
/// <param name="LastText">The last message's words when it is a <c>text</c>, at most 140 characters. Absent otherwise.</param>
/// <param name="LastAtUtc">When the last message was written; absent while nobody has written.</param>
/// <param name="Open">The thread takes messages now.</param>
public sealed record MessagesSummary(
    int Unread,
    string? LastAuthor,
    string? LastKind,
    string? LastText,
    DateTime? LastAtUtc,
    bool Open);

/// <summary>One of the guest's threads on the unread list.</summary>
/// <param name="ConversationId">The thread.</param>
/// <param name="SubjectKind"><c>reservation</c> or <c>takeaway</c>.</param>
/// <param name="SubjectId">The booking's or the order's id - the thread's route.</param>
/// <param name="VenueName">The venue.</param>
/// <param name="Unread">What the venue wrote since the guest last read it.</param>
/// <param name="LastText">The last message's words when it is a <c>text</c>, at most 140 characters.</param>
/// <param name="LastKind"><c>text</c>, <c>ask-coming</c>, <c>answer</c> or <c>status</c>.</param>
/// <param name="LastAuthor"><c>guest</c>, <c>venue</c> or <c>system</c>.</param>
/// <param name="LastAtUtc">When the last message was written.</param>
/// <param name="Open">The thread takes messages now.</param>
public sealed record DinerUnreadThread(
    Guid ConversationId,
    string SubjectKind,
    Guid SubjectId,
    string VenueName,
    int Unread,
    string? LastText,
    string LastKind,
    string LastAuthor,
    DateTime LastAtUtc,
    bool Open);

/// <summary>The guest's threads that are open or have something unread - the nav dot and the Bookings card rows.</summary>
/// <param name="Total">Unread venue messages across them all; the dot shows while above zero.</param>
/// <param name="Items">Newest first.</param>
public sealed record DinerUnreadMessages(int Total, IReadOnlyList<DinerUnreadThread> Items);

// ---------------------------------------------------------------- the tablet's inbox

/// <summary>A thread's subject on the tablet's inbox row.</summary>
/// <param name="Kind"><c>reservation</c> or <c>takeaway</c>.</param>
/// <param name="Id">The booking's or the order's id.</param>
/// <param name="Label">The table's label for a booking, the code ("T-485") for an order.</param>
/// <param name="GuestName">The guest's name.</param>
/// <param name="LocalTime">A booking's start or an order's ready time, <c>HH:mm</c> on the branch's clock.</param>
/// <param name="LocalDate">A booking's day, <c>yyyy-MM-dd</c>. Absent on an order.</param>
/// <param name="PartySize">A booking's party. Absent on an order.</param>
/// <param name="ItemCount">An order's dishes in all. Absent on a booking.</param>
/// <param name="TotalAmd">An order's total, to pay at pickup. Absent on a booking.</param>
public sealed record InboxSubject(
    string Kind,
    Guid Id,
    string Label,
    string GuestName,
    string LocalTime,
    string? LocalDate,
    int? PartySize,
    int? ItemCount,
    long? TotalAmd);

/// <summary>One thread on the tablet's Messages tab.</summary>
/// <param name="ConversationId">The thread.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="LastMessage">The last message that is not a status line - what the row previews, with who wrote it.</param>
/// <param name="UnreadFromGuest">What the guest wrote since anybody at the venue last read it.</param>
/// <param name="AwaitingGuestAnswer">The last message is the venue's "are you coming?" and the guest has not answered.</param>
/// <param name="OldestUnansweredGuestMessageUtc">
/// The first guest message since the venue last wrote - the one to remind about after <c>remindAfterMinutes</c>.
/// Absent when the venue has the last word.
/// </param>
/// <param name="Open">The thread takes messages now.</param>
public sealed record InboxItem(
    Guid ConversationId,
    InboxSubject Subject,
    StaffConversationMessage LastMessage,
    int UnreadFromGuest,
    bool AwaitingGuestAnswer,
    DateTime? OldestUnansweredGuestMessageUtc,
    bool Open);

/// <summary>The branch's threads for the tablet's Messages tab.</summary>
/// <param name="Items">
/// Threads that are open or had a message in the last 24 hours: those with a guest message unanswered first (the
/// longest waiting first), then the rest, newest first.
/// </param>
/// <param name="UnreadTotal">Unread guest messages across <paramref name="Items"/> - the tab's badge.</param>
/// <param name="RemindAfterMinutes">Ring when a guest message has waited this long unanswered.</param>
/// <param name="Templates">The venue's quick replies; absent for the app's default set.</param>
/// <param name="WaitersCanWrite">Whether waiters may write (managers always may).</param>
/// <param name="AsOfUtc">When it was read.</param>
public sealed record BranchConversations(
    IReadOnlyList<InboxItem> Items,
    int UnreadTotal,
    int RemindAfterMinutes,
    IReadOnlyList<string>? Templates,
    bool WaitersCanWrite,
    DateTime AsOfUtc);

// ---------------------------------------------------------------- the console's settings

/// <summary>The branch's message settings, as the console saves them. All four are required.</summary>
/// <param name="GuestsCanWrite">Guests may type messages. The answer buttons work either way.</param>
/// <param name="WaitersCanWrite">Waiters may write; managers, the general manager and the owner always may.</param>
/// <param name="Templates">The quick replies, at most 8 of 1 to 120 characters; null for the app's default set.</param>
/// <param name="RemindAfterMinutes">1 to 15.</param>
public sealed record MessageSettings(
    bool GuestsCanWrite,
    bool WaitersCanWrite,
    IReadOnlyList<string?>? Templates,
    int RemindAfterMinutes);

/// <summary>The branch's message settings with their bounds.</summary>
/// <param name="GuestsCanWrite">Guests may type messages. Default true.</param>
/// <param name="WaitersCanWrite">Waiters may write. Default true.</param>
/// <param name="Templates">The quick replies; absent for the app's default set.</param>
/// <param name="RemindAfterMinutes">The tablet rings after this long unanswered. Default 3.</param>
/// <param name="Bounds">What each may be.</param>
public sealed record MessageSettingsView(
    bool GuestsCanWrite,
    bool WaitersCanWrite,
    IReadOnlyList<string>? Templates,
    int RemindAfterMinutes,
    MessageSettingsBounds Bounds);

/// <summary>The bounds of the message settings, and of a message.</summary>
public sealed record MessageSettingsBounds(
    int MaxTemplates,
    int TemplateMaxLength,
    int RemindAfterMinutesMin,
    int RemindAfterMinutesMax,
    int TextMaxLength);

// ---------------------------------------------------------------- the service

/// <summary>Guest messages: the diner's threads, the tablet's inbox and threads, the console's settings.</summary>
public interface IGuestMessageService
{
    /// <summary>The thread of one of the caller's bookings or takeaway orders. Not theirs, or closed over 30 days ago: 404.</summary>
    Task<DinerConversationView> GetForGuestAsync(ConversationSubject subject, CancellationToken cancellationToken = default);

    /// <summary>The guest writes. Idempotent on <paramref name="clientCommandId"/>.</summary>
    Task<DinerMessageResult> WriteAsGuestAsync(
        ConversationSubject subject, string? text, Guid clientCommandId, CancellationToken cancellationToken = default);

    /// <summary>The guest read the thread.</summary>
    Task MarkReadByGuestAsync(ConversationSubject subject, CancellationToken cancellationToken = default);

    /// <summary>The guest's open threads and the ones with something unread.</summary>
    Task<DinerUnreadMessages> GetUnreadForGuestAsync(CancellationToken cancellationToken = default);

    /// <summary>The branch's inbox.</summary>
    Task<BranchConversations> ListForBranchAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>A thread as the venue reads it. Waiter or above at the subject's branch.</summary>
    Task<StaffConversationView> GetForStaffAsync(ConversationSubject subject, CancellationToken cancellationToken = default);

    /// <summary>Somebody at the venue writes; the guest is told. Idempotent on <paramref name="clientCommandId"/>.</summary>
    Task<StaffMessageResult> WriteAsStaffAsync(
        ConversationSubject subject, string? text, Guid clientCommandId, CancellationToken cancellationToken = default);

    /// <summary>Somebody at the venue read the thread.</summary>
    Task MarkReadByVenueAsync(ConversationSubject subject, CancellationToken cancellationToken = default);

    Task<MessageSettingsView> GetSettingsAsync(Guid branchId, CancellationToken cancellationToken = default);

    Task<MessageSettingsView> SaveSettingsAsync(Guid branchId, MessageSettings settings, CancellationToken cancellationToken = default);
}
