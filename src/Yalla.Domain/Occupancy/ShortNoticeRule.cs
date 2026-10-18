namespace Yalla.Domain.Occupancy;

/// <summary>
/// When a diner's booking is too close to promise without the floor looking first, and how long the
/// floor has to answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> An app booking for tonight is a promise about a table somebody may have just sat down
/// at, and nothing the server knows says whether they will be gone by the time the booking starts. So
/// a short-notice booking is never confirmed blindly: it lands <c>PendingApproval</c>
/// (<c>ApprovalTrigger.ShortNotice</c>) and staff on the floor - waiters included - say yes or no.
/// </para>
/// <para>
/// <b>The diner is not left waiting.</b> The floor has <see cref="AnswerWindowMinutes"/> to answer,
/// or until the start if that is sooner. Past it the booking is declined for them
/// (<c>DeclineReason.NoAnswer</c>) and the diner is told so with an apology, so they can try another
/// time or place while there is still an evening to book.
/// </para>
/// <para>
/// <b>Fixed in code, not a branch setting</b>, and the one place in the booking rules a number is
/// written down rather than read off <c>ReservationPolicy</c>: this is a promise the product makes
/// to every diner, not a preference of the venue's. It applies to every booking a diner makes, from
/// the app or the public page, whatever channel the client says it is; nothing a member of staff
/// enters goes through it.
/// </para>
/// </remarks>
public static class ShortNoticeRule
{
    /// <summary>A booking starting less than this many hours from now is short notice, whatever the date.</summary>
    public const int Hours = 6;

    /// <summary>How long the venue has to answer a short-notice booking before it is declined for them.</summary>
    public const int AnswerWindowMinutes = 15;

    /// <summary>
    /// Whether a booking for <paramref name="startUtc"/> on <paramref name="localDate"/>, asked for at
    /// <paramref name="nowUtc"/>, is short notice: it is for the branch's today, or it starts less
    /// than <see cref="Hours"/> hours from now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Today is the branch's, in its own zone - not UTC's and not the diner's phone's. At 00:30 in
    /// Yerevan "today" has already turned over, so a booking for that day's evening is short notice
    /// and one for tomorrow's is not.
    /// </para>
    /// <para>
    /// The six hours catch what the date cannot: at 22:00 a booking for 01:00 is tomorrow's, and it is
    /// three hours away. Exactly six hours is not short notice; a minute less is.
    /// </para>
    /// </remarks>
    /// <param name="startUtc">The booking's start.</param>
    /// <param name="localDate">The booking's local date at the branch, as the diner picked it.</param>
    /// <param name="nowUtc">When the booking is being made, or moved.</param>
    /// <param name="localToday">The branch's date at <paramref name="nowUtc"/>, in its own zone.</param>
    public static bool Applies(DateTime startUtc, DateOnly localDate, DateTime nowUtc, DateOnly localToday) =>
        localDate == localToday || startUtc - nowUtc < TimeSpan.FromHours(Hours);

    /// <summary>
    /// When the venue's chance to answer a short-notice booking made at <paramref name="nowUtc"/> runs
    /// out: <see cref="AnswerWindowMinutes"/> later, or the start if that is sooner.
    /// </summary>
    /// <remarks>
    /// Never later than the start: a booking still undecided when it begins is a table nobody knows is
    /// promised, which is the situation this rule exists to prevent.
    /// </remarks>
    public static DateTime AnswerDeadline(DateTime nowUtc, DateTime startUtc)
    {
        var window = nowUtc.AddMinutes(AnswerWindowMinutes);

        return window < startUtc ? window : startUtc;
    }
}
