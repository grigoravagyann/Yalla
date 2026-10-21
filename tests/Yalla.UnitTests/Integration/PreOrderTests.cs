using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Pre-orders on a booking, as the guest sees them: what may be ordered, until when, at what price, and what becomes
/// of it when the booking moves or ends. See <c>docs/reservations.md</c>, "Pre-orders".
/// </summary>
[Collection(SqlServerCollection.Name)]
public partial class PreOrderTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The guest orders two dishes, reads them back on <c>/mine</c>, changes the order after the menu price went up -
    /// the dish already on it keeps its price, the new one is priced now - and clears it.
    /// </summary>
    [SkippableFact]
    public async Task A_guest_saves_reads_edits_at_the_locked_price_and_clears_a_pre_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        var saved = await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Khachapuri, quantity = 2, note = "no egg" } },
            note = "Salad first, please",
            clientCommandId = Guid.CreateVersion7(),
        });

        var first = await OkAsync(saved);
        var preOrder = first.GetProperty("preOrder");
        Assert.False(first.GetProperty("wasReplay").GetBoolean());
        Assert.Equal(1, preOrder.GetProperty("state").GetInt32());
        Assert.Equal(2 * TestMenu.KhachapuriAmd, preOrder.GetProperty("totalAmd").GetInt64());
        Assert.Equal("Salad first, please", preOrder.GetProperty("note").GetString());
        Assert.Equal(startUtc.AddHours(-2), preOrder.GetProperty("editableUntilUtc").GetDateTime());
        Assert.Equal(startUtc.AddMinutes(-30), preOrder.GetProperty("dueAtUtc").GetDateTime());

        var line = Assert.Single(preOrder.GetProperty("lines").EnumerateArray());
        Assert.Equal(x.Menu.Khachapuri, line.GetProperty("menuItemId").GetGuid());
        Assert.Equal("no egg", line.GetProperty("note").GetString());
        Assert.True(line.GetProperty("availableNow").GetBoolean());

        var mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(2 * TestMenu.KhachapuriAmd, mine.GetProperty("preOrder").GetProperty("totalAmd").GetInt64());
        Assert.Equal(startUtc.AddHours(-2), mine.GetProperty("preOrderEditableUntilUtc").GetDateTime());

        await SetPriceAsync(factory, x.Menu.Khachapuri, 5_000L);
        await SetPriceAsync(factory, x.Menu.Coffee, 1_500L);

        var edited = await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new object[]
            {
                new { menuItemId = x.Menu.Khachapuri, quantity = 3 },
                new { menuItemId = x.Menu.Coffee, quantity = 1 },
            },
            clientCommandId = Guid.CreateVersion7(),
        }));

        var lines = edited.GetProperty("preOrder").GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(TestMenu.KhachapuriAmd, lines[0].GetProperty("unitPriceAmd").GetInt64());
        Assert.Equal(1_500L, lines[1].GetProperty("unitPriceAmd").GetInt64());
        Assert.Equal((3 * TestMenu.KhachapuriAmd) + 1_500L, edited.GetProperty("preOrder").GetProperty("totalAmd").GetInt64());
        Assert.False(edited.GetProperty("preOrder").TryGetProperty("note", out _));

        var cleared = await OkAsync(await diner.PostAsJsonAsync(
            $"/api/reservations/{id}/pre-order/clear", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.False(cleared.TryGetProperty("preOrder", out _));

        mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.False(mine.TryGetProperty("preOrder", out _));
    }

    /// <summary>Editable until exactly two hours before the start; from that instant, locked - for clearing too.</summary>
    [SkippableFact]
    public async Task A_pre_order_is_editable_until_two_hours_before_the_start_and_locked_from_then()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        factory.Clock.UtcNow = startUtc.AddHours(-2).AddMinutes(-1);
        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        factory.Clock.UtcNow = startUtc.AddHours(-2);
        var late = await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 3 } },
            clientCommandId = Guid.CreateVersion7(),
        });

        var problem = await ProblemAsync(late, HttpStatusCode.Conflict, "pre-order-locked");
        Assert.Equal(startUtc.AddHours(-2), problem.GetProperty("context").GetProperty("lockedAtUtc").GetDateTime());

        await ProblemAsync(
            await diner.PostAsJsonAsync($"/api/reservations/{id}/pre-order/clear", new { clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "pre-order-locked");

        var mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(2, mine.GetProperty("preOrder").GetProperty("state").GetInt32());
        Assert.Equal(2, mine.GetProperty("preOrder").GetProperty("lines")[0].GetProperty("quantity").GetInt32());
    }

    /// <summary>Somebody else's booking is not found, word for word as an id that is no booking at all.</summary>
    [SkippableFact]
    public async Task Another_guests_booking_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var owner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var stranger = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var id = (await CreatedAsync(owner, AppBooking(factory, x.Branch, daysAhead: 2))).GetProperty("id").GetGuid();

        var response = await stranger.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { })).StatusCode);
    }

    /// <summary>
    /// A branch that does not take pre-orders refuses one; a branch that stops taking them keeps the ones it has, and
    /// the guest can still clear theirs.
    /// </summary>
    [SkippableFact]
    public async Task A_branch_that_does_not_take_pre_orders_refuses_new_ones_but_keeps_and_lets_guests_clear_old_ones()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var kept = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2))).GetProperty("id").GetGuid();
        var refused = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 3))).GetProperty("id").GetGuid();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{kept}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        using var manager = factory.CreateClientWithToken(x.ManagerToken);
        await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{x.Branch.BranchId}/pre-order-settings", new
        {
            acceptsPreOrders = false,
            minutesBefore = 30,
            excludedMenuItemIds = Array.Empty<Guid>(),
        }));

        var problem = await ProblemAsync(
            await diner.PutAsJsonAsync($"/api/reservations/{refused}/pre-order", new
            {
                lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            }),
            HttpStatusCode.Conflict,
            "pre-orders-not-accepted");
        Assert.Equal(x.Branch.BranchId, problem.GetProperty("context").GetProperty("branchId").GetGuid());

        var mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray().ToList();
        Assert.True(mine.Single(r => r.GetProperty("id").GetGuid() == kept).TryGetProperty("preOrder", out _));
        Assert.False(mine.Single(r => r.GetProperty("id").GetGuid() == refused).TryGetProperty("preOrderEditableUntilUtc", out _));

        await OkAsync(await diner.PostAsJsonAsync(
            $"/api/reservations/{kept}/pre-order/clear", new { clientCommandId = Guid.CreateVersion7() }));
    }

    /// <summary>
    /// Excluded, sold out, another branch's and unknown dishes are each named with their reason, and nothing is saved.
    /// </summary>
    [SkippableFact]
    public async Task Dishes_that_cannot_be_pre_ordered_are_refused_each_with_its_reason()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, excluded: true);
        var other = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var id = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2))).GetProperty("id").GetGuid();
        var unknown = Guid.CreateVersion7();

        var response = await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[]
            {
                new { menuItemId = x.Menu.Coffee, quantity = 1 },
                new { menuItemId = x.Menu.Wine, quantity = 1 },
                new { menuItemId = x.Menu.SoldOut, quantity = 1 },
                new { menuItemId = other.Menu.Coffee, quantity = 1 },
                new { menuItemId = unknown, quantity = 1 },
            },
            clientCommandId = Guid.CreateVersion7(),
        });

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "pre-order-items-unavailable");
        var reasons = problem.GetProperty("context").GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("menuItemId").GetGuid(), i => i.GetProperty("reason").GetInt32());

        Assert.Equal(4, reasons.Count);
        Assert.Equal(3, reasons[x.Menu.Wine]);
        Assert.Equal(2, reasons[x.Menu.SoldOut]);
        Assert.Equal(1, reasons[other.Menu.Coffee]);
        Assert.Equal(1, reasons[unknown]);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.PreOrders WHERE ReservationId = {id}").SingleAsync());
    }

    /// <summary>
    /// A dish sold out after it was pre-ordered stays on the pre-order, flagged - and a save that keeps it is refused,
    /// so the guest drops or swaps it rather than the server doing it silently.
    /// </summary>
    [SkippableFact]
    public async Task A_dish_that_sells_out_afterwards_stays_flagged_and_must_be_removed_to_save_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var id = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2))).GetProperty("id").GetGuid();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Wine, quantity = 1 }, new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await db.Database.ExecuteSqlAsync($"UPDATE dbo.MenuItems SET IsAvailable = 0 WHERE Id = {x.Menu.Wine}");
        }

        var line = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("preOrder").GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("menuItemId").GetGuid() == x.Menu.Wine);
        Assert.False(line.GetProperty("availableNow").GetBoolean());

        await ProblemAsync(
            await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
            {
                lines = new[] { new { menuItemId = x.Menu.Wine, quantity = 2 } },
                clientCommandId = Guid.CreateVersion7(),
            }),
            HttpStatusCode.UnprocessableEntity,
            "pre-order-items-unavailable");

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
    }

    /// <summary>Quantities, line counts and notes are bounded; every broken bound is named by its wire field.</summary>
    [SkippableFact]
    public async Task Quantities_lines_and_notes_are_bounded_and_every_bound_is_named()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var id = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2))).GetProperty("id").GetGuid();

        var response = await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new object[]
            {
                new { menuItemId = x.Menu.Coffee, quantity = 0 },
                new { menuItemId = x.Menu.Khachapuri, quantity = 21, note = new string('n', 201) },
            },
            note = new string('x', 501),
            clientCommandId = Guid.CreateVersion7(),
        });

        var fields = (await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation-failed"))
            .GetProperty("context").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()).ToList();

        Assert.Contains("lines[0].quantity", fields);
        Assert.Contains("lines[1].quantity", fields);
        Assert.Contains("lines[1].note", fields);
        Assert.Contains("note", fields);

        var tooMany = await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = Enumerable.Range(0, 31).Select(_ => new { menuItemId = x.Menu.Coffee, quantity = 1 }).ToArray(),
            clientCommandId = Guid.CreateVersion7(),
        });

        Assert.Contains(
            "lines",
            (await ProblemAsync(tooMany, HttpStatusCode.UnprocessableEntity, "validation-failed"))
                .GetProperty("context").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()));

        var empty = await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = Array.Empty<object>(),
            clientCommandId = Guid.CreateVersion7(),
        });

        await ProblemAsync(empty, HttpStatusCode.UnprocessableEntity, "validation-failed");
    }

    /// <summary>
    /// The same command twice is one save, answered as a replay; the same id on another command, or by another guest,
    /// is a client bug and says so.
    /// </summary>
    [SkippableFact]
    public async Task A_retried_save_is_a_replay_and_a_reused_command_id_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var stranger = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var id = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2))).GetProperty("id").GetGuid();
        var theirs = (await CreatedAsync(stranger, AppBooking(factory, x.Branch, daysAhead: 2, tableIndex: 1))).GetProperty("id").GetGuid();
        var command = Guid.CreateVersion7();

        var body = new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = command,
        };

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", body));
        var again = await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", body));

        Assert.True(again.GetProperty("wasReplay").GetBoolean());
        Assert.Equal(1, again.GetProperty("preOrder").GetProperty("lines")[0].GetProperty("quantity").GetInt32());

        await ProblemAsync(
            await diner.PostAsJsonAsync($"/api/reservations/{id}/pre-order/clear", new { clientCommandId = command }),
            HttpStatusCode.Conflict,
            "client-command-id-in-use");

        await ProblemAsync(
            await stranger.PutAsJsonAsync($"/api/reservations/{theirs}/pre-order", body),
            HttpStatusCode.Conflict,
            "client-command-id-in-use");

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.PreOrderLines l JOIN dbo.PreOrders p ON p.Id = l.PreOrderId WHERE p.ReservationId = {id}").SingleAsync());
    }

    /// <summary>The manage link does what the app does: save, read, clear, and answer "on my way".</summary>
    [SkippableFact]
    public async Task The_manage_link_saves_reads_and_clears_a_pre_order_and_answers_on_my_way()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var token = booking.GetProperty("manageToken").GetString()!;
        var startUtc = booking.GetProperty("startUtc").GetDateTime();
        using var web = factory.CreateClient();

        var saved = await OkAsync(await web.PutAsJsonAsync($"/api/public/bookings/{token}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Khachapuri, quantity = 1 } },
            note = "by the window",
            clientCommandId = Guid.CreateVersion7(),
        }));
        Assert.Equal(TestMenu.KhachapuriAmd, saved.GetProperty("preOrder").GetProperty("totalAmd").GetInt64());

        var link = await ReadAsync(web, $"/api/public/bookings/{token}");
        Assert.Equal("by the window", link.GetProperty("preOrder").GetProperty("note").GetString());
        Assert.Equal(startUtc.AddHours(-2), link.GetProperty("preOrderEditableUntilUtc").GetDateTime());
        Assert.False(link.GetProperty("onYourWayAsked").GetBoolean());

        factory.Clock.UtcNow = startUtc.AddMinutes(-45);
        Assert.True((await ReadAsync(web, $"/api/public/bookings/{token}")).GetProperty("onYourWayAsked").GetBoolean());

        var answered = await OkAsync(await web.PostAsJsonAsync($"/api/public/bookings/{token}/on-my-way", new { }));
        Assert.Equal(startUtc.AddMinutes(-45), answered.GetProperty("guestConfirmedOnWayAtUtc").GetDateTime());

        link = await ReadAsync(web, $"/api/public/bookings/{token}");
        Assert.False(link.GetProperty("onYourWayAsked").GetBoolean());
        Assert.Equal(startUtc.AddMinutes(-45), link.GetProperty("guestConfirmedOnWayAtUtc").GetDateTime());

        factory.Clock.UtcNow = startUtc.AddHours(-3);
        await OkAsync(await web.PostAsJsonAsync(
            $"/api/public/bookings/{token}/pre-order/clear", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.False((await ReadAsync(web, $"/api/public/bookings/{token}")).TryGetProperty("preOrder", out _));
    }

    /// <summary>
    /// A booking without a pre-order is asked too (2026-10-21): its manage link asks from 30 minutes before, takes the
    /// answer, and the floor shows the waiter that the guest is coming, so nobody calls.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_without_a_pre_order_is_asked_at_30_minutes_and_the_floor_sees_the_answer()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var token = booking.GetProperty("manageToken").GetString()!;
        var startUtc = booking.GetProperty("startUtc").GetDateTime();
        using var web = factory.CreateClient();

        // Signed in before the clock moves: a token minted two days ahead is not valid yet.
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        factory.Clock.UtcNow = startUtc.AddMinutes(-31);
        Assert.False((await ReadAsync(web, $"/api/public/bookings/{token}")).GetProperty("onYourWayAsked").GetBoolean());

        factory.Clock.UtcNow = startUtc.AddMinutes(-30);
        Assert.True((await ReadAsync(web, $"/api/public/bookings/{token}")).GetProperty("onYourWayAsked").GetBoolean());

        var answered = await OkAsync(await web.PostAsJsonAsync($"/api/public/bookings/{token}/on-my-way", new { }));
        Assert.Equal(startUtc.AddMinutes(-30), answered.GetProperty("guestConfirmedOnWayAtUtc").GetDateTime());
        Assert.False((await ReadAsync(web, $"/api/public/bookings/{token}")).GetProperty("onYourWayAsked").GetBoolean());

        // The waiter's floor: the table's next booking says the guest is on their way.
        var floor = await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/floor");
        var table = floor.GetProperty("tables").EnumerateArray()
            .Single(t => t.TryGetProperty("nextReservationId", out var next)
                         && next.GetGuid() == booking.GetProperty("id").GetGuid());
        Assert.Equal(startUtc.AddMinutes(-30), table.GetProperty("nextReservationGuestConfirmedOnWayAtUtc").GetDateTime());
    }

    /// <summary>
    /// A move carries the pre-order, judged against the new time: to another day it is editable again; into the last
    /// two hours it is kept, locked - never dropped.
    /// </summary>
    [SkippableFact]
    public async Task A_move_carries_the_pre_order_and_the_lock_follows_the_new_time()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        var moved = await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/reschedule", new
        {
            date = LocalDate(factory, 3),
            time = "19:00",
            partySize = 2,
            diningTableId = x.Branch.TableIds[0],
            clientCommandId = Guid.CreateVersion7(),
        }));

        var newStart = moved.GetProperty("startUtc").GetDateTime();
        Assert.Equal(1, moved.GetProperty("preOrder").GetProperty("state").GetInt32());
        Assert.Equal(newStart.AddHours(-2), moved.GetProperty("preOrder").GetProperty("editableUntilUtc").GetDateTime());
        Assert.Equal(2, moved.GetProperty("preOrder").GetProperty("lines")[0].GetProperty("quantity").GetInt32());

        // Now, three hours before the moved start, move it to one hour from now: kept, locked.
        factory.Clock.UtcNow = newStart.AddHours(-3);
        var soon = newStart.AddHours(-2);
        var soonLocal = TimeZoneInfo.ConvertTimeFromUtc(soon, TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan"));

        var intoTheLock = await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/reschedule", new
        {
            date = DateOnly.FromDateTime(soonLocal).ToString("yyyy-MM-dd"),
            time = TimeOnly.FromDateTime(soonLocal).ToString("HH:mm"),
            partySize = 2,
            diningTableId = x.Branch.TableIds[0],
            clientCommandId = Guid.CreateVersion7(),
        }));

        Assert.Equal(2, intoTheLock.GetProperty("preOrder").GetProperty("state").GetInt32());
        Assert.Equal(2, intoTheLock.GetProperty("preOrder").GetProperty("lines")[0].GetProperty("quantity").GetInt32());
    }

    /// <summary>A cancelled booking's pre-order reads cancelled, and its "on your way" question is withdrawn.</summary>
    [SkippableFact]
    public async Task Cancelling_the_booking_cancels_the_pre_order_and_its_question()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var id = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2))).GetProperty("id").GetGuid();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
        Assert.Equal(1, await QuestionsQueuedAsync(factory, id));

        Assert.Equal(HttpStatusCode.OK, (await diner.PostAsJsonAsync($"/api/reservations/{id}/cancel", new { reason = "plans changed" })).StatusCode);

        var past = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("past").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(5, past.GetProperty("preOrder").GetProperty("state").GetInt32());
        Assert.Equal(0, await QuestionsQueuedAsync(factory, id));

        await ProblemAsync(
            await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }),
            HttpStatusCode.Conflict,
            "on-my-way-not-open");
    }

    /// <summary>
    /// Every booking is asked "on your way?" 30 minutes before - push and feed entry. Saving a pre-order moves the
    /// question to 45 minutes, clearing it moves it back to 30, and a move asks at the new time (2026-10-21: every
    /// booking, so the waiter need not call).
    /// </summary>
    [SkippableFact]
    public async Task The_on_your_way_question_is_asked_of_every_booking_and_moves_with_its_pre_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();
        var bare = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 4));
        var bareId = bare.GetProperty("id").GetGuid();

        // Asked at 30 minutes from the moment it is booked, like every booking.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var push = await db.OutboxMessages.AsNoTracking()
                .SingleAsync(m => m.IdempotencyKey.StartsWith($"reservation:{id}:on-your-way"));
            Assert.Equal(startUtc.AddMinutes(-30), push.ScheduledForUtc);
        }

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var push = await db.OutboxMessages.AsNoTracking()
                .SingleAsync(m => m.IdempotencyKey.StartsWith($"reservation:{id}:on-your-way"));
            Assert.Equal("reservation.on-your-way", push.Type);
            Assert.Equal(startUtc.AddMinutes(-45), push.ScheduledForUtc);

            var entry = await db.DinerNotifications.AsNoTracking()
                .SingleAsync(n => n.ReservationId == id && n.Kind == "booking-on-your-way");
            Assert.Equal(startUtc.AddMinutes(-45), entry.CreatedAtUtc);

            // The booking without a pre-order keeps its 30 minutes.
            var barePush = await db.OutboxMessages.AsNoTracking()
                .SingleAsync(m => m.IdempotencyKey.StartsWith($"reservation:{bareId}:on-your-way"));
            Assert.Equal(bare.GetProperty("startUtc").GetDateTime().AddMinutes(-30), barePush.ScheduledForUtc);
        }

        // A second save does not ask twice.
        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
        Assert.Equal(1, await QuestionsQueuedAsync(factory, id));

        var moved = await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/reschedule", new
        {
            date = LocalDate(factory, 3),
            time = "20:00",
            partySize = 2,
            diningTableId = x.Branch.TableIds[0],
            clientCommandId = Guid.CreateVersion7(),
        }));
        var newStart = moved.GetProperty("startUtc").GetDateTime();

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var push = await db.OutboxMessages.AsNoTracking()
                .SingleAsync(m => m.IdempotencyKey.StartsWith($"reservation:{id}:on-your-way") && m.SentAtUtc == null);
            Assert.Equal(newStart.AddMinutes(-45), push.ScheduledForUtc);
            Assert.Equal(
                newStart.AddMinutes(-45),
                (await db.DinerNotifications.AsNoTracking().SingleAsync(n => n.ReservationId == id && n.Kind == "booking-on-your-way")).CreatedAtUtc);
        }

        await OkAsync(await diner.PostAsJsonAsync(
            $"/api/reservations/{id}/pre-order/clear", new { clientCommandId = Guid.CreateVersion7() }));

        // Without the pre-order it is asked like every booking: back to 30 minutes, push and feed entry.
        Assert.Equal(1, await QuestionsQueuedAsync(factory, id));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var push = await db.OutboxMessages.AsNoTracking()
                .SingleAsync(m => m.IdempotencyKey.StartsWith($"reservation:{id}:on-your-way") && m.SentAtUtc == null);
            Assert.Equal(newStart.AddMinutes(-30), push.ScheduledForUtc);
            Assert.Equal(
                newStart.AddMinutes(-30),
                (await db.DinerNotifications.AsNoTracking().SingleAsync(n => n.ReservationId == id && n.Kind == "booking-on-your-way")).CreatedAtUtc);
        }
    }

    /// <summary>"Yes, on my way" is recorded once, from the app; a second tap answers with the first.</summary>
    [SkippableFact]
    public async Task On_my_way_is_recorded_once_and_shown_on_the_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        factory.Clock.UtcNow = startUtc.AddMinutes(-40);
        var first = await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }));
        Assert.False(first.GetProperty("wasReplay").GetBoolean());

        factory.Clock.UtcNow = startUtc.AddMinutes(-30);
        var second = await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }));
        Assert.True(second.GetProperty("wasReplay").GetBoolean());
        Assert.Equal(startUtc.AddMinutes(-40), second.GetProperty("guestConfirmedOnWayAtUtc").GetDateTime());

        var mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(startUtc.AddMinutes(-40), mine.GetProperty("guestConfirmedOnWayAtUtc").GetDateTime());
    }

    // ---------------------------------------------------------------- helpers

    internal sealed record Arrangement(AuthBranch Branch, TestMenu Menu, string ManagerToken);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>A branch taking app bookings, with the test menu, and pre-orders switched on by its manager.</summary>
    private async Task<Arrangement> ArrangeAsync(
        YallaApiFactory factory,
        bool accepts = true,
        int minutesBefore = 30,
        bool excluded = false)
    {
        AuthBranch branch;
        TestMenu menu;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: true);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        }

        var manager = await SignInManagerAsync(factory, branch);

        if (accepts)
        {
            using var client = factory.CreateClientWithToken(manager);

            await OkAsync(await client.PutAsJsonAsync($"/api/branches/{branch.BranchId}/pre-order-settings", new
            {
                acceptsPreOrders = true,
                minutesBefore,
                excludedMenuItemIds = excluded ? new[] { menu.Wine } : Array.Empty<Guid>(),
            }));
        }

        return new Arrangement(branch, menu, manager);
    }

    private async Task SetPriceAsync(YallaApiFactory factory, Guid menuItemId, long priceAmd)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        await db.Database.ExecuteSqlAsync($"UPDATE dbo.MenuItems SET PriceAmd = {priceAmd} WHERE Id = {menuItemId}");
    }

    /// <summary>The booking's unsent "on your way" pushes.</summary>
    private async Task<int> QuestionsQueuedAsync(YallaApiFactory factory, Guid reservationId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.OutboxMessages.CountAsync(
            m => m.IdempotencyKey.StartsWith($"reservation:{reservationId}:on-your-way") && m.SentAtUtc == null);
    }

    internal static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    internal static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");

        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("code").GetString());

        return problem;
    }
}
