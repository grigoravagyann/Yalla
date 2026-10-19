using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The Overview's booking-row menu, called with the venue console's own token (venue HANDOFF §6): an
/// owner, a general manager and a branch manager signed in to the admin panel seat a booked party, mark a
/// no-show, let a booking go, free a finished table and answer a request - and the people who could not
/// before still cannot.
/// </summary>
/// <remarks>
/// <para>
/// These routes were built for the tablet and carry <c>WaiterOrAbove</c>, which admits an admin-panel
/// (<c>VenueUser</c>) identity of those three ranks as well as a PIN session; the scope is drawn from the
/// stored row and the route's branch. So opening them to the console needed no policy change - what was
/// missing was the proof, and this is it. Each row sends one action as every kind of caller.
/// </para>
/// <para>
/// <b>The refused go first, against one booking, and it must be untouched afterwards</b>: a waiter of
/// the sibling branch (refused at this branch before, and still), a cook, a diner, a manager of the
/// sibling branch, another venue's manager and waiter. <b>The admitted each get a booking of their own</b>,
/// on a table of their own: the branch's own waiter (what the tablet always did), the branch manager, the
/// general manager and the owner, all three of the latter with the console's email sign-in.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ConsoleBookingActionsTests(SqlServerFixture fixture)
{
    private enum Caller
    {
        Anonymous,
        Diner,
        KitchenSession,
        SiblingBranchWaiter,
        SiblingBranchManager,
        OtherVenueWaiter,
        OtherVenueManager,
        Waiter,
        BranchManager,
        GeneralManager,
        Owner,
    }

    /// <summary>What a booking must be before the action, and the call that acts on it.</summary>
    private sealed record Action(
        ReservationStatus Before,
        ReservationStatus After,
        Func<World, Booking, HttpClient, Task<HttpResponseMessage>> Send,
        bool Seated = false);

    private sealed record Booking(Guid Id, Guid TableId);

    private static readonly Dictionary<string, Action> Actions = new(StringComparer.Ordinal)
    {
        ["seat-reservation"] = new(
            ReservationStatus.Confirmed,
            ReservationStatus.Seated,
            (w, b, c) => c.PostAsJsonAsync(
                $"/api/branches/{w.Branch.BranchId}/tables/{b.TableId}/seat-reservation",
                new { reservationId = b.Id, clientCommandId = Guid.CreateVersion7() })),

        ["no-show"] = new(
            ReservationStatus.Confirmed,
            ReservationStatus.NoShow,
            (_, b, c) => c.PostAsJsonAsync(
                $"/api/reservations/{b.Id}/no-show", new { clientCommandId = Guid.CreateVersion7() })),

        // The console's "cancel the booking": let go as the venue's doing, which does not count against the diner.
        ["release"] = new(
            ReservationStatus.Confirmed,
            ReservationStatus.CancelledByVenue,
            (_, b, c) => c.PostAsJsonAsync(
                $"/api/reservations/{b.Id}/release",
                new { outcome = (int)ReleaseOutcome.CancelledByVenue, clientCommandId = Guid.CreateVersion7() })),

        // "Finish, free the table" on a seated row: the sitting closes and the booking completes. The console
        // names the booking it means, so a stale row cannot end somebody else's sitting.
        ["free"] = new(
            ReservationStatus.Seated,
            ReservationStatus.Completed,
            (w, b, c) => c.PostAsJsonAsync(
                $"/api/branches/{w.Branch.BranchId}/tables/{b.TableId}/free",
                new { clientCommandId = Guid.CreateVersion7(), reservationId = b.Id }),
            Seated: true),

        ["approve"] = new(
            ReservationStatus.PendingApproval,
            ReservationStatus.Confirmed,
            (_, b, c) => c.PostAsJsonAsync($"/api/reservations/{b.Id}/approve", new { })),

        ["reject"] = new(
            ReservationStatus.PendingApproval,
            ReservationStatus.CancelledByVenue,
            (_, b, c) => c.PostAsJsonAsync($"/api/reservations/{b.Id}/reject", new { reason = "Private event." })),
    };

    public static TheoryData<string> ActionNames()
    {
        var data = new TheoryData<string>();

        foreach (var name in Actions.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(ActionNames))]
    public async Task The_console_acts_on_its_own_branch_bookings_and_nobody_new_gets_in(string name)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var action = Actions[name];

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        var world = await ArrangeWorldAsync(factory);

        try
        {
            var failures = new List<string>();
            var tableIndex = 0;

            // One booking every refused caller tries, on the first table. Nothing they send may move it.
            var target = await AddBookingAsync(world, action, tableIndex++);

            foreach (var caller in Enum.GetValues<Caller>().Where(c => Expected(c) != HttpStatusCode.OK))
            {
                using var response = await action.Send(world, target, world.Clients[caller]);

                if (response.StatusCode != Expected(caller))
                {
                    failures.Add($"  {caller}: expected {(int)Expected(caller)}, got {(int)response.StatusCode} - "
                                 + await BodyAsync(response));
                }
            }

            var untouched = await StatusOfAsync(world, target.Id);

            if (untouched != action.Before)
            {
                failures.Add($"  the refused callers' booking moved from {action.Before} to {untouched}");
            }

            foreach (var caller in Enum.GetValues<Caller>().Where(c => Expected(c) == HttpStatusCode.OK))
            {
                var own = await AddBookingAsync(world, action, tableIndex++);

                using var response = await action.Send(world, own, world.Clients[caller]);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    failures.Add($"  {caller}: expected 200, got {(int)response.StatusCode} - " + await BodyAsync(response));
                    continue;
                }

                var after = await StatusOfAsync(world, own.Id);

                if (after != action.After)
                {
                    failures.Add($"  {caller}: the booking is {after}, expected {action.After}");
                }
            }

            Assert.True(failures.Count == 0, $"{name}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            foreach (var client in world.Clients.Values)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// "Finish" from a stale Overview row: the waiter already freed the booking's table and seated a walk-in.
    /// Naming the booking refuses to end the walk-in's sitting, however often it is sent; leaving it out frees
    /// the table as the tablet always has.
    /// </summary>
    [SkippableFact]
    public async Task Finishing_a_booking_whose_table_has_moved_on_leaves_the_new_sitting_alone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        var world = await ArrangeWorldAsync(factory);

        try
        {
            var booking = await AddBookingAsync(world, Actions["free"], tableIndex: 0);
            var owner = world.Clients[Caller.Owner];
            var waiter = world.Clients[Caller.Waiter];
            var table = $"/api/branches/{world.Branch.BranchId}/tables/{booking.TableId}";

            // The floor moves on: the party leaves, a walk-in sits down.
            Assert.Equal(
                HttpStatusCode.OK,
                (await waiter.PostAsJsonAsync($"{table}/free", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);
            Assert.Equal(
                HttpStatusCode.OK,
                (await waiter.PostAsJsonAsync(
                    $"{table}/seat-walk-in", new { partySize = 3, clientCommandId = Guid.CreateVersion7() })).StatusCode);

            var walkIn = await OpenSessionAsync(world, booking.TableId);
            Assert.NotNull(walkIn);

            // The manager's stale "Finish", twice - a retry is a new tap.
            foreach (var attempt in new[] { 1, 2 })
            {
                using var refused = await owner.PostAsJsonAsync(
                    $"{table}/free", new { clientCommandId = Guid.CreateVersion7(), reservationId = booking.Id });

                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

                var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("table-sitting-changed", problem.GetProperty("code").GetString());

                var context = problem.GetProperty("context");
                Assert.Equal(booking.TableId, context.GetProperty("tableId").GetGuid());
                Assert.Equal(booking.Id, context.GetProperty("reservationId").GetGuid());
                Assert.Equal(walkIn, await OpenSessionAsync(world, booking.TableId));
                _ = attempt;
            }

            Assert.Equal(ReservationStatus.Completed, await StatusOfAsync(world, booking.Id));

            // Without a booking named it is the tablet's free, unchanged: whoever is at the table leaves.
            Assert.Equal(
                HttpStatusCode.OK,
                (await owner.PostAsJsonAsync($"{table}/free", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);
            Assert.Null(await OpenSessionAsync(world, booking.TableId));

            // And a booking named at a table nobody is sitting at is the same refusal: its sitting is over.
            using var empty = await owner.PostAsJsonAsync(
                $"{table}/free", new { clientCommandId = Guid.CreateVersion7(), reservationId = booking.Id });

            Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
            Assert.Equal(
                "table-sitting-changed",
                (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally
        {
            foreach (var client in world.Clients.Values)
            {
                client.Dispose();
            }
        }
    }

    private async Task<Guid?> OpenSessionAsync(World w, Guid tableId)
    {
        await using var db = fixture.CreateContext(w.Factory.Clock);

        return await db.TableSessions
            .AsNoTracking()
            .Where(s => s.DiningTableId == tableId && s.ClosedAtUtc == null)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync();
    }

    private static HttpStatusCode Expected(Caller caller) => caller switch
    {
        Caller.Anonymous => HttpStatusCode.Unauthorized,
        Caller.Waiter or Caller.BranchManager or Caller.GeneralManager or Caller.Owner => HttpStatusCode.OK,
        _ => HttpStatusCode.Forbidden,
    };

    // ------------------------------------------------------------ the world

    private sealed class World
    {
        public required YallaApiFactory Factory { get; init; }

        public required AuthBranch Branch { get; init; }

        public required IReadOnlyDictionary<Caller, HttpClient> Clients { get; init; }
    }

    private async Task<World> ArrangeWorldAsync(YallaApiFactory factory)
    {
        AuthBranch branch;
        AuthBranch otherVenue;
        PanelAccount owner;
        PanelAccount generalManager;
        PanelAccount siblingManager;
        Guid siblingBranchId;
        Guid siblingWaiterId;
        Guid cookId;

        const string siblingWaiterPin = "6620";

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            // Twelve tables: one for the refused callers' booking and one per admitted caller, with room.
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 12);
            siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            otherVenue = await AuthTestData.CreateBranchAsync(db);

            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            generalManager = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId);
            siblingManager = await AuthTestData.SeedManagerAsync(db, branch.VenueId, siblingBranchId);
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);

            // A waiter of the same venue whose tablet is at the sibling branch: confined there before, and still.
            var siblingWaiter = new StaffMember(
                branch.VenueId, "Sibling Waiter", $"+37455{Random.Shared.Next(100_000, 999_999)}", StaffRole.Waiter,
                new SecretHasher().Hash(siblingWaiterPin), siblingBranchId);

            db.StaffMembers.Add(siblingWaiter);
            await db.SaveChangesAsync();
            siblingWaiterId = siblingWaiter.Id;
        }

        var ownerToken = await AuthTestData.SignInAsync(factory, owner);

        var clients = new Dictionary<Caller, HttpClient>
        {
            [Caller.Anonymous] = factory.CreateClient(),
            [Caller.Diner] = factory.CreateClientWithToken(await SignInDinerAsync(factory)),
            [Caller.KitchenSession] = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cookId)),
            [Caller.SiblingBranchWaiter] = factory.CreateClientWithToken(
                await SignInOnTabletAsync(factory, ownerToken, siblingBranchId, siblingWaiterId, siblingWaiterPin)),
            [Caller.SiblingBranchManager] = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, siblingManager)),
            [Caller.OtherVenueWaiter] = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, otherVenue)),
            [Caller.OtherVenueManager] = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, otherVenue)),
            [Caller.Waiter] = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch)),
            [Caller.BranchManager] = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch)),
            [Caller.GeneralManager] = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, generalManager)),
            [Caller.Owner] = factory.CreateClientWithToken(ownerToken),
        };

        return new World { Factory = factory, Branch = branch, Clients = clients };
    }

    /// <summary>
    /// A booking the action applies to, on its own table: a confirmed one that started a quarter of an hour
    /// ago (the late row), seated at its table when the action frees one, or tomorrow's request for the
    /// approval pair.
    /// </summary>
    private async Task<Booking> AddBookingAsync(World w, Action action, int tableIndex)
    {
        var now = w.Factory.Clock.UtcNow;
        var asBuilt = new TestBranch(
            w.Branch.VenueId, w.Branch.BranchId, w.Branch.WaiterId, w.Branch.ManagerId, w.Branch.TableIds, "Asia/Yerevan");

        await using var db = fixture.CreateContext(w.Factory.Clock);

        var startLocal = now.AddHours(4).AddMinutes(-15);
        var date = action.Before == ReservationStatus.PendingApproval
            ? DateOnly.FromDateTime(startLocal).AddDays(1)
            : DateOnly.FromDateTime(startLocal);

        var time = action.Before == ReservationStatus.PendingApproval
            ? new TimeOnly(19, 0)
            : TimeOnly.FromDateTime(startLocal);

        var booking = await ConsoleTestData.AddBookingAsync(
            db,
            asBuilt,
            date,
            action.Before == ReservationStatus.PendingApproval ? ReservationStatus.PendingApproval : ReservationStatus.Confirmed,
            partySize: 2,
            tableIndex: tableIndex,
            localTime: time);

        if (action.Seated)
        {
            var table = await db.DiningTables.FirstAsync(t => t.Id == booking.DiningTableId);
            var row = await db.Reservations.FirstAsync(r => r.Id == booking.Id);
            var session = Yalla.Domain.Occupancy.TableSession.SeatReservation(
                w.Branch.BranchId, table.Id, booking.Id, booking.PartySize, now.AddMinutes(-10), w.Branch.WaiterId);

            row.MarkSeated();
            db.TableSessions.Add(session);
            table.Occupy(session.Id);
            await db.SaveChangesAsync();
        }

        return new Booking(booking.Id, booking.DiningTableId);
    }

    private async Task<ReservationStatus> StatusOfAsync(World w, Guid reservationId)
    {
        await using var db = fixture.CreateContext(w.Factory.Clock);

        return await db.Reservations.AsNoTracking().Where(r => r.Id == reservationId).Select(r => r.Status).FirstAsync();
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        return body.Length == 0 ? "(no body)" : body[..Math.Min(body.Length, 300)];
    }

    /// <summary>Enrols a tablet at <paramref name="branchId"/> with the owner's console token and taps a PIN on it.</summary>
    private static async Task<string> SignInOnTabletAsync(
        YallaApiFactory factory, string ownerToken, Guid branchId, Guid staffMemberId, string pin)
    {
        using var owner = factory.CreateClientWithToken(ownerToken);

        var created = await owner.PostAsJsonAsync($"/api/branches/{branchId}/devices/enrolment-codes", new { });
        created.EnsureSuccessStatusCode();

        var code = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

        using var anonymous = factory.CreateClient();
        var enrolled = await anonymous.PostAsJsonAsync(
            "/api/auth/staff/enrol", new { code, deviceId = $"browser-{Guid.NewGuid():N}", deviceName = "Sibling tablet" });

        enrolled.EnsureSuccessStatusCode();

        var deviceToken = (await enrolled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deviceToken").GetString()!;

        using var tablet = factory.CreateClientWithToken(deviceToken);
        var session = await tablet.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId, pin });

        session.EnsureSuccessStatusCode();

        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
}
