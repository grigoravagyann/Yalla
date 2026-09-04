namespace Yalla.Application.Reservations;

/// <summary>
/// What a history of no-shows costs a diner.
/// </summary>
/// <remarks>
/// <para>
/// One class, one setting block, one method - deliberately. This is a <b>recommendation, not a
/// settled decision</b>: nobody has yet watched a season of real Yerevan bookings to know whether
/// three no-shows in ninety days is the right line, or whether the mechanism earns its keep at
/// all. Keeping it in a single named class with an <see cref="Enabled"/> flag means switching it
/// off is one setting and removing it is one file, rather than an archaeology exercise across the
/// booking service.
/// </para>
/// <para>
/// The consequence is deliberately mild: a diner over the threshold loses <i>instant</i>
/// confirmation, so their booking lands as <c>PendingApproval</c> and a human decides. Nobody is
/// ever banned. Yerevan's dining market is small enough that a wrongly-banned regular is a story
/// the whole city hears, and the count cannot tell a serial no-show from someone whose phone died
/// three times.
/// </para>
/// <para>
/// The window is <b>rolling</b>, not lifetime. A diner who missed three bookings two years ago has
/// served their sentence, and a lifetime counter is a punishment that only grows.
/// </para>
/// </remarks>
public sealed class NoShowPolicy
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "NoShowPolicy";

    /// <summary>
    /// Whether the rule applies at all. Off means the count is never even queried, so switching
    /// it off also removes a database read from every booking.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far back the rolling count looks.</summary>
    public int WindowDays { get; set; } = 90;

    /// <summary>
    /// No-shows within the window <i>above</i> which instant confirmation is withdrawn. Three
    /// means a fourth no-show is the one that costs it.
    /// </summary>
    public int Threshold { get; set; } = 3;

    /// <summary>The earliest booking start the rolling count includes.</summary>
    public DateTime WindowStartUtc(DateTime nowUtc) => nowUtc.AddDays(-WindowDays);

    /// <summary>
    /// Whether this diner has to wait for a human. False whenever the rule is switched off, so
    /// callers need no second check.
    /// </summary>
    public bool RequiresApproval(int noShowsInWindow) => Enabled && noShowsInWindow > Threshold;
}
