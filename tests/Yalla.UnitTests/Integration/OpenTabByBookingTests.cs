using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>POST /api/tabs/open-by-booking</c>: the diner who booked a table opens the tab on it - or
/// lands on the one already there - with their booking code instead of the table's QR token.
/// </summary>
/// <remarks>
/// <para>
/// The bug this exists for: a diner booked table 5, typed the booking code off their confirmation
/// into the Scan screen's "type the code" field, and was told it belonged to no table. That field
/// takes the 32-character token printed under the QR, and nothing anywhere led from a booking to
/// its table's tab.
/// </para>
/// <para>
/// <b>Time is moved on the bookings, never on the API's clock.</b> A token is stamped with the API's
/// clock and checked against the real one, so a clock moved forward to dinner time mints tokens that
/// are not valid yet. The clock stays at now, rounded down to the second so every instant a test
/// computes survives the database and the JSON exactly, and each booking is moved to where the test
/// needs it relative to that.
/// </para>
/// <para>
/// The branch's walk-in holdback is set to <see cref="HoldbackMinutes"/>, away from the shipped
/// thirty, so a window that ignored the branch and used a number of its own could not pass.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class OpenTabByBookingTests(SqlServerFixture fixture)
{
    private const string Route = "/api/tabs/open-by-booking";

    /// <summary>The branch's <c>WalkInHoldbackMinutes</c> in every test here.</summary>
    private const int HoldbackMinutes = 20;

    // ------------------------------------------------------------ the diner who booked

    /// <summary>
    /// The booking's own diner opens the tab on the booked table, and is answered exactly as a scan
    /// of that table's QR code would answer them.
    /// </summary>
    [SkippableFact]
    public async Task The_booking_s_own_diner_opens_the_tab_on_the_booked_table_with_the_answer_a_scan_gives()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        await MoveAsync(factory, booking.Id, factory.Clock.UtcNow.AddMinutes(10));

        var response = await OpenByBookingAsync(diner, booking.Code, "phone-booker");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tab = body.GetProperty("tab");
        var tabId = tab.GetProperty("tabId").GetGuid();

        Assert.Equal(TabOpenOutcome.OpenedNewSession, EnumOf<TabOpenOutcome>(body.GetProperty("outcome")));
        Assert.False(body.GetProperty("wasReplay").GetBoolean());
        Assert.Equal("1", tab.GetProperty("tableLabel").GetString());
        Assert.Equal(ParticipantRole.Host, EnumOf<ParticipantRole>(tab.GetProperty("me").GetProperty("role")));

        // Member for member what a scan answers, so the app can treat the two as one thing. Both
        // are the host of a fresh tab, so the visibility rules shape both bodies the same way.
        using var anonymous = factory.CreateClient();
        var scan = await ScanAsync(anonymous, await QrTokenAsync(factory, branch.TableIds[1]), "phone-scanner");

        Assert.Equal(Members(scan), Members(body));
        Assert.Equal(Members(scan.GetProperty("token")), Members(body.GetProperty("token")));
        Assert.Equal(Members(scan.GetProperty("tab")), Members(tab));
        Assert.Equal(Members(scan.GetProperty("tab").GetProperty("me")), Members(tab.GetProperty("me")));

        // The token it hands back is a working participant token on that tab.
        using var onTab = factory.CreateClientWithToken(
            body.GetProperty("token").GetProperty("accessToken").GetString()!);

        Assert.Equal(HttpStatusCode.OK, (await onTab.GetAsync($"/api/tabs/{tabId}")).StatusCode);

        // And the party was seated as the booking, not as a walk-in: the sitting names the booking,
        // the booking is Seated, and the audit row says a diner did it. A walk-in seating would
        // leave the booking Confirmed while its party ate - flagged late, nudged, and one tap from
        // being marked a no-show.
        await using var db = fixture.CreateContext(factory.Clock);

        var session = await db.TableSessions.AsNoTracking()
            .SingleAsync(s => s.DiningTableId == branch.TableIds[0] && s.ClosedAtUtc == null);

        Assert.Equal(TableSessionSource.Reservation, session.Source);
        Assert.Equal(booking.Id, session.ReservationId);
        Assert.Equal(session.Id, (await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId)).TableSessionId);

        Assert.Equal(
            ReservationStatus.Seated,
            (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == booking.Id)).Status);

        var seating = await db.TableStateChanges.AsNoTracking().SingleAsync(c => c.TableSessionId == session.Id);

        Assert.Equal(ActorType.Diner, seating.ActorType);
        Assert.Equal(booking.Id, seating.ReservationId);
    }

    /// <summary>
    /// A retry with the same command id is the same request, and one with no command id is refused
    /// before anything runs.
    /// </summary>
    [SkippableFact]
    public async Task A_retry_with_the_same_command_id_answers_with_the_same_tab_and_one_without_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        await MoveAsync(factory, booking.Id, factory.Clock.UtcNow.AddMinutes(10));

        var commandId = Guid.CreateVersion7();

        var first = await OpenByBookingAsync(diner, booking.Code, "phone-flaky", commandId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = await first.Content.ReadFromJsonAsync<JsonElement>();

        var second = await OpenByBookingAsync(diner, booking.Code, "phone-flaky", commandId);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replay = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(original.GetProperty("wasReplay").GetBoolean());
        Assert.True(replay.GetProperty("wasReplay").GetBoolean());
        Assert.Equal(
            original.GetProperty("tab").GetProperty("tabId").GetGuid(),
            replay.GetProperty("tab").GetProperty("tabId").GetGuid());
        Assert.Equal(
            original.GetProperty("tab").GetProperty("me").GetProperty("participantId").GetGuid(),
            replay.GetProperty("tab").GetProperty("me").GetProperty("participantId").GetGuid());
        Assert.Equal(
            TabOpenOutcome.OpenedNewSession, EnumOf<TabOpenOutcome>(replay.GetProperty("outcome")));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(1, await db.Tabs.CountAsync(t => t.DiningTableId == branch.TableIds[0]));
            Assert.Equal(1, await db.TableSessions.CountAsync(s => s.DiningTableId == branch.TableIds[0]));
        }

        // Without one a retry cannot be told from a second tap, so the request is refused rather
        // than guessed at - by the group's filter, in its own words.
        var bare = await diner.PostAsJsonAsync(Route, new { bookingCode = booking.Code, deviceId = "phone-bare" });

        var problem = await RefusedAsync(bare, HttpStatusCode.BadRequest, "invalid-request");

        Assert.Contains("clientCommandId", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ nobody else

    /// <summary>
    /// Another diner quoting the code is answered exactly as for a code that does not exist, and the
    /// table is left alone.
    /// </summary>
    /// <remarks>
    /// The code is six characters somebody reads out at the door; it is not a secret and nothing may
    /// be authorised by knowing it. A distinct answer for "exists, not yours" would also let anyone
    /// with an account find out which codes are live.
    /// </remarks>
    [SkippableFact]
    public async Task Another_diner_quoting_the_code_is_answered_exactly_as_for_a_code_that_does_not_exist()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var booker = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(booker, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        await MoveAsync(factory, booking.Id, factory.Clock.UtcNow.AddMinutes(10));

        using var stranger = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var theirs = await RefusedAsync(
            await OpenByBookingAsync(stranger, booking.Code, "phone-stranger"),
            HttpStatusCode.NotFound,
            "booking-not-found");

        var nobodys = await RefusedAsync(
            await OpenByBookingAsync(stranger, await UnusedCodeAsync(factory), "phone-stranger"),
            HttpStatusCode.NotFound,
            "booking-not-found");

        // Nothing in the body tells the two apart - not a word, not a member.
        Assert.Equal(Members(nobodys), Members(theirs));

        foreach (var member in new[] { "type", "title", "status", "detail", "code" })
        {
            Assert.Equal(nobodys.GetProperty(member).ToString(), theirs.GetProperty(member).ToString());
        }

        Assert.False(theirs.TryGetProperty("context", out _));

        // The table was not touched: nobody was seated and the booking still waits for its party.
        Assert.False(await HasOpenSessionAsync(factory, branch.TableIds[0]));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(
                ReservationStatus.Confirmed,
                (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == booking.Id)).Status);
        }

        // And the code is real and in its window: the diner who booked it gets in.
        Assert.Equal(
            HttpStatusCode.OK, (await OpenByBookingAsync(booker, booking.Code, "phone-booker")).StatusCode);
    }

    /// <summary>
    /// Only a signed-in diner may ask, and the refusal comes from authorization rather than from a
    /// handler that happened to find nobody.
    /// </summary>
    /// <remarks>
    /// The scan routes beside this one sit in a group marked <c>AllowAnonymous</c>, and an
    /// <c>IAllowAnonymous</c> in an endpoint's metadata makes the authorization middleware wave it
    /// through whatever else is required. The <c>WWW-Authenticate</c> challenge is written only when
    /// authorization actually refused, which is how the two are told apart.
    /// </remarks>
    [SkippableFact]
    public async Task Only_a_signed_in_diner_may_ask_and_authorization_is_what_refuses_everyone_else()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        await MoveAsync(factory, booking.Id, factory.Clock.UtcNow.AddMinutes(10));

        using var anonymous = factory.CreateClient();

        var unauthenticated = await OpenByBookingAsync(anonymous, booking.Code, "phone-anonymous");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Contains(unauthenticated.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");

        // A phone already on a tab holds a participant token: no account, so no bookings.
        var scan = await ScanAsync(anonymous, await QrTokenAsync(factory, branch.TableIds[1]), "phone-on-a-tab");
        using var participant = factory.CreateClientWithToken(
            scan.GetProperty("token").GetProperty("accessToken").GetString()!);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await OpenByBookingAsync(participant, booking.Code, "phone-on-a-tab")).StatusCode);

        // Nor a waiter. Seating a booked party from the floor is seat-reservation.
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await OpenByBookingAsync(waiter, booking.Code, "tablet")).StatusCode);

        Assert.False(await HasOpenSessionAsync(factory, branch.TableIds[0]));
    }

    // ------------------------------------------------------------ the booking's state

    /// <summary>
    /// A booking that is not expecting its party is refused, with a code the app can turn into the
    /// right sentence - and the table is left alone.
    /// </summary>
    [SkippableTheory]
    [InlineData(ReservationStatus.PendingApproval, "booking-not-active")]
    [InlineData(ReservationStatus.CancelledByDiner, "booking-not-active")]
    [InlineData(ReservationStatus.CancelledByVenue, "booking-not-active")]
    [InlineData(ReservationStatus.NoShow, "booking-not-active")]
    [InlineData(ReservationStatus.Completed, "booking-ended")]
    public async Task A_booking_that_no_longer_expects_its_party_is_refused_with_a_code_the_app_can_explain(
        ReservationStatus status,
        string code)
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        if (status == ReservationStatus.PendingApproval)
        {
            // Through the real flow: a branch that approves every booking by hand.
            await SetPolicyAsync(factory, branch, "ReservationPolicy_AutoConfirm", 0);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        await MoveAsync(factory, booking.Id, factory.Clock.UtcNow.AddMinutes(10));
        await PutInStatusAsync(factory, booking.Id, status);

        var refused = await RefusedAsync(
            await OpenByBookingAsync(diner, booking.Code, "phone-booker"), HttpStatusCode.Conflict, code);

        var context = refused.GetProperty("context");

        Assert.Equal(booking.Id, context.GetProperty("reservationId").GetGuid());
        Assert.Equal((int)status, context.GetProperty("status").GetInt32());

        Assert.False(await HasOpenSessionAsync(factory, branch.TableIds[0]));

        await using var db = fixture.CreateContext(factory.Clock);

        Assert.Equal(status, (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == booking.Id)).Status);
    }

    /// <summary>
    /// Too early is refused with the moment the table becomes theirs, and that moment itself is
    /// allowed. The moment is the branch's walk-in holdback before the start.
    /// </summary>
    [SkippableFact]
    public async Task Too_early_is_refused_with_the_moment_the_table_becomes_theirs_and_that_moment_is_allowed()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        var now = factory.Clock.UtcNow;

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var early = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        var onTime = await BookAsync(diner, factory, branch, branch.TableIds[1], new TimeOnly(20, 0));

        // One minute before the branch starts holding the table back for the party...
        var earlyStart = now.AddMinutes(HoldbackMinutes + 1);
        await MoveAsync(factory, early.Id, earlyStart);

        // ...and exactly as it starts to.
        await MoveAsync(factory, onTime.Id, now.AddMinutes(HoldbackMinutes));

        var refused = await RefusedAsync(
            await OpenByBookingAsync(diner, early.Code, "phone-early"), HttpStatusCode.Conflict, "booking-too-early");

        var context = refused.GetProperty("context");

        Assert.Equal(earlyStart.AddMinutes(-HoldbackMinutes), context.GetProperty("earliestUtc").GetDateTime());
        Assert.Equal(earlyStart, context.GetProperty("startUtc").GetDateTime());
        Assert.Equal(early.Id, context.GetProperty("reservationId").GetGuid());
        Assert.Equal((int)ReservationStatus.Confirmed, context.GetProperty("status").GetInt32());

        Assert.False(await HasOpenSessionAsync(factory, branch.TableIds[0]));

        var allowed = await OpenByBookingAsync(diner, onTime.Code, "phone-on-time");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>
    /// Once the booking has ended it is refused. A party that is late - well past the grace, but
    /// before the end - is not: lateness is a waiter's decision to release the table, not the clock's.
    /// </summary>
    [SkippableFact]
    public async Task Once_the_booking_has_ended_it_is_refused_and_a_late_party_before_then_is_not()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        var now = factory.Clock.UtcNow;

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var over = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        var late = await BookAsync(diner, factory, branch, branch.TableIds[1], new TimeOnly(20, 0));

        // Ended at exactly this instant.
        await MoveAsync(factory, over.Id, now.AddMinutes(-90), endUtc: now);

        // Started an hour ago - long past the branch's grace - and still running.
        await MoveAsync(factory, late.Id, now.AddMinutes(-60), endUtc: now.AddMinutes(30));

        var refused = await RefusedAsync(
            await OpenByBookingAsync(diner, over.Code, "phone-over"), HttpStatusCode.Conflict, "booking-ended");

        Assert.Equal(now, refused.GetProperty("context").GetProperty("endUtc").GetDateTime());
        Assert.False(await HasOpenSessionAsync(factory, branch.TableIds[0]));

        Assert.Equal(HttpStatusCode.OK, (await OpenByBookingAsync(diner, late.Code, "phone-late")).StatusCode);
    }

    /// <summary>
    /// A booked table that is out of service is refused exactly as a scan of it is - and so is one
    /// taken off the floor plan, which a scan cannot even find.
    /// </summary>
    [SkippableFact]
    public async Task A_booked_table_out_of_service_is_refused_as_a_scan_of_it_is()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var broken = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        var removed = await BookAsync(diner, factory, branch, branch.TableIds[1], new TimeOnly(20, 0));
        await MoveAsync(factory, broken.Id, factory.Clock.UtcNow.AddMinutes(10));
        await MoveAsync(factory, removed.Id, factory.Clock.UtcNow.AddMinutes(10));

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var marked = await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{branch.TableIds[0]}/out-of-service",
            new { clientCommandId = Guid.CreateVersion7(), reason = "wobbly leg" });

        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        using var anonymous = factory.CreateClient();

        var byScan = await anonymous.PostAsJsonAsync(
            "/api/tabs/open",
            new
            {
                qrToken = await QrTokenAsync(factory, branch.TableIds[0]),
                deviceId = "phone-scanner",
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Conflict, byScan.StatusCode);
        var scanned = await byScan.Content.ReadFromJsonAsync<JsonElement>();

        var byBooking = await RefusedAsync(
            await OpenByBookingAsync(diner, broken.Code, "phone-booker"),
            HttpStatusCode.Conflict,
            scanned.GetProperty("code").GetString()!);

        Assert.Equal(scanned.GetProperty("detail").GetString(), byBooking.GetProperty("detail").GetString());
        Assert.False(await HasOpenSessionAsync(factory, branch.TableIds[0]));

        // Taken off the floor plan with the booking still on it: the same refusal, rather than the
        // scan's "no such table" - the diner has a booking for it, and needs to find a waiter.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await db.DiningTables
                .Where(t => t.Id == branch.TableIds[1])
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false));
        }

        var offThePlan = await RefusedAsync(
            await OpenByBookingAsync(diner, removed.Code, "phone-booker"),
            HttpStatusCode.Conflict,
            scanned.GetProperty("code").GetString()!);

        Assert.Contains("out of service", offThePlan.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(await HasOpenSessionAsync(factory, branch.TableIds[1]));
    }

    // ------------------------------------------------------------ the tab at the table

    /// <summary>
    /// Whoever gets to the table first, the second phone is treated exactly as a scan treats it: one
    /// tab, the first phone hosts it, and the second waits to be let on.
    /// </summary>
    [SkippableFact]
    public async Task A_second_phone_at_the_table_is_treated_exactly_as_a_scan_would_treat_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var first = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        var second = await BookAsync(diner, factory, branch, branch.TableIds[1], new TimeOnly(20, 0));
        await MoveAsync(factory, first.Id, factory.Clock.UtcNow.AddMinutes(10));
        await MoveAsync(factory, second.Id, factory.Clock.UtcNow.AddMinutes(10));

        using var anonymous = factory.CreateClient();

        // The booker first, then a friend scans the QR code on the table.
        var opened = await OpenByBookingAsync(diner, first.Code, "phone-booker");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var host = await opened.Content.ReadFromJsonAsync<JsonElement>();

        var friend = await ScanAsync(anonymous, await QrTokenAsync(factory, branch.TableIds[0]), "phone-friend");

        Assert.Equal(TabOpenOutcome.JoinedExistingTab, EnumOf<TabOpenOutcome>(friend.GetProperty("outcome")));
        Assert.Equal(
            host.GetProperty("tab").GetProperty("tabId").GetGuid(),
            friend.GetProperty("tab").GetProperty("tabId").GetGuid());
        Assert.Equal(
            ParticipantStatus.PendingApproval,
            EnumOf<ParticipantStatus>(friend.GetProperty("tab").GetProperty("me").GetProperty("status")));

        // The other way round: a friend got there first and scanned, then the booker arrives.
        var early = await ScanAsync(anonymous, await QrTokenAsync(factory, branch.TableIds[1]), "phone-early-friend");

        Assert.Equal(TabOpenOutcome.OpenedNewSession, EnumOf<TabOpenOutcome>(early.GetProperty("outcome")));

        var arrived = await OpenByBookingAsync(diner, second.Code, "phone-booker-second");
        Assert.Equal(HttpStatusCode.OK, arrived.StatusCode);
        var booker = await arrived.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(TabOpenOutcome.JoinedExistingTab, EnumOf<TabOpenOutcome>(booker.GetProperty("outcome")));
        Assert.Equal(
            early.GetProperty("tab").GetProperty("tabId").GetGuid(),
            booker.GetProperty("tab").GetProperty("tabId").GetGuid());

        var me = booker.GetProperty("tab").GetProperty("me");

        Assert.Equal(ParticipantRole.Guest, EnumOf<ParticipantRole>(me.GetProperty("role")));
        Assert.Equal(ParticipantStatus.PendingApproval, EnumOf<ParticipantStatus>(me.GetProperty("status")));
    }

    /// <summary>
    /// A booking a waiter already seated opens its tab on that sitting, whatever the clock says - the
    /// venue put the party at the table, so the table is theirs.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_a_waiter_already_seated_opens_its_tab_on_that_sitting_whatever_the_clock_says()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));

        // Nearly an hour early - far outside the window - and the table was free, so they sat down.
        await MoveAsync(factory, booking.Id, factory.Clock.UtcNow.AddMinutes(55));

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var seated = await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{branch.TableIds[0]}/seat-reservation",
            new { reservationId = booking.Id, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);

        var sessionId = (await seated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tableSessionId").GetGuid();

        var response = await OpenByBookingAsync(diner, booking.Code, "phone-booker");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(TabOpenOutcome.OpenedOnExistingSession, EnumOf<TabOpenOutcome>(body.GetProperty("outcome")));
        Assert.Equal(
            ParticipantRole.Host,
            EnumOf<ParticipantRole>(body.GetProperty("tab").GetProperty("me").GetProperty("role")));

        await using var db = fixture.CreateContext(factory.Clock);

        var tabId = body.GetProperty("tab").GetProperty("tabId").GetGuid();

        Assert.Equal(sessionId, (await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId)).TableSessionId);
    }

    /// <summary>
    /// A code typed the way people type one - lower case, a dash in the middle, a space either side -
    /// still finds the booking.
    /// </summary>
    [SkippableFact]
    public async Task A_code_typed_in_lower_case_with_a_dash_or_a_space_still_finds_the_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch, branch.TableIds[0], new TimeOnly(18, 0));
        await MoveAsync(factory, booking.Id, factory.Clock.UtcNow.AddMinutes(10));

        var code = booking.Code;
        var dashed = $"  {code[..3]}-{code[3..]} ".ToLowerInvariant();

        var first = await OpenByBookingAsync(diner, dashed, "phone-typing");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var opened = await first.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(TabOpenOutcome.OpenedNewSession, EnumOf<TabOpenOutcome>(opened.GetProperty("outcome")));

        // Typed again from the same phone with a space instead: the same person on the same tab.
        var spaced = $"{code[..3]} {code[3..]}".ToLowerInvariant();

        var again = await OpenByBookingAsync(diner, spaced, "phone-typing");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var landed = await again.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            opened.GetProperty("tab").GetProperty("tabId").GetGuid(),
            landed.GetProperty("tab").GetProperty("tabId").GetGuid());
        Assert.Equal(
            opened.GetProperty("tab").GetProperty("me").GetProperty("participantId").GetGuid(),
            landed.GetProperty("tab").GetProperty("me").GetProperty("participantId").GetGuid());
    }

    // ------------------------------------------------------------ helpers

    private sealed record Booking(Guid Id, string Code);

    /// <summary>The API, with its clock rounded down to the second. Back, never forward.</summary>
    private YallaApiFactory NewFactory()
    {
        var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        var now = factory.Clock.UtcNow;

        factory.Clock.UtcNow = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

        return factory;
    }

    /// <summary>A branch with two tables and the walk-in holdback at <see cref="HoldbackMinutes"/>.</summary>
    private async Task<AuthBranch> ArrangeAsync(YallaApiFactory factory)
    {
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        await SetPolicyAsync(factory, branch, "ReservationPolicy_WalkInHoldbackMinutes", HoldbackMinutes);

        return branch;
    }

    /// <summary>One column of the branch's policy. The policy is an owned type mapped onto the branch row.</summary>
    private async Task SetPolicyAsync(YallaApiFactory factory, AuthBranch branch, string column, int value)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        // One literal statement per column, values as parameters: a column name cannot be a
        // parameter, and SQL assembled out of strings is the habit the analyser is right to refuse.
        var updated = column switch
        {
            "ReservationPolicy_WalkInHoldbackMinutes" => await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Branches SET ReservationPolicy_WalkInHoldbackMinutes = {value} WHERE Id = {branch.BranchId}"),
            "ReservationPolicy_AutoConfirm" => await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Branches SET ReservationPolicy_AutoConfirm = {value != 0} WHERE Id = {branch.BranchId}"),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Not a column these tests set."),
        };

        Assert.Equal(1, updated);
    }

    /// <summary>A date the branch accepts: two days out, past the lead time and inside the window.</summary>
    private static DateOnly BookingDate(YallaApiFactory factory)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        return DateOnly.FromDateTime(localNow).AddDays(2);
    }

    /// <summary>Books a table through the real endpoint, so the booking is the diner's by the real rule.</summary>
    private static async Task<Booking> BookAsync(
        HttpClient diner,
        YallaApiFactory factory,
        AuthBranch branch,
        Guid tableId,
        TimeOnly time)
    {
        var response = await diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId,
                date = BookingDate(factory).ToString("yyyy-MM-dd"),
                time = time.ToString("HH\\:mm"),
                partySize = 2,
                guestName = "Ani Test",
                guestPhone = "+37411223344",
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return new Booking(body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    /// <summary>
    /// Moves a booking to start at <paramref name="startUtc"/>, keeping its length unless an end is
    /// given. Time is moved here rather than on the clock - see the class remarks.
    /// </summary>
    private async Task MoveAsync(YallaApiFactory factory, Guid bookingId, DateTime startUtc, DateTime? endUtc = null)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var booked = await db.Reservations.AsNoTracking()
            .Where(r => r.Id == bookingId)
            .Select(r => new { r.StartUtc, r.EndUtc })
            .SingleAsync();

        var end = endUtc ?? startUtc + (booked.EndUtc - booked.StartUtc);

        await db.Reservations
            .Where(r => r.Id == bookingId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.StartUtc, startUtc)
                .SetProperty(r => r.EndUtc, end));
    }

    /// <summary>
    /// Takes a booking to <paramref name="status"/> through the domain's own transitions, so it is
    /// a state the product can actually reach. Pending approval is reached by booking at a branch
    /// that approves by hand, before this is called.
    /// </summary>
    private async Task PutInStatusAsync(YallaApiFactory factory, Guid bookingId, ReservationStatus status)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var booking = await db.Reservations.SingleAsync(r => r.Id == bookingId);
        var now = factory.Clock.UtcNow;

        switch (status)
        {
            case ReservationStatus.PendingApproval:
                Assert.Equal(ReservationStatus.PendingApproval, booking.Status);
                return;

            case ReservationStatus.CancelledByDiner:
                booking.CancelByDiner(now, "Plans changed", afterDeadline: false);
                break;

            case ReservationStatus.CancelledByVenue:
                booking.CancelByVenue(now, "Kitchen closed tonight");
                break;

            case ReservationStatus.NoShow:
                booking.MarkNoShow(now);
                break;

            case ReservationStatus.Completed:
                booking.MarkSeated();
                booking.MarkCompleted();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Not a state this test reaches.");
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A well-formed code that no booking carries.</summary>
    private async Task<string> UnusedCodeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        string code;

        do
        {
            code = ReservationCode.Generate();
        }
        while (await db.Reservations.AnyAsync(r => r.Code == code));

        return code;
    }

    private async Task<string> QrTokenAsync(YallaApiFactory factory, Guid tableId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.DiningTables.AsNoTracking()
            .Where(t => t.Id == tableId)
            .Select(t => t.QrToken)
            .SingleAsync();
    }

    private async Task<bool> HasOpenSessionAsync(YallaApiFactory factory, Guid tableId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.TableSessions.AnyAsync(s => s.DiningTableId == tableId && s.ClosedAtUtc == null);
    }

    private static Task<HttpResponseMessage> OpenByBookingAsync(
        HttpClient client,
        string bookingCode,
        string deviceId,
        Guid? clientCommandId = null) =>
        client.PostAsJsonAsync(
            Route,
            new
            {
                bookingCode,
                deviceId,
                clientCommandId = clientCommandId ?? Guid.CreateVersion7(),
                displayName = deviceId,
            });

    /// <summary>The table's QR code, scanned the way the app scans it.</summary>
    private static async Task<JsonElement> ScanAsync(HttpClient client, string qrToken, string deviceId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken, deviceId, clientCommandId = Guid.CreateVersion7(), displayName = deviceId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The refusal, checked for its status and its code - not merely some failure.</summary>
    private static async Task<JsonElement> RefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(code, problem.GetProperty("code").GetString());

        return problem;
    }

    private static string[] Members(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A diner account, through the real sign-in flow.</summary>
    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    /// <summary>An enum however it was serialised - as its number or its name.</summary>
    private static T EnumOf<T>(JsonElement element) where T : struct, Enum =>
        element.ValueKind == JsonValueKind.Number
            ? (T)Enum.ToObject(typeof(T), element.GetInt32())
            : Enum.Parse<T>(element.GetString()!, ignoreCase: true);
}
