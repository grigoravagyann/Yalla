using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>One booking already on a table, as the sequencing rule needs to see it.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="EndUtc">
/// When it ends: the start plus the turn time it was made with, or the time its guest promised to free
/// the table by when that is sooner (see <see cref="PromisedFreeByUtc"/>).
/// </param>
/// <param name="Status">Where it stands. Only a live status holds the table.</param>
/// <param name="PromisedFreeByUtc">
/// The time its guest promised to have left by - accepting a "must free by" when they booked, or
/// saying yes to a later guest's request - or null.
/// </param>
/// <param name="IsBeingAsked">
/// Whether another booking is waiting on this booking's guest - or the floor - to say whether they will
/// free the table in time. One question to one guest at a time.
/// </param>
public readonly record struct SequencedBooking(
    Guid ReservationId,
    DateTime StartUtc,
    DateTime EndUtc,
    ReservationStatus Status,
    DateTime? PromisedFreeByUtc = null,
    bool IsBeingAsked = false);

/// <summary>The party sitting at a table now, as the sequencing rule needs to see it.</summary>
/// <param name="SeatedAtUtc">When they sat down.</param>
/// <param name="ReservationId">The booking they were seated against, or null for a walk-in.</param>
public readonly record struct OpenSitting(DateTime SeatedAtUtc, Guid? ReservationId);

/// <summary>
/// What <see cref="TableSequencing.Judge"/> says about one table at one start time: refused (and
/// why, and because of which booking), or available - outright, with the time the party must leave
/// by, and/or only if the guest before them agrees to leave in time.
/// </summary>
public sealed record TableSequencingVerdict
{
    /// <summary>Why the table cannot be had at this time, or null when it can.</summary>
    public ReservationRejectionReason? Refusal { get; init; }

    /// <summary>The booking behind <see cref="Refusal"/>, when a booking is behind it.</summary>
    public SequencedBooking? RefusedBecauseOf { get; init; }

    /// <summary>The start asked about.</summary>
    public required DateTime StartUtc { get; init; }

    /// <summary>
    /// The end of the sitting the table can give: the start plus the turn time, or
    /// <see cref="MustFreeByUtc"/> when the next booking cuts it short.
    /// </summary>
    public required DateTime EndUtc { get; init; }

    /// <summary>
    /// The booking before, whose guest has to agree to leave by <see cref="EarlierGuestFreeByUtc"/>
    /// first - or null when nobody has to be asked.
    /// </summary>
    public SequencedBooking? Earlier { get; init; }

    /// <summary>The time the guest before is asked to have left by: the start less the clearing time.</summary>
    public DateTime? EarlierGuestFreeByUtc { get; init; }

    /// <summary>The booking after, whose start cuts this sitting short - or null.</summary>
    public SequencedBooking? Next { get; init; }

    /// <summary>
    /// The time this party must have left by - the next booking's start less the clearing time - when
    /// a full sitting would run into it; null when it would not. The diner accepts it before booking.
    /// </summary>
    public DateTime? MustFreeByUtc { get; init; }

    /// <summary>Whether the table cannot be had at this time at all.</summary>
    public bool IsRefused => Refusal is not null;

    /// <summary>Whether the table can be had, but only once the guest before agrees to leave in time.</summary>
    public bool NeedsEarlierGuest => Refusal is null && EarlierGuestFreeByUtc is not null;

    /// <summary>Whether the table can be had, at once - perhaps with a time to leave by - with nobody to ask.</summary>
    public bool IsFree => Refusal is null && EarlierGuestFreeByUtc is null;

    /// <summary>The interval the table can give: <see cref="StartUtc"/> to <see cref="EndUtc"/>.</summary>
    public BookedInterval Interval => new(StartUtc, EndUtc);
}

/// <summary>
/// How bookings follow one another on one table: the rules the booking writer applies under the
/// table's lock, and the single-slot availability, the day slots, a move and the floor's list read -
/// one pure function, so none of them can disagree with another about a table at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fixed in code, not branch settings</b> - like <see cref="ShortNoticeRule"/>, these are the
/// product's promise about how a table's evening goes, not a venue preference. The clearing time
/// between two parties (<c>BufferMinutes</c>, five by default) is still the branch's.
/// </para>
/// <para>
/// For a start <c>S</c> on a table, <c>P</c> is the latest live booking starting at or before <c>S</c>
/// and <c>N</c> the first starting after it:
/// </para>
/// <list type="number">
/// <item><b>Just booked.</b> <c>S &lt; P.start + 90 min</c>: refused, <see cref="ReservationRejectionReason.TableJustBooked"/> -
/// unless <c>P</c> already promised to be gone by <c>S - buffer</c>, as the booking before one that
/// accepted a time to leave by has.</item>
/// <item><b>Too close to the next.</b> <c>S &gt; N.start - 45 min</c>: refused,
/// <see cref="ReservationRejectionReason.NextBookingTooSoon"/>.</item>
/// <item><b>Must free by.</b> A full sitting plus the clearing time would run into <c>N</c>: the sitting
/// ends at <c>N.start - buffer</c>, and the diner accepts that time before booking.</item>
/// <item><b>Ask the guest before.</b> <c>S &lt; P.start + 180 min</c>, or <c>P</c>'s sitting plus clearing
/// time runs into <c>S</c>: the table can be asked for, not had - <c>P</c>'s guest (or the floor) is asked
/// whether they will have left by <c>S - buffer</c>. Unless <c>P</c> already promised to leave by then.
/// While one such question to <c>P</c> is open, nobody else can ask: refused,
/// <see cref="ReservationRejectionReason.EarlierGuestBeingAsked"/>.</item>
/// <item>Otherwise the table is free - after the original overlap rule (<see cref="ReservationOverlap"/>)
/// has had its say over every other booking, and the open sitting's projection over the walk-in at
/// the table.</item>
/// </list>
/// </remarks>
public static class TableSequencing
{
    /// <summary>How long after a booking starts its table cannot even be asked for.</summary>
    public const int JustBookedMinutes = 90;

    /// <summary>The latest a booking may start before the table's next one: this many minutes before it.</summary>
    public const int LatestStartBeforeNextMinutes = 45;

    /// <summary>How long after a booking starts its guest is asked before the table is given to somebody else.</summary>
    public const int AskEarlierGuestWithinMinutes = 180;

    /// <summary>
    /// How far before the start a booking's guests can matter to it: the bookings starting up to this
    /// long before, and any whose sitting still runs into it, are what the rule reads.
    /// </summary>
    public static TimeSpan Lookback(int bufferMinutes) =>
        TimeSpan.FromMinutes(Math.Max(AskEarlierGuestWithinMinutes, bufferMinutes));

    /// <summary>How far after the start a booking can matter to it.</summary>
    public static TimeSpan Lookahead(int turnTimeMinutes, int bufferMinutes) =>
        TimeSpan.FromMinutes(Math.Max(LatestStartBeforeNextMinutes, turnTimeMinutes + bufferMinutes));

    /// <summary>
    /// The verdict for a booking starting at <paramref name="startUtc"/> on a table with these
    /// bookings and this sitting.
    /// </summary>
    /// <param name="startUtc">The start asked about.</param>
    /// <param name="turnTimeMinutes">The branch's turn time: how long a sitting runs.</param>
    /// <param name="bufferMinutes">The branch's clearing time between two parties.</param>
    /// <param name="bookings">
    /// The table's bookings around the start - every one whose sitting ends after
    /// <c>start - </c><see cref="Lookback"/> and that starts before <c>start + </c><see cref="Lookahead"/>
    /// at least. Not live ones are ignored; the booking being moved must already be left out.
    /// </param>
    /// <param name="sitting">The party at the table now, or null.</param>
    public static TableSequencingVerdict Judge(
        DateTime startUtc,
        int turnTimeMinutes,
        int bufferMinutes,
        IEnumerable<SequencedBooking> bookings,
        OpenSitting? sitting = null)
    {
        ArgumentNullException.ThrowIfNull(bookings);

        if (turnTimeMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(turnTimeMinutes), turnTimeMinutes, "Turn time must be greater than zero.");
        }

        if (bufferMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferMinutes), bufferMinutes, "Clearing time must not be negative.");
        }

        var buffer = TimeSpan.FromMinutes(bufferMinutes);
        var naturalEnd = startUtc.AddMinutes(turnTimeMinutes);

        var live = bookings
            .Where(b => ReservationOverlap.Blocks(b.Status))
            .Select(b => WithSitting(b, sitting, turnTimeMinutes))
            .OrderBy(b => b.StartUtc)
            .ThenBy(b => b.ReservationId)
            .ToList();

        var previous = live.LastOrDefault(b => b.StartUtc <= startUtc);
        var hasPrevious = live.Any(b => b.StartUtc <= startUtc);
        var next = live.FirstOrDefault(b => b.StartUtc > startUtc);
        var hasNext = live.Any(b => b.StartUtc > startUtc);

        TableSequencingVerdict Refuse(ReservationRejectionReason reason, SequencedBooking? because) => new()
        {
            Refusal = reason,
            RefusedBecauseOf = because,
            StartUtc = startUtc,
            EndUtc = naturalEnd,
        };

        // Whoever has it before already promised to be gone by this start less the clearing time - by
        // accepting a time to leave by when they booked, or saying yes to a request. Neither the ninety
        // minutes nor a question to them applies: they will not be there.
        var previousPromisedInTime = hasPrevious
                                     && previous.PromisedFreeByUtc is { } promised
                                     && promised <= startUtc - buffer;

        // 1. Whoever has it before is barely seated.
        if (hasPrevious && !previousPromisedInTime && startUtc < previous.StartUtc.AddMinutes(JustBookedMinutes))
        {
            return Refuse(ReservationRejectionReason.TableJustBooked, previous);
        }

        // 2. Not worth sitting down before the next party.
        if (hasNext && startUtc > next.StartUtc.AddMinutes(-LatestStartBeforeNextMinutes))
        {
            return Refuse(ReservationRejectionReason.NextBookingTooSoon, next);
        }

        // 3. A full sitting would run into the next party: it ends when the table has to be cleared
        //    for them, and the diner says so before booking.
        var endUtc = naturalEnd;
        DateTime? mustFreeBy = null;

        if (hasNext && naturalEnd + buffer > next.StartUtc)
        {
            var freeBy = next.StartUtc - buffer;

            // A clearing time of three quarters of an hour or more leaves nothing to sit down for.
            if (freeBy <= startUtc)
            {
                return Refuse(ReservationRejectionReason.NextBookingTooSoon, next);
            }

            mustFreeBy = freeBy;
            endUtc = freeBy;
        }

        // 4. The guest before may still be there: ask them first - unless they have already promised to
        //    be gone in time.
        SequencedBooking? earlier = null;
        DateTime? askFreeBy = null;

        if (hasPrevious)
        {
            var needed = startUtc - buffer;
            var mayStillBeThere = startUtc < previous.StartUtc.AddMinutes(AskEarlierGuestWithinMinutes)
                                  || startUtc < previous.EndUtc + buffer;
            if (mayStillBeThere && !previousPromisedInTime)
            {
                // A clearing time so long it would have them leave before they arrived.
                if (needed <= previous.StartUtc)
                {
                    return Refuse(ReservationRejectionReason.TableJustBooked, previous);
                }

                if (previous.IsBeingAsked)
                {
                    return Refuse(ReservationRejectionReason.EarlierGuestBeingAsked, previous);
                }

                earlier = previous;
                askFreeBy = needed;
            }
        }

        // 5. The original rule over everything else on the table, as a net under the ones above. The
        //    booking being asked is left out: overlapping it is what the question is about.
        var proposed = new BookedInterval(startUtc, endUtc);

        foreach (var booking in live)
        {
            if (earlier is { } asked && booking.ReservationId == asked.ReservationId)
            {
                continue;
            }

            if (ReservationOverlap.Conflicts(
                    proposed, new BookedInterval(booking.StartUtc, booking.EndUtc), bufferMinutes))
            {
                return Refuse(ReservationRejectionReason.TableAlreadyBooked, booking) with { EndUtc = endUtc };
            }
        }

        // 6. A walk-in at the table, projected forward by the turn time. A party seated against one of
        //    the bookings above was judged as that booking.
        if (sitting is { } open
            && !IsSeatedBooking(open, live)
            && SessionOccupancy.Conflicts(proposed, open.SeatedAtUtc, turnTimeMinutes, bufferMinutes))
        {
            return new TableSequencingVerdict
            {
                Refusal = ReservationRejectionReason.TableCurrentlyOccupied,
                StartUtc = startUtc,
                EndUtc = endUtc,
            };
        }

        return new TableSequencingVerdict
        {
            StartUtc = startUtc,
            EndUtc = endUtc,
            Earlier = earlier,
            EarlierGuestFreeByUtc = askFreeBy,
            Next = mustFreeBy is null ? null : next,
            MustFreeByUtc = mustFreeBy,
        };
    }

    /// <summary>
    /// The latest a guest can be asked to answer by: <paramref name="nowUtc"/> plus
    /// <paramref name="windowMinutes"/>, never past the earlier booking's start (once it is under way the
    /// question is the floor's), and never past the later booking's.
    /// </summary>
    public static DateTime AnswerDeadline(
        DateTime nowUtc,
        int windowMinutes,
        DateTime earlierStartUtc,
        DateTime laterStartUtc)
    {
        var deadline = nowUtc.AddMinutes(windowMinutes);

        if (earlierStartUtc > nowUtc && earlierStartUtc < deadline)
        {
            deadline = earlierStartUtc;
        }

        return laterStartUtc < deadline ? laterStartUtc : deadline;
    }

    /// <summary>
    /// The deadline of a wait handed to another guest before it - the one it had, never later than a new
    /// wait would get now (<see cref="AnswerDeadline"/>): a wait never gets a later deadline, and never
    /// runs past the start of the guest it now waits on. One with none (from before every such wait had a
    /// clock) gets the new one.
    /// </summary>
    public static DateTime AnswerDeadlineNoLaterThan(
        DateTime? had,
        DateTime nowUtc,
        int windowMinutes,
        DateTime earlierStartUtc,
        DateTime laterStartUtc)
    {
        var fresh = AnswerDeadline(nowUtc, windowMinutes, earlierStartUtc, laterStartUtc);

        return had is { } kept && kept < fresh ? kept : fresh;
    }

    /// <summary>
    /// A booking seated now and still running past its booked end - a party that sat down late - is
    /// judged as sitting until its projected end, unless its guest promised a time to leave by.
    /// </summary>
    private static SequencedBooking WithSitting(SequencedBooking booking, OpenSitting? sitting, int turnTimeMinutes)
    {
        if (sitting is not { ReservationId: { } seatedFor } open
            || seatedFor != booking.ReservationId
            || booking.PromisedFreeByUtc is not null)
        {
            return booking;
        }

        var projectedEnd = open.SeatedAtUtc.AddMinutes(turnTimeMinutes);

        return projectedEnd > booking.EndUtc ? booking with { EndUtc = projectedEnd } : booking;
    }

    private static bool IsSeatedBooking(OpenSitting sitting, IReadOnlyList<SequencedBooking> live) =>
        sitting.ReservationId is { } id && live.Any(b => b.ReservationId == id);
}
