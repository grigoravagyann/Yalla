using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;
using static Yalla.UnitTests.Integration.PreOrderTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Staff-taken bookings (2026-10-31, "Ձեռքով ամրագրում"): a waiter books for a guest by hand from the floor, the booking
/// is theirs to follow - the to-call list, the guest's "coming" recorded over the phone - and none of a diner's rules
/// stand in the way. See <c>docs/reservations.md</c>, "Staff-taken bookings".
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class StaffBookingTests(SqlServerFixture fixture)
{
    private static readonly TimeZoneInfo Yerevan = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");

    /// <summary>
    /// At a branch that takes no online bookings at all - the platform's switches off, the venue's gate off - the waiter
    /// books a table for tonight: confirmed at once, no reminders, no manage link, the taker on the booking and in the
    /// History; the console sees who took it; half an hour before the start the booking is on the to-call list, the
    /// taker's first; "coming" recorded by the waiter is theirs, and the list lets the booking go.
    /// </summary>
    [SkippableFact]
    public async Task A_waiter_books_for_a_guest_by_hand_and_follows_whether_they_come()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var today = Today(factory);
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2, bookable: false);

            // Nobody books online here: the platform blocks both channels (and so the pre-orders that ride on them).
            var row = await db.Branches.FirstAsync(b => b.Id == branch.BranchId);
            row.SetAllowedFeatures(
                [.. BranchFeatures.Full.Where(f => f is not (BranchFeature.AppBookings or BranchFeature.WebBookings or BranchFeature.PreOrders))],
                incompleteMenuItemCount: 0);
            await db.SaveChangesAsync();
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        var b = $"/api/branches/{branch.BranchId}";

        // Tonight, two hours ahead - short notice for a diner, and a booking for the floor.
        var body = Body(branch.TableIds[0], today, "17:00", 3);
        var made = await CreatedAsync(await waiter.PostAsJsonAsync($"{b}/reservations/staff", body));
        var id = made.GetProperty("id").GetGuid();

        Assert.Equal((int)ReservationStatus.Confirmed, made.GetProperty("status").GetInt32());
        Assert.Equal((int)ReservationChannel.Staff, made.GetProperty("channel").GetInt32());
        Assert.Equal(branch.WaiterId, made.GetProperty("takenByStaffId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(made.GetProperty("takenByName").GetString()));
        Assert.Equal(branch.TableIds[0], made.GetProperty("tableId").GetGuid());
        Assert.Equal("+37491000111", made.GetProperty("guestPhone").GetString());
        Assert.Equal("Window, please", made.GetProperty("note").GetString());
        Assert.False(made.GetProperty("bookedWithoutTable").GetBoolean());
        Assert.False(made.GetProperty("wasReplay").GetBoolean());
        Assert.False(made.TryGetProperty("awaitingApprovalBecause", out var why) && why.ValueKind != JsonValueKind.Null);
        Assert.False(made.TryGetProperty("manageToken", out var link) && link.ValueKind != JsonValueKind.Null);

        // The retry answers the booking it made, 200 rather than 201.
        var replayed = await OkAsync(await waiter.PostAsJsonAsync($"{b}/reservations/staff", body));
        Assert.Equal(id, replayed.GetProperty("id").GetGuid());
        Assert.True(replayed.GetProperty("wasReplay").GetBoolean());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id);
            Assert.Equal(branch.WaiterId, row.TakenByStaffId);
            Assert.Null(row.DinerUserId);
            Assert.Null(row.BookerPhoneE164);
            Assert.False(row.PhoneUnverified);
            Assert.Null(row.ManageTokenHash);

            // No reminder, no nudge, no "are you on your way?": there is no app to push to.
            var prefix = OutboxMessageTypes.PrefixFor("reservation", id);
            Assert.Empty(await db.OutboxMessages.AsNoTracking().Where(m => m.IdempotencyKey.StartsWith(prefix)).ToListAsync());

            // The History's line is the waiter's, on their tablet - not a guest's.
            var line = await db.ActivityEntries.AsNoTracking().SingleAsync(e => e.SubjectId == id && e.Action == ActivityActions.BookingCreated);
            Assert.Equal(ActivityActorKind.Staff, line.ActorKind);
            Assert.Equal(branch.WaiterId, line.StaffMemberId);
            Assert.NotNull(line.DeviceId);
            Assert.Contains("\"takenByStaffId\"", line.SummaryJson, StringComparison.Ordinal);
        }

        // The console's Overview says who took it.
        var overview = await ReadAsync(manager, $"{b}/overview");
        var listed = overview.GetProperty("todayBookings").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal((int)ReservationChannel.Staff, listed.GetProperty("channel").GetInt32());
        Assert.Equal(made.GetProperty("takenByName").GetString(), listed.GetProperty("takenByName").GetString());

        // Two hours ahead, nobody is called yet.
        Assert.Empty((await ReadAsync(waiter, $"{b}/reservations/to-call")).EnumerateArray());

        // Twenty-five minutes before: the taker's card, with the number to call; a colleague sees it as not theirs.
        factory.Clock.UtcNow = At(today, "16:35");

        var mine = Assert.Single((await ReadAsync(waiter, $"{b}/reservations/to-call")).EnumerateArray());
        Assert.Equal(id, mine.GetProperty("reservationId").GetGuid());
        Assert.True(mine.GetProperty("isMine").GetBoolean());
        Assert.Equal(branch.WaiterId, mine.GetProperty("takenByStaffId").GetGuid());
        Assert.Equal("+37491000111", mine.GetProperty("guestPhone").GetString());
        Assert.Equal("17:00:00", mine.GetProperty("localStartTime").GetString());
        Assert.Equal(branch.TableIds[0], mine.GetProperty("tableId").GetGuid());

        var theirs = Assert.Single((await ReadAsync(manager, $"{b}/reservations/to-call")).EnumerateArray());
        Assert.False(theirs.GetProperty("isMine").GetBoolean());

        // The guest said they are coming: recorded as the waiter's; a retry and a second answer are the booking as it stands.
        var coming = new { clientCommandId = Guid.CreateVersion7() };
        var said = await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/guest-coming", coming));
        Assert.Equal(At(today, "16:35"), said.GetProperty("guestConfirmedOnWayAtUtc").GetDateTime());
        Assert.False(said.GetProperty("wasReplay").GetBoolean());

        Assert.True((await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/guest-coming", coming))).GetProperty("wasReplay").GetBoolean());
        Assert.True((await OkAsync(await manager.PostAsJsonAsync(
            $"/api/reservations/{id}/guest-coming", new { clientCommandId = Guid.CreateVersion7() }))).GetProperty("wasReplay").GetBoolean());

        Assert.Empty((await ReadAsync(waiter, $"{b}/reservations/to-call")).EnumerateArray());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id);
            Assert.Equal(branch.WaiterId, row.OnWayRecordedByStaffId);

            var line = await db.ActivityEntries.AsNoTracking().SingleAsync(e => e.SubjectId == id && e.Action == ActivityActions.BookingGuestComing);
            Assert.Equal(ActivityActorKind.Staff, line.ActorKind);
            Assert.Equal(branch.WaiterId, line.StaffMemberId);
            Assert.Empty(await db.ActivityEntries.AsNoTracking()
                .Where(e => e.SubjectId == id && e.Action == ActivityActions.BookingGuestAnswered).ToListAsync());
        }

        // They came: seated on the booking, as any booking is.
        factory.Clock.UtcNow = At(today, "17:05");
        await OkAsync(await waiter.PostAsJsonAsync(
            $"{b}/tables/{branch.TableIds[0]}/seat-reservation", new { reservationId = id, clientCommandId = Guid.CreateVersion7() }));
    }

    /// <summary>
    /// What holds for the floor and what does not: a start already past, the window and the opening hours refuse it;
    /// the table's seats refuse it; the table's evening refuses a clash. A table the app does not offer is the floor's to
    /// book, and the staff sheet's day says so where the public read does not.
    /// </summary>
    [SkippableFact]
    public async Task The_slots_rules_hold_and_the_diners_rules_do_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var today = Today(factory);
        AuthBranch branch;
        int windowDays;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);

            // The second table is not offered to the app at all.
            (await db.DiningTables.FirstAsync(t => t.Id == branch.TableIds[1])).SetBookable(false);
            windowDays = (await db.Branches.AsNoTracking().Where(x => x.Id == branch.BranchId).Select(x => x.ReservationPolicy).FirstAsync()).BookingWindowDays;
            await db.SaveChangesAsync();
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var anyone = factory.CreateClient();
        var b = $"/api/branches/{branch.BranchId}";

        // The slot's own rules.
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(branch.TableIds[0], today, "14:00", 2)),
            HttpStatusCode.UnprocessableEntity,
            "reservation-lead-time-too-short");
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(branch.TableIds[0], today.AddDays(windowDays + 1), "19:00", 2)),
            HttpStatusCode.UnprocessableEntity,
            "reservation-outside-booking-window");
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(branch.TableIds[0], today, "22:45", 2)),
            HttpStatusCode.UnprocessableEntity,
            "reservation-outside-opening-hours");
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(branch.TableIds[0], today, "19:00", 9)),
            HttpStatusCode.UnprocessableEntity,
            "reservation-party-exceeds-capacity");
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(null, today, "19:00", 2)),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");

        // The staff sheet offers both tables at 19:00; the public picker counts only the one the app may book.
        var sheet = await ReadAsync(waiter, $"{b}/reservations/slots?date={today:yyyy-MM-dd}&party=2");
        Assert.Equal((int)BookingMode.ChooseTable, sheet.GetProperty("bookingMode").GetInt32());
        var slots = Slots(sheet);
        Assert.Equal(
            branch.TableIds.Order(),
            slots["19:00"].GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("tableId").GetGuid()).Order());
        Assert.False(slots["14:00"].GetProperty("isAvailable").GetBoolean());
        Assert.Equal((int)ReservationRejectionReason.LeadTimeTooShort, slots["14:00"].GetProperty("unavailableReason").GetInt32());
        Assert.False(slots.ContainsKey("09:00"));

        var picker = await anyone.GetFromJsonAsync<JsonElement>($"{b}/availability/slots?date={today:yyyy-MM-dd}&partySize=2");
        Assert.Equal(1, Slots(picker)["19:00"].GetProperty("freeTableCount").GetInt32());

        // The floor books the table the app does not offer.
        var made = await CreatedAsync(await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(branch.TableIds[1], today, "19:00", 2)));
        Assert.Equal(branch.TableIds[1], made.GetProperty("tableId").GetGuid());

        // The table's evening refuses a second party on it half an hour later - and the sheet already said so.
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(branch.TableIds[1], today, "19:30", 2)),
            HttpStatusCode.Conflict,
            "table-already-booked");

        slots = Slots(await ReadAsync(waiter, $"{b}/reservations/slots?date={today:yyyy-MM-dd}&party=2"));
        Assert.Equal([branch.TableIds[0]], slots["19:00"].GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("tableId").GetGuid()));
        Assert.Equal([branch.TableIds[0]], slots["19:30"].GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("tableId").GetGuid()));
    }

    /// <summary>
    /// At a branch that books by time only the floor books a time and how many, with no table; the room's guests at once
    /// hold; the sheet answers in guests; the booking is on the floor's list of the unseated with who took it.
    /// </summary>
    [SkippableFact]
    public async Task By_time_only_the_floor_books_a_time_and_how_many()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var today = Today(factory);
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
            var row = await db.Branches.FirstAsync(x => x.Id == branch.BranchId);
            row.UpdateReservationPolicy(TimeOnlyPolicy(guestsAtOnce: 6), factory.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        var b = $"/api/branches/{branch.BranchId}";

        var made = await CreatedAsync(await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(null, today, "19:00", 4)));
        Assert.True(made.GetProperty("bookedWithoutTable").GetBoolean());
        Assert.False(made.TryGetProperty("tableId", out var tableId) && tableId.ValueKind != JsonValueKind.Null);
        Assert.Equal(branch.WaiterId, made.GetProperty("takenByStaffId").GetGuid());

        // The sheet answers in guests: two places left from 19:30, none of them tables.
        var sheet = await ReadAsync(waiter, $"{b}/reservations/slots?date={today:yyyy-MM-dd}&party=4");
        Assert.Equal((int)BookingMode.TimeOnly, sheet.GetProperty("bookingMode").GetInt32());
        var half = Slots(sheet)["19:30"];
        Assert.Equal(2, half.GetProperty("guestsLeft").GetInt32());
        Assert.False(half.GetProperty("isAvailable").GetBoolean());
        Assert.Equal((int)ReservationRejectionReason.TimeFull, half.GetProperty("unavailableReason").GetInt32());
        Assert.Empty(half.GetProperty("tables").EnumerateArray());

        var full = await ProblemAsync(
            await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(null, today, "19:30", 4)), HttpStatusCode.Conflict, "time-full");
        Assert.Equal(2, full.GetProperty("context").GetProperty("guestsLeft").GetInt32());

        await ProblemAsync(
            await waiter.PostAsJsonAsync($"{b}/reservations/staff", Body(branch.TableIds[0], today, "21:00", 2)),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");

        var unseated = Assert.Single((await ReadAsync(waiter, $"{b}/reservations/without-table")).EnumerateArray());
        Assert.Equal(made.GetProperty("id").GetGuid(), unseated.GetProperty("id").GetGuid());
        Assert.Equal(made.GetProperty("takenByName").GetString(), unseated.GetProperty("takenByName").GetString());
    }

    // ------------------------------------------------------------------ helpers

    private static object Body(Guid? tableId, DateOnly date, string time, int partySize) => new
    {
        tableId,
        date = date.ToString("yyyy-MM-dd"),
        time,
        partySize,
        guestName = "Արամ Հ.",
        guestPhone = "+37491000111",
        clientCommandId = Guid.CreateVersion7(),
        note = "Window, please",
    };

    private static Dictionary<string, JsonElement> Slots(JsonElement day) =>
        day.GetProperty("slots").EnumerateArray().ToDictionary(s => s.GetProperty("time").GetString()!);

    /// <summary>A staff booking made: 201 with the booking.</summary>
    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static DateOnly Today(YallaApiFactory factory) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(factory.Clock.UtcNow, Yerevan));

    private static DateTime At(DateOnly date, string time) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.ParseExact(time, "HH:mm", null)), Yerevan);

    private static ReservationPolicy TimeOnlyPolicy(int guestsAtOnce)
    {
        var d = ReservationPolicy.DefaultFor(VenueType.Restaurant);

        return new ReservationPolicy(
            d.TurnTimeMinutes,
            d.BufferMinutes,
            d.GraceMinutes,
            d.LateNudgeAfterMinutes,
            d.GraceExtensionMinutes,
            d.MinLeadMinutes,
            d.BookingWindowDays,
            d.CancellationDeadlineMinutes,
            autoConfirm: true,
            d.ServiceChargePercent,
            d.PricesIncludeVat,
            d.MaxSeatOverhang,
            d.ApprovalRequiredAbovePartySize,
            d.WalkInHoldbackMinutes,
            bookingMode: BookingMode.TimeOnly,
            guestsAtOnce: guestsAtOnce);
    }

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString).StartingAtYerevan(new TimeOnly(15, 0));
}
