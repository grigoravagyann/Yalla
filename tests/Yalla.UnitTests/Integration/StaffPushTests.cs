using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Staff push end to end: the device's subscription and switches, the save that writes the outbox message, and the
/// fan-out that encrypts one push per wanting device - in its language, on shift or not, and never to a gone browser.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class StaffPushTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Settings_default_by_role_and_device_and_refuse_what_cannot_be_used()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var rig = await Rig.StartAsync(fixture);

        var waiter = await rig.GetSettingsAsync(rig.WaiterPhone);
        Assert.True(waiter.GetProperty("approvals").GetBoolean());
        Assert.True(waiter.GetProperty("guestMessages").GetBoolean());
        Assert.True(waiter.GetProperty("tableCalls").GetBoolean());
        Assert.True(waiter.GetProperty("takeaway").GetBoolean());
        Assert.False(waiter.GetProperty("kitchenReady").GetBoolean());
        Assert.True(waiter.GetProperty("onlyDuringShift").GetBoolean());
        Assert.True(waiter.GetProperty("isDefault").GetBoolean());
        Assert.False(waiter.GetProperty("subscribed").GetBoolean());

        Assert.False((await rig.GetSettingsAsync(rig.ManagerPhone)).GetProperty("onlyDuringShift").GetBoolean());
        Assert.False((await rig.GetSettingsAsync(rig.Tablet)).GetProperty("onlyDuringShift").GetBoolean());

        // The key, readable with the device token alone; enabled because Development made a pair for the run.
        using var bare = rig.Factory.CreateClientWithToken(rig.WaiterDeviceToken);
        var config = await (await bare.GetAsync("/api/staff/push-config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(config.GetProperty("enabled").GetBoolean());
        Assert.Equal(65, Yalla.Infrastructure.Notifications.Base64Url.Decode(config.GetProperty("publicKey").GetString()!).Length);

        // The device's own settings need its person signed in; an admin-panel token has no device at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.GetAsync("/api/staff/device/notification-preferences")).StatusCode);
        using var panel = rig.Factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(rig.Factory, rig.Branch));
        Assert.Equal(HttpStatusCode.Forbidden, (await panel.GetAsync("/api/staff/push-config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await panel.PutAsJsonAsync("/api/staff/device/push-subscription", new { })).StatusCode);

        var badLocale = await rig.WaiterPhone.PutAsJsonAsync("/api/staff/device/notification-preferences", Switches(locale: "de"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badLocale.StatusCode);

        var browser = new Browser();
        var foreignHost = await rig.WaiterPhone.PutAsJsonAsync(
            "/api/staff/device/push-subscription",
            new { endpoint = "https://internal.example/admin", keys = new { p256dh = browser.P256dh, auth = browser.Auth } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignHost.StatusCode);
        Assert.Contains("endpoint", await foreignHost.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A known push service's host is not enough: not on another port, and not with somebody's credentials in it.
        foreach (var crafted in (string[])["https://push.test:8443/w/x", "https://user:secret@push.test/w/x", "http://push.test/w/x"])
        {
            var refused = await rig.WaiterPhone.PutAsJsonAsync(
                "/api/staff/device/push-subscription",
                new { endpoint = crafted, keys = new { p256dh = browser.P256dh, auth = browser.Auth } });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        }

        var badKey = await rig.WaiterPhone.PutAsJsonAsync(
            "/api/staff/device/push-subscription",
            new { endpoint = browser.Endpoint, keys = new { p256dh = "not-a-key", auth = browser.Auth } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badKey.StatusCode);

        // The right shape but not a point on P-256: Windows' CNG throws PlatformNotSupportedException for it, which was
        // a 500 until it was caught with the rest (found by live QA, 2026-10-24).
        var offCurve = await rig.WaiterPhone.PutAsJsonAsync(
            "/api/staff/device/push-subscription",
            new
            {
                endpoint = browser.Endpoint,
                keys = new { p256dh = Yalla.Infrastructure.Notifications.Base64Url.Encode([0x04, .. new byte[64]]), auth = browser.Auth },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, offCurve.StatusCode);
        Assert.Contains("p256dh", await offCurve.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Saved switches read back, with the language.
        var saved = await (await rig.WaiterPhone.PutAsJsonAsync(
                "/api/staff/device/notification-preferences", Switches(kitchenReady: true, onlyDuringShift: false, locale: "ru")))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(saved.GetProperty("kitchenReady").GetBoolean());
        Assert.False(saved.GetProperty("onlyDuringShift").GetBoolean());
        Assert.Equal("ru", saved.GetProperty("locale").GetString());
        Assert.False(saved.GetProperty("isDefault").GetBoolean());

        await rig.SubscribeAsync(rig.WaiterPhone, browser);
        Assert.True((await rig.GetSettingsAsync(rig.WaiterPhone)).GetProperty("subscribed").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await rig.WaiterPhone.DeleteAsync("/api/staff/device/push-subscription")).StatusCode);
        Assert.False((await rig.GetSettingsAsync(rig.WaiterPhone)).GetProperty("subscribed").GetBoolean());
    }

    [SkippableFact]
    public async Task A_table_call_reaches_each_device_that_wants_it_in_its_language_and_a_waiter_only_on_shift()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var rig = await Rig.StartAsync(fixture);

        var waiterBrowser = new Browser();
        var managerBrowser = new Browser();
        var tabletBrowser = new Browser();

        await rig.SubscribeAsync(rig.WaiterPhone, waiterBrowser);
        await rig.SubscribeAsync(rig.ManagerPhone, managerBrowser);
        await rig.SubscribeAsync(rig.Tablet, tabletBrowser);
        await rig.ManagerPhone.PutAsJsonAsync("/api/staff/device/notification-preferences", Switches(locale: "en"));

        var tab = await rig.OpenTabAsync();
        var bill = await rig.RaiseCallAsync(tab, ServiceRequestPreset.TheBill);

        await rig.DispatchAsync($"staff-push:call:{bill}");

        // The waiter is off shift and on the default "only during my shift": quiet. The manager and the tablet hear.
        Assert.DoesNotContain(rig.Push.Sent, p => p.Endpoint == waiterBrowser.Endpoint);

        var toManager = Assert.Single(rig.Push.Sent, p => p.Endpoint == managerBrowser.Endpoint);
        Assert.Single(rig.Push.Sent, p => p.Endpoint == tabletBrowser.Endpoint);

        var payload = managerBrowser.Open(toManager.Body);
        Assert.Equal("Table 1 · asking for the bill", payload.GetProperty("title").GetString());
        Assert.Equal("tableCalls", payload.GetProperty("kind").GetString());
        Assert.Equal($"/staff/now?call={bill}", payload.GetProperty("url").GetString());
        Assert.Equal($"call:{rig.Branch.FirstTableId}", payload.GetProperty("tag").GetString());
        Assert.Equal(["body", "kind", "tag", "title", "url"], payload.EnumerateObject().Select(p => p.Name).Order().ToArray());

        Assert.Equal("900", toManager.Headers["TTL"]);
        Assert.Equal("aes128gcm", toManager.Headers["Content-Encoding"]);
        Assert.StartsWith("vapid t=", toManager.Headers["Authorization"], StringComparison.Ordinal);

        // On shift now, the waiter hears the next one - in Armenian, the default.
        await rig.PutWaiterOnShiftAsync();
        rig.Push.Sent.Clear();

        var water = await rig.RaiseCallAsync(tab, ServiceRequestPreset.Water);
        await rig.DispatchAsync($"staff-push:call:{water}");

        var toWaiter = Assert.Single(rig.Push.Sent, p => p.Endpoint == waiterBrowser.Endpoint);
        Assert.Equal("Սեղան 1 · կանչում է", waiterBrowser.Open(toWaiter.Body).GetProperty("title").GetString());

        // Answered meanwhile: nobody is buzzed.
        await using (var db = fixture.CreateContext(rig.Factory.Clock))
        {
            var call = await db.ServiceRequests.SingleAsync(r => r.Id == water);
            call.Acknowledge(rig.Factory.Clock.UtcNow, rig.Branch.WaiterId);
            await db.SaveChangesAsync();
        }

        rig.Push.Sent.Clear();
        await rig.DispatchAsync($"staff-push:call:{water}");
        Assert.Empty(rig.Push.Sent);
    }

    /// <summary>
    /// "I'm on it" answers the push: a call claimed before its push goes out buzzes nobody, though it stays open on the
    /// floor's list until it is acknowledged. An unclaimed one beside it still buzzes.
    /// </summary>
    [SkippableFact]
    public async Task A_call_claimed_before_its_push_goes_out_buzzes_nobody_but_stays_open()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var rig = await Rig.StartAsync(fixture);

        var managerBrowser = new Browser();
        await rig.SubscribeAsync(rig.ManagerPhone, managerBrowser);

        var tab = await rig.OpenTabAsync();
        var claimed = await rig.RaiseCallAsync(tab, ServiceRequestPreset.Napkins);
        var unclaimed = await rig.RaiseCallAsync(tab, ServiceRequestPreset.Water);

        await using (var db = fixture.CreateContext(rig.Factory.Clock))
        {
            var call = await db.ServiceRequests.SingleAsync(r => r.Id == claimed);
            Assert.True(call.Claim(rig.Factory.Clock.UtcNow, rig.Branch.WaiterId));
            await db.SaveChangesAsync();
        }

        await rig.DispatchAsync($"staff-push:call:{claimed}");
        Assert.Empty(rig.Push.Sent);

        await using (var db = fixture.CreateContext(rig.Factory.Clock))
        {
            Assert.True((await db.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == claimed)).IsOpen);
        }

        await rig.DispatchAsync($"staff-push:call:{unclaimed}");
        Assert.Single(rig.Push.Sent, p => p.Endpoint == managerBrowser.Endpoint);
    }

    [SkippableFact]
    public async Task A_switched_off_kind_is_not_pushed_and_a_gone_browser_is_deleted()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var rig = await Rig.StartAsync(fixture);
        var managerBrowser = new Browser();

        await rig.SubscribeAsync(rig.ManagerPhone, managerBrowser);
        await rig.ManagerPhone.PutAsJsonAsync("/api/staff/device/notification-preferences", Switches(tableCalls: false));

        var tab = await rig.OpenTabAsync();
        await rig.DispatchAsync($"staff-push:call:{await rig.RaiseCallAsync(tab, ServiceRequestPreset.Napkins)}");
        Assert.Empty(rig.Push.Sent);

        // A push service that has failed: retried later, so the handler throws.
        await rig.ManagerPhone.PutAsJsonAsync("/api/staff/device/notification-preferences", Switches());
        rig.Push.Answer = HttpStatusCode.ServiceUnavailable;
        var napkins = await rig.RaiseCallAsync(tab, ServiceRequestPreset.Other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.DispatchAsync($"staff-push:call:{napkins}"));

        // 410: the browser unsubscribed. The subscription goes, and nothing is retried.
        rig.Push.Answer = HttpStatusCode.Gone;
        await rig.DispatchAsync($"staff-push:call:{napkins}");

        Assert.False((await rig.GetSettingsAsync(rig.ManagerPhone)).GetProperty("subscribed").GetBoolean());
    }

    [SkippableFact]
    public async Task One_device_that_cannot_be_pushed_to_does_not_stop_the_others_or_get_them_buzzed_twice()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var rig = await Rig.StartAsync(fixture);
        var managerBrowser = new Browser();
        var tabletBrowser = new Browser();

        await rig.SubscribeAsync(rig.ManagerPhone, managerBrowser);
        await rig.SubscribeAsync(rig.Tablet, tabletBrowser);

        // The tablet's stored key no longer decodes to a point - encrypting to it throws.
        await using (var db = fixture.CreateContext(rig.Factory.Clock))
        {
            await db.StaffPushSubscriptions
                .Where(s => s.Endpoint == tabletBrowser.Endpoint)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.P256dh, "AAAA"));
        }

        var tab = await rig.OpenTabAsync();
        var call = await rig.RaiseCallAsync(tab, ServiceRequestPreset.TheBill);

        // Delivered to the manager, so the message is done: no exception, and so no retry to buzz the manager again.
        await rig.DispatchAsync($"staff-push:call:{call}");

        var only = Assert.Single(rig.Push.Sent);
        Assert.Equal(managerBrowser.Endpoint, only.Endpoint);
    }

    [SkippableFact]
    public async Task A_revoked_phone_is_pushed_nothing_and_its_subscription_is_gone()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var rig = await Rig.StartAsync(fixture);
        var managerBrowser = new Browser();
        var tabletBrowser = new Browser();

        await rig.SubscribeAsync(rig.ManagerPhone, managerBrowser);
        await rig.SubscribeAsync(rig.Tablet, tabletBrowser);

        using (var console = rig.Factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(rig.Factory, rig.Branch)))
        {
            var phones = await (await console.GetAsync($"/api/branches/{rig.Branch.BranchId}/devices?kind=2"))
                .Content.ReadFromJsonAsync<JsonElement>();
            var managersPhone = phones.EnumerateArray()
                .Single(d => d.GetProperty("boundStaffMember").GetProperty("id").GetGuid() == rig.Branch.ManagerId)
                .GetProperty("id").GetGuid();

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await console.PostAsync($"/api/branches/{rig.Branch.BranchId}/devices/{managersPhone}/revoke", null)).StatusCode);

            await using var db = fixture.CreateContext(rig.Factory.Clock);
            Assert.False(await db.StaffPushSubscriptions.AnyAsync(s => s.StaffDeviceId == managersPhone));
        }

        var tab = await rig.OpenTabAsync();
        await rig.DispatchAsync($"staff-push:call:{await rig.RaiseCallAsync(tab, ServiceRequestPreset.Water)}");

        Assert.Equal(tabletBrowser.Endpoint, Assert.Single(rig.Push.Sent).Endpoint);
    }

    [SkippableFact]
    public async Task An_order_up_goes_to_the_tables_own_waiter_and_not_to_every_phone()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var rig = await Rig.StartAsync(fixture);
        var waiterBrowser = new Browser();
        var managerBrowser = new Browser();

        await rig.SubscribeAsync(rig.WaiterPhone, waiterBrowser);
        await rig.SubscribeAsync(rig.ManagerPhone, managerBrowser);
        await rig.WaiterPhone.PutAsJsonAsync("/api/staff/device/notification-preferences", Switches(kitchenReady: true, onlyDuringShift: false));
        await rig.ManagerPhone.PutAsJsonAsync("/api/staff/device/notification-preferences", Switches(kitchenReady: true));

        // The waiter seated the table (CreateOpenTabAsync), so the order is theirs.
        var tab = await rig.OpenTabAsync();
        Guid orderId;

        await using (var db = fixture.CreateContext(rig.Factory.Clock))
        {
            var order = TabOrder.PlacedByStaffMember(tab.TabId, rig.Branch.ManagerId, rig.Factory.Clock.UtcNow);
            db.TabOrders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        // Moved along the rail by the manager, through the service: the push hangs on the order's TabOrderMoved event,
        // whose handlers a bare context does not have (docs/domain-events.md).
        await using (var db = fixture.CreateContext(rig.Factory.Clock))
        {
            var orders = fixture.CreateOrderService(db, rig.Factory.Clock, TestActor.Manager(rig.Branch.ManagerId));

            await orders.MoveOrderStatusAsync(orderId, TabOrderStatus.InKitchen);
            await orders.MoveOrderStatusAsync(orderId, TabOrderStatus.Ready);
        }

        await rig.DispatchAsync($"staff-push:ready:{orderId}:");

        var push = Assert.Single(rig.Push.Sent);
        Assert.Equal(waiterBrowser.Endpoint, push.Endpoint);
        Assert.Equal($"/staff/floor?table={rig.Branch.FirstTableId}", waiterBrowser.Open(push.Body).GetProperty("url").GetString());
    }

    [SkippableFact]
    public async Task The_save_that_causes_an_event_writes_its_staff_push_and_nothing_else_does()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var rig = await Rig.StartAsync(fixture);
        var now = rig.Factory.Clock.UtcNow;

        await using var db = fixture.CreateContext(rig.Factory.Clock);
        var policy = await db.Branches.AsNoTracking().Where(b => b.Id == rig.Branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();

        // The booking's date and time on the branch's own wall clock, as the API writes them - not the UTC
        // ones, which name another day for part of every night.
        var zone = BranchZone.For("Asia/Yerevan");

        Reservation Booking(ReservationStatus status, int hoursAhead) => Reservation.Create(
            rig.Branch.BranchId,
            rig.Branch.FirstTableId,
            now.AddHours(hoursAhead),
            DateOnly.FromDateTime(zone.ToLocal(now.AddHours(hoursAhead))),
            TimeOnly.FromDateTime(zone.ToLocal(now.AddHours(hoursAhead))),
            2,
            "Lilit M.",
            "+37411000000",
            Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            policy,
            initialStatus: status);

        var pending = Booking(ReservationStatus.PendingApproval, 30);
        var confirmed = Booking(ReservationStatus.Confirmed, 60);
        db.Reservations.AddRange(pending, confirmed);
        await db.SaveChangesAsync();

        var thread = Conversation.ForReservation(rig.Branch.BranchId, pending.Id, null, now);
        db.Conversations.Add(thread);
        var fromGuest = thread.WriteAsGuest("Can we sit by the window?", null, now);
        var fromVenue = thread.WriteAsVenue("Of course", rig.Branch.ManagerId, "Manager", null, now);
        await db.SaveChangesAsync();

        var keys = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == OutboxMessageTypes.StaffPush)
            .Where(m => m.IdempotencyKey.Contains(pending.Id.ToString())
                        || m.IdempotencyKey.Contains(confirmed.Id.ToString())
                        || m.IdempotencyKey.Contains(fromGuest.Id.ToString())
                        || m.IdempotencyKey.Contains(fromVenue.Id.ToString()))
            .Select(m => m.IdempotencyKey)
            .ToListAsync();

        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, k => k.StartsWith($"staff-push:approval:{pending.Id}:", StringComparison.Ordinal));
        Assert.Contains($"staff-push:message:{fromGuest.Id}", keys);

        // And the approval push reads the booking as it now stands.
        await rig.DispatchAsync($"staff-push:approval:{pending.Id}:");
    }

    // ------------------------------------------------------------------ the rig

    private static object Switches(
        bool approvals = true,
        bool guestMessages = true,
        bool tableCalls = true,
        bool takeaway = true,
        bool kitchenReady = false,
        bool onlyDuringShift = false,
        string? locale = null) =>
        new { approvals, guestMessages, tableCalls, takeaway, kitchenReady, onlyDuringShift, locale };

    /// <summary>A browser's push subscription: a P-256 key pair and an auth secret, and the endpoint it was given.</summary>
    /// <summary>A real browser's subscription on <paramref name="host"/>: an endpoint and a usable key pair.</summary>
    internal static (string Endpoint, string P256dh, string Auth) NewBrowserSubscription(string host)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;

        return (
            $"https://{host}/w/{Guid.NewGuid():N}",
            Yalla.Infrastructure.Notifications.Base64Url.Encode([0x04, .. q.X!, .. q.Y!]),
            Yalla.Infrastructure.Notifications.Base64Url.Encode(RandomNumberGenerator.GetBytes(16)));
    }

    private sealed class Browser
    {
        private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _auth = RandomNumberGenerator.GetBytes(16);

        public string Endpoint { get; } = $"https://push.test/w/{Guid.NewGuid():N}";

        public string P256dh
        {
            get
            {
                var q = _key.ExportParameters(false).Q;
                return Yalla.Infrastructure.Notifications.Base64Url.Encode([0x04, .. q.X!, .. q.Y!]);
            }
        }

        public string Auth => Yalla.Infrastructure.Notifications.Base64Url.Encode(_auth);

        public JsonElement Open(byte[] body) =>
            JsonDocument.Parse(WebPushCryptoTests.Decrypt(body, _key, _auth)).RootElement.Clone();
    }

    private sealed record SentPush(string Endpoint, byte[] Body, Dictionary<string, string> Headers);

    /// <summary>A push service: records every push, answers what it is told to.</summary>
    private sealed class PushService : HttpMessageHandler
    {
        public List<SentPush> Sent { get; } = [];

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));

            foreach (var h in request.Content!.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }

            lock (Sent)
            {
                Sent.Add(new SentPush(request.RequestUri!.ToString(), request.Content.ReadAsByteArrayAsync(cancellationToken).Result, headers));
            }

            await Task.Yield();

            return new HttpResponseMessage(Answer);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqlServerFixture _fixture;

        private Rig(SqlServerFixture fixture, YallaApiFactory factory, PushService push)
        {
            _fixture = fixture;
            Factory = factory;
            Push = push;
        }

        public YallaApiFactory Factory { get; }

        public PushService Push { get; }

        public AuthBranch Branch { get; private set; } = null!;

        public string WaiterDeviceToken { get; private set; } = null!;

        public HttpClient WaiterPhone { get; private set; } = null!;

        public HttpClient ManagerPhone { get; private set; } = null!;

        public HttpClient Tablet { get; private set; } = null!;

        public static async Task<Rig> StartAsync(SqlServerFixture fixture)
        {
            var push = new PushService();
            var factory = new YallaApiFactory()
                .WithDatabase(fixture.ConnectionString)

                // The test drives the handler itself; a background dispatcher would race it to the stub.
                .With("Outbox:Enabled", "false")
                .With("WebPush:AllowedEndpointHosts:0", "push.test")
                .WithServices(services => services
                    .AddHttpClient(StaffPushSender.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => push));

            var rig = new Rig(fixture, factory, push);

            await using (var db = fixture.CreateContext(factory.Clock))
            {
                rig.Branch = await AuthTestData.CreateBranchAsync(db);
            }

            var (waiterDevice, waiterSession) = await StaffPhoneTests.PairWaiterAsync(factory, rig.Branch);
            rig.WaiterDeviceToken = waiterDevice;
            rig.WaiterPhone = factory.CreateClientWithToken(waiterSession);

            using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, rig.Branch));
            using var anonymous = factory.CreateClient();
            var paired = await StaffPhoneTests.PairAsync(
                anonymous, await StaffPhoneTests.IssueAsync(manager, rig.Branch, rig.Branch.ManagerId), "9182");
            paired.EnsureSuccessStatusCode();
            rig.ManagerPhone = factory.CreateClientWithToken(
                (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session").GetProperty("accessToken").GetString()!);

            rig.Tablet = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, rig.Branch));

            return rig;
        }

        public async Task SubscribeAsync(HttpClient device, Browser browser)
        {
            var saved = await device.PutAsJsonAsync(
                "/api/staff/device/push-subscription",
                new { endpoint = browser.Endpoint, keys = new { p256dh = browser.P256dh, auth = browser.Auth } });

            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        }

        public async Task<JsonElement> GetSettingsAsync(HttpClient device) =>
            await (await device.GetAsync("/api/staff/device/notification-preferences")).Content.ReadFromJsonAsync<JsonElement>();

        public async Task<AuthTab> OpenTabAsync()
        {
            await using var db = _fixture.CreateContext(Factory.Clock);

            return await AuthTestData.CreateOpenTabAsync(db, Branch, Branch.FirstTableId, Factory.Clock.UtcNow);
        }

        public async Task<Guid> RaiseCallAsync(AuthTab tab, ServiceRequestPreset preset)
        {
            await using var db = _fixture.CreateContext(Factory.Clock);

            var call = new ServiceRequest(tab.TabId, Branch.BranchId, tab.TableId, tab.HostParticipantId, preset, Factory.Clock.UtcNow);
            db.ServiceRequests.Add(call);
            await db.SaveChangesAsync();

            return call.Id;
        }

        public async Task PutWaiterOnShiftAsync()
        {
            await using var db = _fixture.CreateContext(Factory.Clock);

            var zone = BranchZone.For("Asia/Yerevan");
            var started = zone.ToLocal(Factory.Clock.UtcNow).AddHours(-1);
            var start = new TimeOnly(started.Hour, started.Minute);

            db.StaffShifts.Add(new StaffShift(Branch.WaiterId, Branch.BranchId, DateOnly.FromDateTime(started), start, start.AddHours(4)));
            await db.SaveChangesAsync();
        }

        /// <summary>Runs the staff push handler on the message whose key starts with <paramref name="keyPrefix"/>.</summary>
        public async Task DispatchAsync(string keyPrefix)
        {
            string payload;

            await using (var db = _fixture.CreateContext(Factory.Clock))
            {
                payload = await db.OutboxMessages.AsNoTracking()
                    .Where(m => m.Type == OutboxMessageTypes.StaffPush && m.IdempotencyKey.StartsWith(keyPrefix))
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .Select(m => m.PayloadJson)
                    .FirstAsync();
            }

            using var scope = Factory.Services.CreateScope();
            var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().Single(h => h.MessageType == OutboxMessageTypes.StaffPush);

            await handler.HandleAsync(payload, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            // Nothing of this test's is left for another host's dispatcher to push at a host that does not exist.
            await using (var db = _fixture.CreateContext(Factory.Clock))
            {
                await db.StaffPushSubscriptions
                    .Where(s => s.StaffDevice.BranchId == Branch.BranchId)
                    .ExecuteDeleteAsync();
            }

            WaiterPhone.Dispose();
            ManagerPhone.Dispose();
            Tablet.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
