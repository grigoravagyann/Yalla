using Yalla.Domain.Enums;
using Yalla.Domain.Takeaway;

namespace Yalla.Domain.Messages;

/// <summary>
/// The rules of guest messages that are the product's rather than a venue's: how long a message is, when a thread is
/// open, how fast a guest may write, and what a venue may set. See <c>docs/messages.md</c>.
/// </summary>
/// <remarks>Pure: every rule works on the facts handed in, so each is a unit test.</remarks>
public static class ConversationRules
{
    /// <summary>The longest message, after trimming.</summary>
    public const int MaxTextLength = 500;

    /// <summary>A push and a feed entry show this much of a message, ellipsis included.</summary>
    public const int PreviewLength = 140;

    /// <summary>A thread stays open this long after its booking ends or its order reaches its end.</summary>
    public const int OpenAfterEndHours = 2;

    /// <summary>The guest may read a closed thread for this many days; after that it is not found.</summary>
    public const int ReadableAfterCloseDays = 30;

    /// <summary>
    /// A thread - the venue's record - is kept this long after its last message; the daily sweep then deletes it with its
    /// messages, unless the thread is still open. The console's history reaches back as far.
    /// </summary>
    public const int RetentionDays = 365;

    /// <summary>The tablet's inbox keeps a closed thread this long after its last message.</summary>
    public const int InboxRecentHours = 24;

    /// <summary>At most this many guest messages in any minute.</summary>
    public const int GuestMessagesPerMinute = 5;

    /// <summary>At most this many guest messages in one thread, ever.</summary>
    public const int GuestMessagesPerConversation = 60;

    // ---------------------------------------------------------------- the venue's settings

    public const int MaxTemplates = 8;
    public const int MaxTemplateLength = 120;
    public const int DefaultRemindAfterMinutes = 3;
    public const int MinRemindAfterMinutes = 1;
    public const int MaxRemindAfterMinutes = 15;

    /// <summary>The wire names a refusal names.</summary>
    public static class Fields
    {
        public const string Text = "text";
        public const string Templates = "templates";
        public const string RemindAfterMinutes = "remindAfterMinutes";

        public static string Template(int index) => $"templates[{index}]";
    }

    /// <summary>The wire names of a thread's subject.</summary>
    public static class SubjectKinds
    {
        public const string Reservation = "reservation";
        public const string Takeaway = "takeaway";
    }

    /// <summary>
    /// The text as it is stored: trimmed, 1 to 500 characters. Anything else is a 422 naming <c>text</c>.
    /// </summary>
    /// <exception cref="FieldValidationException">Blank, or longer than 500 after trimming.</exception>
    public static string RequireText(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new FieldValidationException(
                new FieldViolation(Fields.Text, "A message has some text.", FieldBounds.Required));
        }

        if (trimmed.Length > MaxTextLength)
        {
            throw new FieldValidationException(
                new FieldViolation(
                    Fields.Text,
                    $"A message is at most {MaxTextLength} characters.",
                    FieldBounds.Max,
                    Max: MaxTextLength,
                    Value: trimmed.Length));
        }

        return trimmed;
    }

    /// <summary>At most <see cref="PreviewLength"/> characters, an ellipsis replacing the cut.</summary>
    /// <remarks>
    /// Never cuts an emoji in half: a high surrogate left at the cut goes with its pair, or the push and the feed would
    /// carry a lone half that a phone draws as a box.
    /// </remarks>
    public static string Preview(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= PreviewLength)
        {
            return text;
        }

        var keep = PreviewLength - 1;

        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep).TrimEnd(), "…");
    }

    // ---------------------------------------------------------------- when a thread is open

    /// <summary>A booking still expecting its guest - or with them at the table.</summary>
    public static bool BookingIsLive(ReservationStatus status) =>
        status is ReservationStatus.PendingApproval or ReservationStatus.Confirmed or ReservationStatus.Seated;

    /// <summary>
    /// When a booking's thread closes: two hours after the booking ends while it is pending, confirmed or seated; two
    /// hours after it was cancelled, declined, marked a no-show or completed once it was.
    /// </summary>
    /// <param name="status">The booking's status now.</param>
    /// <param name="endUtc">When the booking ends.</param>
    /// <param name="endedAtUtc">
    /// When it ended, for a booking that has: its cancellation, decline or no-show (<c>CancelledAtUtc</c>), or when its
    /// table was given back (completed). Null falls back to <paramref name="endUtc"/>.
    /// </param>
    public static DateTime ReservationClosesAtUtc(ReservationStatus status, DateTime endUtc, DateTime? endedAtUtc) =>
        (BookingIsLive(status) ? endUtc : endedAtUtc ?? endUtc).AddHours(OpenAfterEndHours);

    /// <summary>
    /// When a takeaway order's thread closes: never while it is sent, accepted or ready; two hours after it was
    /// collected, declined, cancelled or marked not collected.
    /// </summary>
    /// <returns>Null while the order is still moving - it has no close yet.</returns>
    public static DateTime? TakeawayClosesAtUtc(TakeawayStatus status, DateTime? endedAtUtc) =>
        TakeawayRules.IsActive(status) ? null : (endedAtUtc ?? DateTime.MinValue).AddHours(OpenAfterEndHours);

    /// <summary>Whether a thread closing at <paramref name="closesAtUtc"/> (null: not yet known) takes messages now.</summary>
    public static bool IsOpen(DateTime? closesAtUtc, DateTime nowUtc) => closesAtUtc is null || nowUtc < closesAtUtc;

    /// <summary>Whether the guest may still read a thread that closes at <paramref name="closesAtUtc"/>.</summary>
    public static bool GuestMayRead(DateTime? closesAtUtc, DateTime nowUtc) =>
        closesAtUtc is null || nowUtc < closesAtUtc.Value.AddDays(ReadableAfterCloseDays);

    /// <summary>
    /// Where a "read" moves a side's read mark to: up to the newest message the reader's screen showed
    /// (<paramref name="upToUtc"/>, its <c>createdAtUtc</c>) - never past now, so a client's clock or a made-up instant
    /// cannot mark unread what has not been written yet. No <paramref name="upToUtc"/>: now. The caller still never moves
    /// a mark backwards.
    /// </summary>
    public static DateTime ReadMarkAt(DateTime nowUtc, DateTime? upToUtc) =>
        upToUtc is { } upTo && upTo < nowUtc ? upTo : nowUtc;

    /// <summary>
    /// Whether "are you coming?" may still be answered: a confirmed booking from two hours before its start until its
    /// end - the window <c>on-my-way</c> and <c>running-late</c> take answers in.
    /// </summary>
    public static bool AnswerAccepted(ReservationStatus status, DateTime startUtc, DateTime endUtc, DateTime nowUtc) =>
        status == ReservationStatus.Confirmed
        && nowUtc >= Occupancy.PreOrderRules.LockedAtUtc(startUtc)
        && nowUtc < endUtc;

    // ---------------------------------------------------------------- how fast a guest may write

    /// <summary>
    /// When the guest's next message would be taken, given when their last ones were sent - or now, when it would be
    /// taken now; null when the thread's own limit is spent.
    /// </summary>
    /// <param name="sentUtc">The guest's text messages in this thread, any order.</param>
    /// <param name="nowUtc">Now.</param>
    public static DateTime? NextGuestMessageAtUtc(IReadOnlyCollection<DateTime> sentUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sentUtc);

        if (sentUtc.Count >= GuestMessagesPerConversation)
        {
            return null;
        }

        var lastMinute = sentUtc.Where(s => s > nowUtc.AddMinutes(-1)).OrderBy(s => s).ToList();

        return lastMinute.Count < GuestMessagesPerMinute
            ? nowUtc
            : lastMinute[lastMinute.Count - GuestMessagesPerMinute].AddMinutes(1);
    }

    // ---------------------------------------------------------------- the venue's settings

    /// <summary>
    /// Every way the settings are out of bounds, each field named: at most eight templates of 1 to 120 characters
    /// after trimming, and a reminder of 1 to 15 minutes.
    /// </summary>
    public static IReadOnlyList<FieldViolation> CheckSettings(IReadOnlyList<string?>? templates, int remindAfterMinutes)
    {
        var violations = new List<FieldViolation>();

        if (templates is not null)
        {
            if (templates.Count > MaxTemplates)
            {
                violations.Add(new FieldViolation(
                    Fields.Templates,
                    $"At most {MaxTemplates} quick replies.",
                    FieldBounds.Max,
                    Max: MaxTemplates,
                    Value: templates.Count));
            }

            for (var i = 0; i < templates.Count; i++)
            {
                var length = templates[i]?.Trim().Length ?? 0;

                if (length == 0)
                {
                    violations.Add(new FieldViolation(Fields.Template(i), "A quick reply has some text.", FieldBounds.Required));
                }
                else if (length > MaxTemplateLength)
                {
                    violations.Add(new FieldViolation(
                        Fields.Template(i),
                        $"A quick reply is at most {MaxTemplateLength} characters.",
                        FieldBounds.Max,
                        Max: MaxTemplateLength,
                        Value: length));
                }
            }
        }

        if (remindAfterMinutes is < MinRemindAfterMinutes or > MaxRemindAfterMinutes)
        {
            violations.Add(new FieldViolation(
                Fields.RemindAfterMinutes,
                $"The reminder is {MinRemindAfterMinutes} to {MaxRemindAfterMinutes} minutes; {remindAfterMinutes} was given.",
                FieldBounds.Range,
                Min: MinRemindAfterMinutes,
                Max: MaxRemindAfterMinutes,
                Value: remindAfterMinutes));
        }

        return violations;
    }
}
