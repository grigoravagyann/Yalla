using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Xunit.Abstractions;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The availability read model, against a real SQL Server.
/// </summary>
/// <remarks>
/// Three separate things are worth proving here and each fails silently in its own way. That the
/// bookings join is a <b>left</b> join, because an inner one drops precisely the tables with
/// nothing booked - the most available tables in the room. That the window a diner is offered is
/// the real one. And that the whole answer is still <b>one</b> round trip, because a projection
/// that quietly becomes a query per table looks identical in every assertion and only shows up on
/// the pilot venue's Friday night.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class AvailabilityQueryTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    /// <summary>12:00 on Thursday 10 September 2026 in Yerevan (UTC+4).</summary>
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    private static readonly TimeOnly SixPm = new(18, 0);

    [SkippableFact]
    public async Task A_table_with_no_upcoming_bookings_still_appears()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // The left-join guard. One table is booked, two are not, and it is the two that an
        // accidental inner join would silently drop.
        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);

        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(21, 0)));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        Assert.NotNull(availability);
        Assert.Equal(3, availability.Tables.Count);
        Assert.All(branch.TableIds, id => Assert.Contains(availability.Tables, t => t.TableId == id));

        var untouched = availability.Tables.Where(t => t.TableId != branch.TableIds[0]).ToList();

        Assert.Equal(2, untouched.Count);
        Assert.All(untouched, t =>
        {
            Assert.True(t.IsAvailable);
            Assert.Null(t.UnavailableReason);

            // Nothing booked after them at all, so nothing closes the window. Said outright
            // rather than left for the client to infer from a null.
            Assert.NotNull(t.Window);
            Assert.True(t.Window!.HasNoLaterBooking);
            Assert.Null(t.Window.AvailableUntilUtc);
            Assert.Null(t.Window.WindowMinutes);
            Assert.False(t.Window.IsShorterThanTurnTime);
        });
    }

    [SkippableFact]
    public async Task A_table_with_a_later_booking_returns_the_window_the_diner_may_have()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);

        // A 20:00 booking on table 1. The diner asking about 18:00 may have the table until
        // 19:55 - 20:00 less the branch's 5 minutes of clearing time.
        var later = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(20, 0)));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        Assert.NotNull(availability);

        var limited = availability.Tables.Single(t => t.TableId == branch.TableIds[0]);

        Assert.True(limited.IsAvailable);
        Assert.Equal(later.Id, limited.NextReservationId);
        Assert.Equal(later.StartUtc, limited.NextReservationStartUtc);

        var window = limited.Window!;
        Assert.False(window.HasNoLaterBooking);
        Assert.Equal(availability.RequestedStartUtc, window.AvailableFromUtc);
        Assert.Equal(later.StartUtc.AddMinutes(-availability.BufferMinutes), window.AvailableUntilUtc);

        // 18:00 to 19:55, as the diner reads it off the screen before confirming.
        Assert.Equal(SixPm, window.AvailableFromLocal);
        Assert.Equal(new TimeOnly(19, 55), window.AvailableUntilLocal);
        Assert.Equal(115, window.WindowMinutes);

        // 115 minutes against a 90-minute turn time is not short, so the sheet says nothing - and a
        // full sitting ends before the next one less clearing, so there is no time to leave by either.
        Assert.False(window.IsShorterThanTurnTime);
        Assert.Null(limited.MustFreeByUtc);
        Assert.False(limited.IsRequestable);

        // The other table has no limit, which is exactly the comparison the screen exists to let
        // a diner make.
        var unlimited = availability.Tables.Single(t => t.TableId == branch.TableIds[1]);

        Assert.True(unlimited.Window!.HasNoLaterBooking);
        Assert.Null(unlimited.Window.AvailableUntilUtc);
    }

    [SkippableFact]
    public async Task A_table_whose_next_booking_starts_within_forty_five_minutes_is_refused_as_too_soon()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);

        // 18:30 is thirty minutes after an 18:00 start - less than the forty-five the table's evening
        // needs before a next booking (TableSequencing), so 18:00 is not on offer at all.
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(18, 30)));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        var taken = availability!.Tables.Single(t => t.TableId == branch.TableIds[0]);

        Assert.False(taken.IsAvailable);
        Assert.Equal(ReservationRejectionReason.NextBookingTooSoon, taken.UnavailableReason);

        // No window at all, because there is no offer to make - rather than a window too short
        // to use, which is what a zero-length one would read as.
        Assert.Null(taken.Window);
    }

    [SkippableFact]
    public async Task Each_table_carries_its_own_specific_reason_rather_than_a_bare_no()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        var eightSeater = await TestBranchBuilder.AddTableAsync(db, branch, "8A", seats: 8);
        var barStool = await TestBranchBuilder.AddTableAsync(db, branch, "B1", seats: 2, isBookable: false);
        var broken = await TestBranchBuilder.AddTableAsync(db, branch, "X1", seats: 4);

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .MarkOutOfServiceAsync(
                new TableStateCommand(branch.BranchId, broken.Id, Guid.NewGuid(), "broken chair"));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        Assert.NotNull(availability);

        // The four-seater takes two people; the eight-seater leaves six seats empty against a
        // limit of two; the bar stool never takes bookings; the broken table is out of service.
        Assert.True(availability.Tables.Single(t => t.TableId == branch.FirstTableId).IsAvailable);

        Assert.Equal(
            ReservationRejectionReason.SeatOverhangExceeded,
            availability.Tables.Single(t => t.TableId == eightSeater.Id).UnavailableReason);

        Assert.Equal(
            ReservationRejectionReason.TableNotBookable,
            availability.Tables.Single(t => t.TableId == barStool.Id).UnavailableReason);

        Assert.Equal(
            ReservationRejectionReason.TableOutOfService,
            availability.Tables.Single(t => t.TableId == broken.Id).UnavailableReason);
    }

    [SkippableFact]
    public async Task A_party_over_the_branch_threshold_is_told_before_it_confirms()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);
        await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 9, BookingDate, SixPm));

        // Not a refusal, and worth saying before the diner commits rather than after.
        Assert.NotNull(availability);
        Assert.All(availability.Tables, t => Assert.True(t.RequiresApproval));
    }

    /// <summary>
    /// A branch that approves every booking by hand says so before the diner confirms, whatever the
    /// party size - and the booking does land waiting, so the promise is the truth.
    /// </summary>
    /// <remarks>
    /// Only the party-size threshold used to set <c>RequiresApproval</c>, while booking at a branch
    /// with auto-confirm off lands every booking pending. The sheet and the confirm button promised
    /// an instant confirmation the branch was never going to give.
    /// </remarks>
    [SkippableFact]
    public async Task A_branch_that_approves_every_booking_says_so_before_the_diner_confirms()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(
            db, tableCount: 1, policy: ApprovingEverything(ReservationPolicy.DefaultFor(VenueType.Restaurant)));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        Assert.NotNull(availability);

        var table = Assert.Single(availability.Tables);

        Assert.True(table.IsAvailable);
        Assert.True(
            table.RequiresApproval,
            "Two people, under any threshold, at a branch that approves every booking by hand.");

        var booked = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, table.TableId, SixPm));

        Assert.Equal(ReservationStatus.PendingApproval, booked.Status);
    }

    [SkippableFact]
    public async Task A_request_the_branch_cannot_serve_at_all_says_so_once_rather_than_per_table()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);

        await using var readDb = fixture.CreateContext(clock);

        // 03:00, when the branch has been shut for four hours.
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(
                new AvailabilityRequest(branch.BranchId, 2, BookingDate, new TimeOnly(3, 0)));

        Assert.NotNull(availability);
        Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, availability.UnavailableReason);

        // The floor is still drawn - the diner picks another time, not another restaurant.
        Assert.Equal(3, availability.Tables.Count);
        Assert.All(availability.Tables, t =>
            Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, t.UnavailableReason));
    }

    [SkippableFact]
    public async Task The_derived_state_is_computed_for_the_requested_time_and_not_for_now()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        // Booked at 18:00. Asked about 17:56, which is inside the branch's 5-minute turnaround.
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId, SixPm));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(
                new AvailabilityRequest(branch.BranchId, 2, BookingDate, new TimeOnly(17, 56)));

        var table = availability!.Tables.Single();

        // The table is physically free right now and will be for days, but the question was about
        // 17:56 - and at 17:56 it is about to be somebody else's.
        Assert.Equal(TableStatus.Free, table.PhysicalStatus);
        Assert.Equal(DerivedTableState.ReservedSoon, table.State);
    }

    [SkippableFact]
    public async Task Omitting_the_date_and_time_answers_for_now_at_the_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // What somebody standing outside the door is asking. Yerevan is UTC+4, so 08:00Z is 12:00.
        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2));

        Assert.NotNull(availability);
        Assert.Equal(new DateOnly(2026, 9, 10), availability.LocalDate);
        Assert.Equal(new TimeOnly(12, 0), availability.LocalTime);

        // Inside the minimum lead time, which is the honest answer for "right now".
        Assert.Equal(ReservationRejectionReason.LeadTimeTooShort, availability.UnavailableReason);
    }

    [SkippableFact]
    public async Task A_date_with_no_time_still_answers_about_that_date()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // "What does that evening look like?" with no time named. The bookings around the
        // requested date have to be fetched, not the ones around today - which is what happens if
        // the query anchors its search window on the clock whenever either half is missing.
        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        var noon = new TimeOnly(12, 0);

        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId, noon));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate));

        Assert.NotNull(availability);
        Assert.Equal(BookingDate, availability.LocalDate);

        // Noon at the branch on the requested date, and the booking made above is right there - it
        // starts then, so the table is refused as just booked.
        Assert.Equal(noon, availability.LocalTime);
        Assert.Equal(
            ReservationRejectionReason.TableJustBooked,
            availability.Tables.Single().UnavailableReason);
    }

    [SkippableFact]
    public async Task The_whole_answer_arrives_in_one_round_trip()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 8);

        // Bookings on half the tables, so an N+1 has something to be tempted by - each its own diner's,
        // since one diner holds one table at a branch at a time.
        foreach (var tableId in branch.TableIds.Take(4))
        {
            await fixture.CreateReservationService(db, clock, TestActor.Diner())
                .CreateAsync(Booking(branch, tableId, new TimeOnly(21, 0)));
        }

        var counter = new CommandCounter();
        await using var readDb = fixture.CreateContext(clock, counter);

        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        Assert.NotNull(availability);
        Assert.Equal(8, availability.Tables.Count);

        // Eight tables, one statement. A projection that regressed into a query per table would
        // return byte-identical results and only show up here.
        Assert.Equal(1, counter.Count);
    }

    [SkippableFact]
    public async Task The_generated_sql_is_reportable_and_joins_the_bookings_as_a_left_join()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);

        var sql = fixture.CreateAvailabilityQuery(db, clock)
            .GetAvailabilityQuerySql(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        // Printed so "is this still one statement, and still a left join?" can be answered from a
        // test run rather than a profiler.
        output.WriteLine(sql);

        Assert.Contains("[Reservations]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[DiningTables]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);

        // No inner join anywhere: that is the mistake that silently drops the tables with nothing
        // booked, which are the ones a diner most wants to see.
        Assert.DoesNotContain("INNER JOIN [Reservations]", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- one day, every half hour

    /// <summary>
    /// The time picker and the room screen never disagree: every time the day lists is answered the
    /// same way by the single-slot read for that time.
    /// </summary>
    /// <remarks>
    /// Asked of today - where the lead time strikes the morning and a party seated now runs into the
    /// next sittings - and of a later day with bookings, a table that is too big, one that takes no
    /// bookings and one out of service. Every listed time is put to the single-slot read; every time
    /// left out must be one the branch is shut for.
    /// </remarks>
    [SkippableFact]
    public async Task Every_time_the_day_lists_agrees_with_the_single_slot_read()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);

        await TestBranchBuilder.AddTableAsync(db, branch, "8A", seats: 8);
        await TestBranchBuilder.AddTableAsync(db, branch, "B1", seats: 2, isBookable: false);
        var broken = await TestBranchBuilder.AddTableAsync(db, branch, "X1", seats: 4);

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .MarkOutOfServiceAsync(new TableStateCommand(branch.BranchId, broken.Id, Guid.NewGuid(), "broken chair"));

        // Somebody sitting at table 3 right now, which runs into this afternoon's sittings.
        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, branch.TableIds[2], 2, Guid.CreateVersion7()));

        var booker = fixture.CreateReservationService(db, clock, TestActor.Diner());
        await booker.CreateAsync(Booking(branch, branch.TableIds[0], SixPm));
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(19, 0)));
        await booker.CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(13, 0)) with { LocalDate = new DateOnly(2026, 9, 10) });

        await using var readDb = fixture.CreateContext(clock);
        var query = fixture.CreateAvailabilityQuery(readDb, clock);

        foreach (var date in new[] { new DateOnly(2026, 9, 10), BookingDate })
        {
            foreach (var partySize in new[] { 2, 4 })
            {
                var day = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, date, partySize));

                Assert.NotNull(day);
                Assert.Equal(15, day.StepMinutes);
                Assert.NotEmpty(day.Slots);

                for (var minutes = 0; minutes < 24 * 60; minutes += DaySlotStep.Minutes)
                {
                    var time = new TimeOnly(minutes / 60, minutes % 60);
                    var single = await query.GetAvailabilityAsync(
                        new AvailabilityRequest(branch.BranchId, partySize, date, time));

                    var slot = day.Slots.SingleOrDefault(s => s.Time == time.ToString("HH\\:mm"));

                    if (slot is null)
                    {
                        // Left out only when the branch is shut for that sitting - which the single
                        // read reports as such, or as too soon when it is both.
                        Assert.Contains(
                            single!.UnavailableReason,
                            new ReservationRejectionReason?[]
                            {
                                ReservationRejectionReason.OutsideOpeningHours,
                                ReservationRejectionReason.LeadTimeTooShort,
                            });

                        continue;
                    }

                    var offered = single!.Tables.Where(t => t.IsAvailable).ToList();
                    var free = offered.Count(t => !t.IsRequestable);
                    var requestable = offered.Count(t => t.IsRequestable);

                    Assert.Equal(single.RequestedStartUtc, slot.StartUtc);
                    Assert.Equal(free, slot.FreeTableCount);
                    Assert.Equal(requestable, slot.RequestableTableCount);
                    Assert.Equal(offered.Count > 0, slot.IsAvailable);
                    Assert.Equal(free == 0 && requestable > 0, slot.IsRequestable);

                    // A table that has to be asked for always waits; the others say what the slot says
                    // when it is not only asked for.
                    Assert.All(single.Tables.Where(t => t.IsRequestable), t => Assert.True(t.RequiresApproval));

                    if (!slot.IsRequestable)
                    {
                        Assert.All(single.Tables.Where(t => !t.IsRequestable), t => Assert.Equal(t.RequiresApproval, slot.RequiresApproval));
                    }
                    else
                    {
                        Assert.True(slot.RequiresApproval);
                    }

                    // The latest time to leave by, when no table on offer gives a full sitting.
                    var expectedFreeBy = offered.Count > 0 && offered.All(t => t.MustFreeByUtc is not null)
                        ? offered.Max(t => t.MustFreeByUtc)
                        : null;

                    Assert.Equal(expectedFreeBy, slot.MustFreeByUtc);

                    if (single.UnavailableReason is { } refused)
                    {
                        Assert.Equal(refused, slot.UnavailableReason);
                    }
                    else if (!slot.IsAvailable)
                    {
                        Assert.Contains(slot.UnavailableReason, single.Tables.Select(t => t.UnavailableReason));
                    }
                    else
                    {
                        Assert.Null(slot.UnavailableReason);
                    }
                }
            }
        }

        // The morning of today is inside the lead time, and is listed struck with that reason.
        var today = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, new DateOnly(2026, 9, 10), 2));
        var tenOClock = today!.Slots.First();

        Assert.Equal("10:00", tenOClock.Time);
        Assert.False(tenOClock.IsAvailable);
        Assert.Equal(ReservationRejectionReason.LeadTimeTooShort, tenOClock.UnavailableReason);
        Assert.Null(today.UnavailableReason);
    }

    /// <summary>
    /// The only table that fits the party is booked, so the times its evening refuses are struck - for
    /// the table's booking, not as "too small", although the two-seaters that do not fit outnumber it.
    /// </summary>
    [SkippableFact]
    public async Task A_time_whose_only_fitting_table_is_booked_is_struck_for_the_booking_not_as_too_small()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        await TestBranchBuilder.AddTableAsync(db, branch, "2A", seats: 2);
        await TestBranchBuilder.AddTableAsync(db, branch, "2B", seats: 2);

        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId, SixPm, partySize: 4));

        await using var readDb = fixture.CreateContext(clock);
        var day = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, BookingDate, 4));

        Assert.NotNull(day);
        Assert.Null(day.UnavailableReason);

        // Open 10:00-23:00 with a 90-minute turn: the last sitting that fits starts at 21:30, and every
        // quarter of an hour from 10:00 to then is listed.
        Assert.Equal("10:00", day.Slots[0].Time);
        Assert.Equal("21:30", day.Slots[^1].Time);
        Assert.Equal(47, day.Slots.Count);

        // The 18:00 refuses less than forty-five minutes before it and ninety after its start; from
        // 19:30 its guest can be asked, and before 17:30 a sitting just has a time to leave by.
        var struck = day.Slots.Where(s => !s.IsAvailable).ToDictionary(s => s.Time, s => s.UnavailableReason);

        Assert.Equal(
            new Dictionary<string, ReservationRejectionReason?>
            {
                ["17:30"] = ReservationRejectionReason.NextBookingTooSoon,
                ["17:45"] = ReservationRejectionReason.NextBookingTooSoon,
                ["18:00"] = ReservationRejectionReason.TableJustBooked,
                ["18:15"] = ReservationRejectionReason.TableJustBooked,
                ["18:30"] = ReservationRejectionReason.TableJustBooked,
                ["18:45"] = ReservationRejectionReason.TableJustBooked,
                ["19:00"] = ReservationRejectionReason.TableJustBooked,
                ["19:15"] = ReservationRejectionReason.TableJustBooked,
            },
            struck);
        Assert.All(day.Slots.Where(s => !s.IsAvailable), s => Assert.Equal(0, s.FreeTableCount));

        Assert.Equal("17:55", day.Slots.Single(s => s.Time == "17:15").MustFreeByLocal);
        Assert.True(day.Slots.Single(s => s.Time == "19:30").IsRequestable);

        var free = day.Slots.Single(s => s.Time == "16:00");

        Assert.True(free.IsAvailable);
        Assert.Equal(1, free.FreeTableCount);
        Assert.Null(free.UnavailableReason);
        Assert.Equal(new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc), free.StartUtc);
    }

    /// <summary>
    /// A diner changing their booking sees their own table free - in the day and at the slot - and
    /// every other booking still counts.
    /// </summary>
    [SkippableFact]
    public async Task The_diners_own_booking_can_be_left_out_of_both_reads()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);
        var booker = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var mine = await booker.CreateAsync(Booking(branch, branch.TableIds[0], SixPm));
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[1], SixPm));

        await using var readDb = fixture.CreateContext(clock);
        var query = fixture.CreateAvailabilityQuery(readDb, clock);

        var everything = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, BookingDate, 2));
        var withoutMine = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, BookingDate, 2, mine.Id));

        Assert.False(everything!.Slots.Single(s => s.Time == "18:00").IsAvailable);

        var six = withoutMine!.Slots.Single(s => s.Time == "18:00");
        Assert.True(six.IsAvailable);
        Assert.Equal(1, six.FreeTableCount);

        var slot = await query.GetAvailabilityAsync(
            new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm, ExcludeReservationId: mine.Id));

        Assert.True(slot!.Tables.Single(t => t.TableId == branch.TableIds[0]).IsAvailable);
        Assert.Equal(
            ReservationRejectionReason.TableJustBooked,
            slot.Tables.Single(t => t.TableId == branch.TableIds[1]).UnavailableReason);

        // An id that is not a booking here changes nothing.
        var stranger = await query.GetDaySlotsAsync(
            new DaySlotsRequest(branch.BranchId, BookingDate, 2, Guid.CreateVersion7()));

        Assert.Equal(
            everything.Slots.Select(s => (s.Time, s.FreeTableCount)),
            stranger!.Slots.Select(s => (s.Time, s.FreeTableCount)));
    }

    /// <summary>A day the branch cannot serve at all says so once, at the top.</summary>
    [SkippableFact]
    public async Task A_day_the_branch_cannot_serve_says_so_once()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);

        // Shut on Sundays: 13 September 2026 is one.
        await db.OpeningHours
            .Where(h => h.BranchId == branch.BranchId && h.Day == DayOfWeek.Sunday)
            .ExecuteDeleteAsync();

        await using var readDb = fixture.CreateContext(clock);
        var query = fixture.CreateAvailabilityQuery(readDb, clock);

        var sunday = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, BookingDate, 2));

        Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, sunday!.UnavailableReason);
        Assert.Empty(sunday.Slots);

        // Yesterday: every time listed, every one too soon.
        var yesterday = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, new DateOnly(2026, 9, 9), 2));

        Assert.Equal(ReservationRejectionReason.LeadTimeTooShort, yesterday!.UnavailableReason);
        Assert.NotEmpty(yesterday.Slots);
        Assert.All(yesterday.Slots, s => Assert.Equal(ReservationRejectionReason.LeadTimeTooShort, s.UnavailableReason));

        // Further out than the branch's fourteen days.
        var tooFar = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, new DateOnly(2026, 9, 30), 2));

        Assert.Equal(ReservationRejectionReason.OutsideBookingWindow, tooFar!.UnavailableReason);
        Assert.All(tooFar.Slots, s => Assert.False(s.IsAvailable));

        // A party nothing seats is a reason per time, not one for the day: a smaller party could come.
        var twelve = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, new DateOnly(2026, 9, 12), 12));

        Assert.Null(twelve!.UnavailableReason);
        Assert.All(twelve.Slots, s =>
        {
            Assert.False(s.IsAvailable);
            Assert.Equal(ReservationRejectionReason.PartyExceedsCapacity, s.UnavailableReason);
            Assert.True(s.RequiresApproval, "Twelve is over the branch's threshold of eight.");
        });

        Assert.Null(await query.GetDaySlotsAsync(new DaySlotsRequest(Guid.NewGuid(), BookingDate, 2)));
    }

    [SkippableFact]
    public async Task The_whole_day_arrives_in_one_round_trip()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 8);

        // Each its own diner's: one diner holds one table at a branch at a time.
        foreach (var tableId in branch.TableIds.Take(4))
        {
            await fixture.CreateReservationService(db, clock, TestActor.Diner())
                .CreateAsync(Booking(branch, tableId, new TimeOnly(21, 0)));
        }

        var counter = new CommandCounter();
        await using var readDb = fixture.CreateContext(clock, counter);
        var query = fixture.CreateAvailabilityQuery(readDb, clock);

        var day = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, BookingDate, 2));

        Assert.NotNull(day);
        Assert.Equal(47, day.Slots.Count);

        // Ninety-six times, eight tables, one statement.
        Assert.Equal(1, counter.Count);

        var sql = query.GetDaySlotsQuerySql(new DaySlotsRequest(branch.BranchId, BookingDate, 2, Guid.CreateVersion7()));
        output.WriteLine(sql);

        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INNER JOIN [Reservations]", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The given policy with auto-confirm switched off, and nothing else changed.</summary>
    private static ReservationPolicy ApprovingEverything(ReservationPolicy policy) =>
        new(
            policy.TurnTimeMinutes,
            policy.BufferMinutes,
            policy.GraceMinutes,
            policy.LateNudgeAfterMinutes,
            policy.GraceExtensionMinutes,
            policy.MinLeadMinutes,
            policy.BookingWindowDays,
            policy.CancellationDeadlineMinutes,
            autoConfirm: false,
            policy.ServiceChargePercent,
            policy.PricesIncludeVat,
            policy.MaxSeatOverhang,
            policy.ApprovalRequiredAbovePartySize,
            policy.WalkInHoldbackMinutes,
            policy.ReminderHoursBefore);

    private static CreateReservationCommand Booking(
        TestBranch branch,
        Guid tableId,
        TimeOnly localTime,
        int partySize = 2) =>
        new(
            branch.BranchId,
            tableId,
            BookingDate,
            localTime,
            partySize,
            GuestName: "Ani Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7());

    /// <summary>
    /// Counts the statements actually sent. The only way to catch an N+1 - the results look
    /// identical either way.
    /// </summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
