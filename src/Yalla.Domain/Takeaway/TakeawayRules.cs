using System.Globalization;
using System.Security.Cryptography;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Takeaway;

/// <summary>
/// The rules of takeaway that are the product's rather than a venue's, and the arithmetic of its times: when orders are
/// taken, which pickup times are offered, when the kitchen is full, the code, the accept deadline and the no-show rule.
/// </summary>
/// <remarks>
/// Pure: everything here works on the branch's wall clock and the numbers handed in, so each rule is a unit test. The
/// service reads the calendar, the settings and the orders in progress; this decides. See <c>docs/takeaway.md</c>.
/// </remarks>
public static class TakeawayRules
{
    // ---------------------------------------------------------------- the venue's settings

    public const int DefaultPrepMinutes = 20;
    public const int MinPrepMinutes = 5;
    public const int MaxPrepMinutes = 90;
    public const int PrepMinutesStep = 5;

    public const int DefaultLastOrderMinutes = 30;
    public const int MinLastOrderMinutes = 0;
    public const int MaxLastOrderMinutes = 120;
    public const int LastOrderMinutesStep = 15;

    public const int DefaultMaxInProgress = 5;
    public const int MinMaxInProgress = 1;
    public const int MaxMaxInProgress = 30;

    // ---------------------------------------------------------------- the product's

    /// <summary>An order nobody accepts within this many minutes is declined, and the guest told.</summary>
    public const int AcceptWindowMinutes = 5;

    /// <summary>"No-show" may be marked this long after the order was ready, and not before.</summary>
    public const int NoShowAfterMinutes = 30;

    /// <summary>Pickup slots are offered on the quarter hour.</summary>
    public const int SlotStepMinutes = 15;

    /// <summary>The tablet's ready-time stepper moves in five minutes.</summary>
    public const int ReadyAtStepMinutes = 5;

    /// <summary>The latest a ready time may be set ahead of now: a day's service, not next week.</summary>
    public const int MaxReadyAheadHours = 12;

    /// <summary>How far before now a ready time may be and still be taken: a tablet clock a little behind.</summary>
    public const int ReadyAtClockSkewMinutes = 1;

    public const int MaxLines = 30;
    public const int MinQuantity = 1;
    public const int MaxQuantity = 20;
    public const int MaxTotalQuantity = 60;
    public const int MaxNoteLength = 500;
    public const int MaxDeclineReasonLength = 300;

    /// <summary>How the code begins: "T-482".</summary>
    public const string CodePrefix = "T-";

    /// <summary>"T-" and three digits.</summary>
    public const int CodeLength = 5;

    /// <summary>The wire names a refusal of the shape names.</summary>
    public static class Fields
    {
        public const string Lines = "lines";
        public const string Note = "note";
        public const string Pickup = "pickup";
        public const string PickupAt = "pickup.at";
        public const string ReadyAt = "readyAt";
        public const string Reason = "reason";
        public const string Method = "method";
        public const string PrepMinutes = "prepMinutes";
        public const string LastOrderMinutes = "lastOrderMinutes";
        public const string MaxInProgress = "maxInProgress";

        public static string Quantity(int index) => $"lines[{index}].quantity";
    }

    /// <summary>The statuses an order is "in progress" in, for <c>MaxInProgress</c>: sent or accepted, not yet cooked.</summary>
    public static bool IsInProgress(TakeawayStatus status) => status is TakeawayStatus.Sent or TakeawayStatus.Accepted;

    /// <summary>Still moving: the guest has somewhere to be, or the venue something to do.</summary>
    public static bool IsActive(TakeawayStatus status) =>
        status is TakeawayStatus.Sent or TakeawayStatus.Accepted or TakeawayStatus.Ready;

    /// <summary>A three-digit code, "T-" first: <c>T-007</c> to <c>T-999</c>, unique per branch per local day.</summary>
    public static string CodeFor(int number) =>
        number is < 0 or > 999
            ? throw new ArgumentOutOfRangeException(nameof(number), number, "A takeaway code is three digits.")
            : CodePrefix + number.ToString("D3", CultureInfo.InvariantCulture);

    /// <summary>A random code not in <paramref name="used"/>, or null when the day's thousand are all taken.</summary>
    public static string? FreshCode(IReadOnlySet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        if (used.Count >= 1000)
        {
            return null;
        }

        // Random first, so a stranger cannot guess tonight's next code; a sweep from a random start when the day is busy.
        var start = RandomNumberGenerator.GetInt32(0, 1000);

        for (var i = 0; i < 1000; i++)
        {
            var code = CodeFor((start + i) % 1000);

            if (!used.Contains(code))
            {
                return code;
            }
        }

        return null;
    }

    /// <summary>When the venue's five minutes to answer run out.</summary>
    public static DateTime AcceptDeadlineUtc(DateTime sentAtUtc) => sentAtUtc.AddMinutes(AcceptWindowMinutes);

    /// <summary>
    /// From when "no-show" may be marked: thirty minutes after the order was ready - the later of the time promised and
    /// the moment the kitchen marked it, because the guest was told it is ready only when it was.
    /// </summary>
    public static DateTime NoShowFromUtc(DateTime readyAtUtc, DateTime? markedReadyAtUtc) =>
        (markedReadyAtUtc is { } marked && marked > readyAtUtc ? marked : readyAtUtc).AddMinutes(NoShowAfterMinutes);

    /// <summary>Whether a ready time may be set now: not in the past (a minute of clock skew aside), at most twelve hours on.</summary>
    public static bool ReadyAtIsAcceptable(DateTime readyAtUtc, DateTime nowUtc) =>
        readyAtUtc >= nowUtc.AddMinutes(-ReadyAtClockSkewMinutes) && readyAtUtc <= nowUtc.AddHours(MaxReadyAheadHours);

    /// <summary>The kitchen is full: <paramref name="inProgress"/> orders sent or accepted already, at most <paramref name="maxInProgress"/>.</summary>
    public static bool IsBusy(int inProgress, int maxInProgress) => inProgress >= maxInProgress;

    /// <summary>
    /// Whether a pickup at <paramref name="slotUtc"/> fits beside the orders in progress: each one is cooked in the
    /// <paramref name="prepMinutes"/> before its own pickup, and the slot is offered while fewer than
    /// <paramref name="maxInProgress"/> of those overlap its own cooking.
    /// </summary>
    public static bool SlotFits(DateTime slotUtc, int prepMinutes, IEnumerable<DateTime> pickupsInProgressUtc, int maxInProgress)
    {
        ArgumentNullException.ThrowIfNull(pickupsInProgressUtc);

        var overlapping = pickupsInProgressUtc.Count(p => Math.Abs((p - slotUtc).TotalMinutes) < prepMinutes);

        return overlapping < maxInProgress;
    }

    /// <summary>
    /// When a branch takes takeaway now, and which pickup times it offers - on its wall clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only inside an opening block</b>, the one open now (<see cref="OpeningCalendar.OpenAt"/> - today's, or last
    /// night's running past midnight). The last pickup is that block's close minus <paramref name="lastOrderMinutes"/>;
    /// the earliest is now plus <paramref name="prepMinutes"/>. Orders are taken while the earliest is not after the last.
    /// </para>
    /// <para>
    /// <b>Slots</b> are the quarter hours from the earliest (rounded up) to the last, both included. Closed, the next
    /// opening the calendar knows of from which an order could be taken is <see cref="TakeawayWindow.OpensAtLocal"/>.
    /// </para>
    /// </remarks>
    /// <param name="calendar">The branch's calendar, read from the day before <paramref name="localNow"/> to at least <paramref name="searchDays"/> after.</param>
    /// <param name="localNow">Now, on the branch's wall clock.</param>
    /// <param name="prepMinutes">The venue's preparation time.</param>
    /// <param name="lastOrderMinutes">How long before closing ordering stops.</param>
    /// <param name="searchDays">How many days ahead to look for the next opening when closed.</param>
    public static TakeawayWindow Window(
        OpeningCalendar calendar, DateTime localNow, int prepMinutes, int lastOrderMinutes, int searchDays = 7)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var asap = localNow.AddMinutes(prepMinutes);

        if (calendar.OpenAt(localNow) is { } block)
        {
            var lastPickup = block.Closes.AddMinutes(-lastOrderMinutes);

            if (asap <= lastPickup)
            {
                var slots = new List<DateTime>();

                for (var slot = CeilToStep(asap); slot <= lastPickup; slot = slot.AddMinutes(SlotStepMinutes))
                {
                    slots.Add(slot);
                }

                return new TakeawayWindow(true, asap, lastPickup, slots, null);
            }
        }

        return new TakeawayWindow(false, null, null, [], NextOpening(calendar, localNow, prepMinutes, lastOrderMinutes, searchDays));
    }

    /// <summary>The first opening after <paramref name="localNow"/> long enough to take an order in.</summary>
    private static DateTime? NextOpening(
        OpeningCalendar calendar, DateTime localNow, int prepMinutes, int lastOrderMinutes, int searchDays)
    {
        var today = DateOnly.FromDateTime(localNow);

        for (var day = 0; day <= searchDays; day++)
        {
            foreach (var interval in calendar.IntervalsOn(today.AddDays(day)))
            {
                if (interval.Opens > localNow
                    && interval.Opens.AddMinutes(prepMinutes) <= interval.Closes.AddMinutes(-lastOrderMinutes))
                {
                    return interval.Opens;
                }
            }
        }

        return null;
    }

    /// <summary>Up to the next quarter hour, seconds dropped; a time on the quarter stays.</summary>
    public static DateTime CeilToStep(DateTime local)
    {
        var step = TimeSpan.FromMinutes(SlotStepMinutes).Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;
        var ticks = (local.Ticks + minute - 1) / minute * minute;

        return new DateTime((ticks + step - 1) / step * step, local.Kind);
    }

    /// <summary>A pickup slot as the wire writes it: local "HH:mm".</summary>
    public static string SlotText(DateTime local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Every way an order's shape breaks a bound, named by wire field. Empty when it is fine.</summary>
    public static IReadOnlyList<FieldViolation> Check(IReadOnlyList<int> quantities, string? note)
    {
        ArgumentNullException.ThrowIfNull(quantities);
        var violations = new List<FieldViolation>();

        if (quantities.Count is 0 or > MaxLines)
        {
            violations.Add(new FieldViolation(
                Fields.Lines,
                $"A takeaway order has 1 to {MaxLines} lines; {quantities.Count} were sent.",
                FieldBounds.Range,
                Min: 1,
                Max: MaxLines,
                Value: quantities.Count));
        }

        for (var i = 0; i < quantities.Count; i++)
        {
            if (quantities[i] is < MinQuantity or > MaxQuantity)
            {
                violations.Add(new FieldViolation(
                    Fields.Quantity(i),
                    $"A line is {MinQuantity} to {MaxQuantity} of a dish; {quantities[i]} was sent.",
                    FieldBounds.Range,
                    Min: MinQuantity,
                    Max: MaxQuantity,
                    Value: quantities[i]));
            }
        }

        var total = quantities.Sum(q => (long)Math.Max(0, q));

        if (quantities.Count is > 0 and <= MaxLines && total > MaxTotalQuantity)
        {
            violations.Add(new FieldViolation(
                Fields.Lines,
                $"A takeaway order is at most {MaxTotalQuantity} dishes in all; {total} were sent.",
                FieldBounds.Max,
                Max: MaxTotalQuantity,
                Value: total));
        }

        if (note is { Length: > MaxNoteLength })
        {
            violations.Add(new FieldViolation(
                Fields.Note,
                $"The note is at most {MaxNoteLength} characters.",
                FieldBounds.Max,
                Max: MaxNoteLength,
                Value: note.Length));
        }

        return violations;
    }

    /// <summary>Every setting out of its bounds or off its step, named by wire field. Empty when they are fine.</summary>
    public static IReadOnlyList<FieldViolation> CheckSettings(int prepMinutes, int lastOrderMinutes, int maxInProgress)
    {
        var violations = new List<FieldViolation>();

        Stepped(violations, Fields.PrepMinutes, prepMinutes, MinPrepMinutes, MaxPrepMinutes, PrepMinutesStep);
        Stepped(violations, Fields.LastOrderMinutes, lastOrderMinutes, MinLastOrderMinutes, MaxLastOrderMinutes, LastOrderMinutesStep);
        Stepped(violations, Fields.MaxInProgress, maxInProgress, MinMaxInProgress, MaxMaxInProgress, 1);

        return violations;
    }

    private static void Stepped(List<FieldViolation> violations, string field, int value, int min, int max, int step)
    {
        if (value < min || value > max)
        {
            violations.Add(new FieldViolation(
                field, $"{field} is {min} to {max}; {value} was given.", FieldBounds.Range, Min: min, Max: max, Value: value));
        }
        else if ((value - min) % step != 0)
        {
            violations.Add(new FieldViolation(
                field, $"{field} moves in steps of {step} from {min}; {value} was given.", FieldBounds.Range, Min: min, Max: max, Value: value));
        }
    }
}

/// <summary>When a branch takes takeaway, on its wall clock.</summary>
/// <param name="Open">Orders are taken now.</param>
/// <param name="AsapReadyLocal">"As soon as possible": now plus the preparation time. Absent when closed.</param>
/// <param name="LastPickupLocal">The latest pickup: the open block's close minus the last-order minutes. Absent when closed.</param>
/// <param name="SlotsLocal">The quarter hours offered, earliest first. Empty when closed.</param>
/// <param name="OpensAtLocal">Closed: the next opening an order could be taken from. Absent when open, or none within the search.</param>
public sealed record TakeawayWindow(
    bool Open,
    DateTime? AsapReadyLocal,
    DateTime? LastPickupLocal,
    IReadOnlyList<DateTime> SlotsLocal,
    DateTime? OpensAtLocal);
