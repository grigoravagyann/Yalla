using Yalla.Domain.Common;

namespace Yalla.Domain.Messages;

/// <summary>
/// The thread between a venue and a guest about one booking or one takeaway order. There is no thread without a
/// subject, and no general chat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one subject.</b> <see cref="ReservationId"/> or <see cref="TakeawayOrderId"/>, never both and never
/// neither (a check constraint), and each unique (filtered indexes): one booking, one thread.
/// </para>
/// <para>
/// <b>Created lazily</b> - by the first message, the venue's ask or the guest's answer - under the subject's row lock,
/// so two first messages at once make one thread. A booking nobody wrote about has none.
/// </para>
/// <para>
/// <b>Read marks, one per side.</b> <see cref="GuestLastReadUtc"/> and <see cref="VenueLastReadUtc"/>: what the other
/// side wrote after it is unread. Any member of staff reading counts for the venue. Writing marks the writer's side
/// read - nobody has unread messages in a thread they just answered.
/// </para>
/// </remarks>
public sealed class Conversation : Entity
{
    private readonly List<ConversationMessage> _messages = [];

    public Guid BranchId { get; private set; }

    /// <summary>The booking this thread is about. Null for a takeaway order's.</summary>
    public Guid? ReservationId { get; private set; }

    /// <summary>The takeaway order this thread is about. Null for a booking's.</summary>
    public Guid? TakeawayOrderId { get; private set; }

    /// <summary>
    /// The guest's account, as the subject had it. Null for a booking made on the web with no account, and once the
    /// account is deleted - the thread stays the venue's record, without the link to the person.
    /// </summary>
    public Guid? DinerUserId { get; private set; }

    /// <summary>When the newest message was written - the inbox's order, and its "last 24 hours".</summary>
    public DateTime LastMessageUtc { get; private set; }

    /// <summary>When the guest last read the thread; what the venue wrote after it is unread for them.</summary>
    public DateTime? GuestLastReadUtc { get; private set; }

    /// <summary>When anybody at the venue last read the thread; what the guest wrote after it is unread for the venue.</summary>
    public DateTime? VenueLastReadUtc { get; private set; }

    public IReadOnlyCollection<ConversationMessage> Messages => _messages;

    private Conversation()
    {
    }

    private Conversation(Guid branchId, Guid? reservationId, Guid? takeawayOrderId, Guid? dinerUserId, DateTime nowUtc)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        ReservationId = reservationId;
        TakeawayOrderId = takeawayOrderId;
        DinerUserId = dinerUserId is { } account ? Guard.NotEmpty(account, nameof(dinerUserId)) : null;
        StampCreatedAt(nowUtc);
        LastMessageUtc = nowUtc;
    }

    /// <summary>A booking's thread.</summary>
    public static Conversation ForReservation(Guid branchId, Guid reservationId, Guid? dinerUserId, DateTime nowUtc) =>
        new(branchId, Guard.NotEmpty(reservationId, nameof(reservationId)), null, dinerUserId, nowUtc);

    /// <summary>A takeaway order's thread.</summary>
    public static Conversation ForTakeaway(Guid branchId, Guid takeawayOrderId, Guid? dinerUserId, DateTime nowUtc) =>
        new(branchId, null, Guard.NotEmpty(takeawayOrderId, nameof(takeawayOrderId)), dinerUserId, nowUtc);

    /// <summary>The guest writes. Their side is read up to now.</summary>
    /// <param name="text">Already checked: trimmed, 1 to 500 characters.</param>
    /// <param name="clientCommandId">The app's idempotency key.</param>
    /// <param name="nowUtc">Now.</param>
    public ConversationMessage WriteAsGuest(string text, Guid? clientCommandId, DateTime nowUtc)
    {
        var message = ConversationMessage.Text(Id, MessageAuthor.Guest, text, null, null, clientCommandId, nowUtc);
        Add(message, nowUtc);
        GuestLastReadUtc = Later(GuestLastReadUtc, nowUtc);

        return message;
    }

    /// <summary>Somebody at the venue writes. The venue's side is read up to now.</summary>
    public ConversationMessage WriteAsVenue(string text, Guid staffMemberId, string staffName, Guid? clientCommandId, DateTime nowUtc)
    {
        var message = ConversationMessage.Text(Id, MessageAuthor.Venue, text, staffMemberId, staffName, clientCommandId, nowUtc);
        Add(message, nowUtc);
        VenueLastReadUtc = Later(VenueLastReadUtc, nowUtc);

        return message;
    }

    /// <summary>The venue's "are you coming?", with who asked.</summary>
    public ConversationMessage AskComing(Guid staffMemberId, string staffName, DateTime nowUtc)
    {
        if (ReservationId is null)
        {
            throw new DomainStateException("Only a booking's guest is asked whether they are coming.");
        }

        var message = ConversationMessage.Ask(Id, staffMemberId, staffName, nowUtc);
        Add(message, nowUtc);
        VenueLastReadUtc = Later(VenueLastReadUtc, nowUtc);

        return message;
    }

    /// <summary>
    /// The guest answered "are you coming?" - by the app, the link or the notification - and the booking changed: the
    /// answer, then the system line saying what the booking is now.
    /// </summary>
    /// <param name="answer">What they said.</param>
    /// <param name="lateMinutes">For <see cref="GuestAnswer.Late"/>: 10, 15 or 30.</param>
    /// <param name="heldUntilLocal">For <see cref="GuestAnswer.Late"/>: the start plus the minutes, on the branch's wall clock.</param>
    /// <param name="nowUtc">Now.</param>
    public (ConversationMessage Answer, ConversationMessage Status) Answer(
        GuestAnswer answer, int? lateMinutes, TimeOnly? heldUntilLocal, DateTime nowUtc)
    {
        Guard.Defined(answer, nameof(answer));

        if ((answer == GuestAnswer.Late) != lateMinutes is not null)
        {
            throw new ArgumentException("Running late carries its minutes, and only running late does.", nameof(lateMinutes));
        }

        // The status line a tick after the answer: one save writes both, and a database-assigned number is not promised
        // to follow the order rows were added in - the time is.
        var said = ConversationMessage.Answered(Id, answer, lateMinutes, nowUtc);
        var status = ConversationMessage.StatusAfter(
            Id, answer, lateMinutes, answer == GuestAnswer.Late ? heldUntilLocal : null, nowUtc.AddTicks(1));

        Add(said, nowUtc);
        Add(status, nowUtc);
        GuestLastReadUtc = Later(GuestLastReadUtc, nowUtc);

        return (said, status);
    }

    /// <summary>The guest opened the thread.</summary>
    public void MarkReadByGuest(DateTime nowUtc) => GuestLastReadUtc = Later(GuestLastReadUtc, Guard.NotLocalTime(nowUtc, nameof(nowUtc)));

    /// <summary>Somebody at the venue opened the thread.</summary>
    public void MarkReadByVenue(DateTime nowUtc) => VenueLastReadUtc = Later(VenueLastReadUtc, Guard.NotLocalTime(nowUtc, nameof(nowUtc)));

    private void Add(ConversationMessage message, DateTime nowUtc)
    {
        _messages.Add(message);
        LastMessageUtc = Later(LastMessageUtc, Guard.NotLocalTime(nowUtc, nameof(nowUtc)));
    }

    /// <summary>A read mark never moves back - two tablets with clocks a second apart must not unread anything.</summary>
    private static DateTime Later(DateTime? current, DateTime candidate) =>
        current is { } value && value > candidate ? value : candidate;
}

/// <summary>One message in a <see cref="Conversation"/>.</summary>
/// <remarks>
/// <para>
/// <b>Structured, not rendered.</b> Only a <see cref="MessageKind.Text"/> message has a <see cref="Body"/>; an ask, an
/// answer and a status line carry their facts (<see cref="AnswerKind"/>, <see cref="LateMinutes"/>,
/// <see cref="HeldUntilLocal"/>) and every client writes the sentence in its reader's language.
/// </para>
/// <para>
/// <b>Who at the venue</b> is a snapshot: <see cref="StaffName"/> as the person was named when they wrote, for staff
/// eyes only. The guest sees the venue's name.
/// </para>
/// <para>
/// <b>Order is <see cref="Entity.CreatedAtUtc"/>, then <see cref="Sequence"/></b> - a number the database assigns, which
/// breaks a tie between two saves in the same instant. Within one save the number is not promised to follow the order rows
/// were added in, so a status line is stamped a tick after the answer it follows.
/// </para>
/// </remarks>
public sealed class ConversationMessage : Entity
{
    public Guid ConversationId { get; private set; }

    public MessageAuthor Author { get; private set; }

    public MessageKind Kind { get; private set; }

    /// <summary>The words, for a <see cref="MessageKind.Text"/> message; null for every other kind.</summary>
    public string? Body { get; private set; }

    /// <summary>The guest's answer, on an <see cref="MessageKind.Answer"/> and the <see cref="MessageKind.Status"/> after it.</summary>
    public GuestAnswer? AnswerKind { get; private set; }

    /// <summary>"Running late" by this many minutes - 10, 15 or 30.</summary>
    public int? LateMinutes { get; private set; }

    /// <summary>On a status line after "running late": the start plus the minutes, on the branch's wall clock.</summary>
    public TimeOnly? HeldUntilLocal { get; private set; }

    /// <summary>Who at the venue wrote it. Venue messages only.</summary>
    public Guid? StaffMemberId { get; private set; }

    /// <summary>Their name when they wrote it - staff see it, the guest never does.</summary>
    public string? StaffName { get; private set; }

    /// <summary>The writer's idempotency key: unique per thread and author, so a retry answers the message it made.</summary>
    public Guid? ClientCommandId { get; private set; }

    /// <summary>Assigned by the database on insert; breaks a tie between messages of one instant. Never shown.</summary>
    public long Sequence { get; private set; }

    private ConversationMessage()
    {
    }

    private ConversationMessage(Guid conversationId, MessageAuthor author, MessageKind kind, DateTime nowUtc)
        : base(Guid.CreateVersion7())
    {
        ConversationId = Guard.NotEmpty(conversationId, nameof(conversationId));
        Author = Guard.Defined(author, nameof(author));
        Kind = Guard.Defined(kind, nameof(kind));
        StampCreatedAt(nowUtc);
    }

    internal static ConversationMessage Text(
        Guid conversationId,
        MessageAuthor author,
        string text,
        Guid? staffMemberId,
        string? staffName,
        Guid? clientCommandId,
        DateTime nowUtc) =>
        new(conversationId, author, MessageKind.Text, nowUtc)
        {
            Body = ConversationRules.RequireText(text),
            StaffMemberId = staffMemberId is { } id ? Guard.NotEmpty(id, nameof(staffMemberId)) : null,
            StaffName = Guard.OptionalText(staffName, nameof(staffName), FieldLengths.PersonName),
            ClientCommandId = clientCommandId is { } command ? Guard.NotEmpty(command, nameof(clientCommandId)) : null,
        };

    internal static ConversationMessage Ask(Guid conversationId, Guid staffMemberId, string staffName, DateTime nowUtc) =>
        new(conversationId, MessageAuthor.Venue, MessageKind.AskComing, nowUtc)
        {
            StaffMemberId = Guard.NotEmpty(staffMemberId, nameof(staffMemberId)),
            StaffName = Guard.OptionalText(staffName, nameof(staffName), FieldLengths.PersonName),
        };

    internal static ConversationMessage Answered(Guid conversationId, GuestAnswer answer, int? lateMinutes, DateTime nowUtc) =>
        new(conversationId, MessageAuthor.Guest, MessageKind.Answer, nowUtc)
        {
            AnswerKind = answer,
            LateMinutes = lateMinutes,
        };

    internal static ConversationMessage StatusAfter(
        Guid conversationId, GuestAnswer answer, int? lateMinutes, TimeOnly? heldUntilLocal, DateTime nowUtc) =>
        new(conversationId, MessageAuthor.System, MessageKind.Status, nowUtc)
        {
            AnswerKind = answer,
            LateMinutes = lateMinutes,
            HeldUntilLocal = heldUntilLocal,
        };
}
