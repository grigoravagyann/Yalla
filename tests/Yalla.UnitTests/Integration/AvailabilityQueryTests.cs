using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
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

            // Nothing booked after them at all, so nothing closes the window. Null is the answer
            // a diner would rather have.
            Assert.Null(t.AvailableUntilUtc);
            Assert.False(t.LimitedByNextBooking);
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
        // 19:45 - 20:00 less the branch's 15 minutes of clearing time.
        var later = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(20, 0)));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        Assert.NotNull(availability);

        var limited = availability.Tables.Single(t => t.TableId == branch.TableIds[0]);

        Assert.True(limited.IsAvailable);
        Assert.True(limited.LimitedByNextBooking);
        Assert.Equal(later.Id, limited.NextReservationId);
        Assert.Equal(later.StartUtc, limited.NextReservationStartUtc);

        Assert.Equal(availability.RequestedStartUtc, limited.AvailableFromUtc);
        Assert.Equal(later.StartUtc.AddMinutes(-availability.BufferMinutes), limited.AvailableUntilUtc);

        // 18:00 to 19:45, as the diner reads it off the screen before confirming.
        Assert.Equal(SixPm, limited.AvailableFromLocal);
        Assert.Equal(new TimeOnly(19, 45), limited.AvailableUntilLocal);
        Assert.Equal(105, limited.AvailableMinutes);

        // The other table has no limit, which is exactly the comparison the screen exists to let
        // a diner make.
        var unlimited = availability.Tables.Single(t => t.TableId == branch.TableIds[1]);

        Assert.False(unlimited.LimitedByNextBooking);
        Assert.Null(unlimited.AvailableUntilUtc);
    }

    [SkippableFact]
    public async Task A_table_whose_next_booking_leaves_no_room_is_reported_as_already_booked()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);

        // 18:30 leaves nowhere near a 90-minute sitting starting at 18:00.
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(18, 30)));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));

        var taken = availability!.Tables.Single(t => t.TableId == branch.TableIds[0]);

        Assert.False(taken.IsAvailable);
        Assert.Equal(ReservationRejectionReason.TableAlreadyBooked, taken.UnavailableReason);

        // No window, because there is no offer to make - rather than a window too short to use.
        Assert.Null(taken.AvailableUntilUtc);
        Assert.Null(taken.AvailableFromUtc);
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

        // Booked at 18:00. Asked about 17:50, which is inside the branch's 15-minute turnaround.
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId, SixPm));

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(
                new AvailabilityRequest(branch.BranchId, 2, BookingDate, new TimeOnly(17, 50)));

        var table = availability!.Tables.Single();

        // The table is physically free right now and will be for days, but the question was about
        // 17:50 - and at 17:50 it is about to be somebody else's.
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

        // Noon at the branch on the requested date, and the booking made above is right there.
        Assert.Equal(noon, availability.LocalTime);
        Assert.Equal(
            ReservationRejectionReason.TableAlreadyBooked,
            availability.Tables.Single().UnavailableReason);
    }

    [SkippableFact]
    public async Task The_whole_answer_arrives_in_one_round_trip()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 8);
        var booker = fixture.CreateReservationService(db, clock, TestActor.Diner());

        // Bookings on half the tables, so an N+1 has something to be tempted by.
        foreach (var tableId in branch.TableIds.Take(4))
        {
            await booker.CreateAsync(Booking(branch, tableId, new TimeOnly(21, 0)));
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
