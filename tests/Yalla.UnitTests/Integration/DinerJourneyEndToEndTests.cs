using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// One diner's whole evening against one venue, over HTTP: the venue fills in its page, the diner finds
/// it, keeps it, books it, is told the booking is confirmed, sits down, orders, watches the order through
/// the kitchen, reviews the place, has that review reported by somebody else, and sets a profile photo.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this area proves one rule with its preconditions written straight to the tables.
/// This one proves the rules agree with each other: that what the console writes is what the app reads,
/// that the booking a diner makes is the one their "I'm at my table" opens, and that the account on the
/// booking is the account the orders, the review and the favourite belong to.
/// </para>
/// <para>
/// <b>Two direct database writes, and only two.</b> The venue, its staff and its menu are created in the
/// database, because there is no public way to create them that is not a test of its own. And the
/// booking's start is moved to ten minutes from now once it is confirmed, exactly as
/// <see cref="OpenTabByBookingTests"/> does, for the reason given there: tokens are stamped with the API's
/// clock and validated against the real one, so moving the API's clock forward two days would mint tokens
/// that are not valid yet, and a booking cannot be made for ten minutes from now without the branch's
/// opening hours and lead time permitting it at whatever time the suite happens to run.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DinerJourneyEndToEndTests(SqlServerFixture fixture) : IDisposable
{
    private const string BookingNote = "Window seat, a birthday.";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-diner-journey-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_diner_finds_keeps_books_sits_orders_reviews_and_is_reported_all_over_http()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root);

        // ------------------------------------------------------------ setup: the venue, its staff, a menu

        AuthBranch branch;
        TestMenu menu;
        PanelAccount owner;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        }

        using var anyone = factory.CreateClient();
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));

        // ------------------------------------------------------------ 1. the venue fills in its page

        var cover = await UploadBranchPhotoAsync(manager, branch.BranchId, Png(20));
        var galleryFirst = await UploadBranchPhotoAsync(manager, branch.BranchId, Png(110));
        var gallerySecond = await UploadBranchPhotoAsync(manager, branch.BranchId, Png(200));

        await ExpectAsync(
            HttpStatusCode.OK,
            manager.PutAsJsonAsync(
                $"/api/branches/{branch.BranchId}/public-profile",
                new { phoneE164 = (string?)null, acceptsWebBookings = true, coverPhotoId = cover }));

        // Saving the policy is what marks it reviewed, and with online bookings on that is what lets the
        // app book here (K9). Bookings wait for the venue's say-so, so the diner has something to be told.
        var policy = await ReadAsync(manager, $"/api/branches/{branch.BranchId}/reservation-policy");

        await ExpectAsync(
            HttpStatusCode.OK,
            manager.PutAsJsonAsync(
                $"/api/branches/{branch.BranchId}/reservation-policy",
                new
                {
                    turnTimeMinutes = policy.GetProperty("turnTimeMinutes").GetInt32(),
                    bufferMinutes = policy.GetProperty("bufferMinutes").GetInt32(),
                    graceMinutes = policy.GetProperty("graceMinutes").GetInt32(),
                    lateNudgeAfterMinutes = policy.GetProperty("lateNudgeAfterMinutes").GetInt32(),
                    graceExtensionMinutes = policy.GetProperty("graceExtensionMinutes").GetInt32(),
                    minLeadMinutes = policy.GetProperty("minLeadMinutes").GetInt32(),
                    bookingWindowDays = policy.GetProperty("bookingWindowDays").GetInt32(),
                    cancellationDeadlineMinutes = policy.GetProperty("cancellationDeadlineMinutes").GetInt32(),
                    autoConfirm = false,
                    serviceChargePercent = policy.GetProperty("serviceChargePercent").GetDecimal(),
                    pricesIncludeVat = policy.GetProperty("pricesIncludeVat").GetBoolean(),
                    maxSeatOverhang = OptionalInt(policy, "maxSeatOverhang"),
                    approvalRequiredAbovePartySize = OptionalInt(policy, "approvalRequiredAbovePartySize"),
                    walkInHoldbackMinutes = 30,
                }));

        var cuisine = $"Journey grill {Guid.NewGuid():N}";
        const string about = "Charcoal, lavash and one long table.";

        await ExpectAsync(
            HttpStatusCode.OK,
            manager.PutAsJsonAsync(
                $"/api/branches/{branch.BranchId}/listing",
                new
                {
                    cuisine,
                    about,
                    priceLevel = 2,
                    amenities = new[] { "wifi", "outdoorSeating" },
                    galleryPhotoIds = new[] { galleryFirst, gallerySecond },
                }));

        // Moving the pin is the owner's call (K5): the same form, with the location.
        await ExpectAsync(
            HttpStatusCode.OK,
            ownerClient.PutAsJsonAsync(
                $"/api/branches/{branch.BranchId}/listing",
                new
                {
                    cuisine,
                    about,
                    priceLevel = 2,
                    amenities = new[] { "wifi", "outdoorSeating" },
                    galleryPhotoIds = new[] { galleryFirst, gallerySecond },
                    address = "7 Test Street, Yerevan",
                    latitude = 40.1811,
                    longitude = 44.5136,
                }));

        await ExpectAsync(
            HttpStatusCode.OK,
            manager.PutAsJsonAsync(
                $"/api/branches/{branch.BranchId}/table-photo-positions",
                new
                {
                    coverPhotoId = cover,
                    positions = new[]
                    {
                        new { tableId = branch.TableIds[0], photoX = 0.3, photoY = 0.6 },
                        new { tableId = branch.TableIds[1], photoX = 0.7, photoY = 0.4 },
                    },
                }));

        // ------------------------------------------------------------ 2. anybody finds it

        var card = await CardAsync(anyone, branch.BranchId);
        Assert.Equal(cuisine, card.GetProperty("cuisine").GetString());
        Assert.Equal(40.1811, card.GetProperty("latitude").GetDouble());
        Assert.Equal(cover, card.GetProperty("coverPhoto").GetProperty("photoId").GetGuid());
        Assert.Equal(0, card.GetProperty("reviewCount").GetInt32());

        var detail = await ReadAsync(anyone, $"/api/public/branches/{branch.BranchId}");
        Assert.Equal(about, detail.GetProperty("about").GetString());
        Assert.True(detail.GetProperty("acceptsAppBookings").GetBoolean(), "the branch was set up to take app bookings");
        Assert.Equal(
            new[] { "outdoorSeating", "wifi" },
            detail.GetProperty("amenities").EnumerateArray().Select(a => a.GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(
            [galleryFirst, gallerySecond],
            detail.GetProperty("gallery").EnumerateArray().Select(p => p.GetProperty("photoId").GetGuid()));

        var markers = detail.GetProperty("tableMarkers").EnumerateArray().ToList();
        var firstMarker = Assert.Single(markers, m => m.GetProperty("tableId").GetGuid() == branch.TableIds[0]);
        Assert.Equal(0.3, firstMarker.GetProperty("photoX").GetDouble());
        Assert.Equal(2, markers.Count);

        // Every picture the app draws is really there, in every size it asks for.
        foreach (var photo in detail.GetProperty("gallery").EnumerateArray().Prepend(card.GetProperty("coverPhoto")))
        {
            foreach (var variant in new[] { "thumbnailUrl", "cardUrl", "fullUrl" })
            {
                await AssertImageAsync(anyone, photo.GetProperty(variant).GetString()!);
            }
        }

        // ------------------------------------------------------------ 3. the diner keeps it and books it

        var phone = ReviewTestData.NextPhone();
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var account = await JsonAsync(await ExpectAsync(
            HttpStatusCode.Created,
            anyone.PostAsJsonAsync(
                "/api/auth/diner/register",
                new
                {
                    username = $"ani_{suffix}",
                    email = $"ani-{suffix}@example.test",
                    password = DinerFeedTestData.Password,
                    phoneE164 = phone,
                    displayName = "Ani Petrosyan",
                })));

        using var diner = factory.CreateClientWithToken(account.GetProperty("accessToken").GetString()!);

        // A heart, kept on the account (K11). A proved number is not needed for that.
        await ExpectAsync(HttpStatusCode.NoContent, diner.PutAsync($"/api/diner/favorites/{branch.BranchId}", content: null));

        var favourites = await ReadAsync(diner, "/api/diner/favorites");
        var kept = Assert.Single(favourites.GetProperty("items").EnumerateArray());
        Assert.Equal(branch.BranchId, kept.GetProperty("branchId").GetGuid());
        Assert.Equal(cuisine, kept.GetProperty("listing").GetProperty("cuisine").GetString());

        Task<HttpResponseMessage> BookAsync() =>
            diner.PostAsJsonAsync(
                "/api/reservations",
                new
                {
                    branchId = branch.BranchId,
                    tableId = branch.TableIds[0],
                    date = BookingDate(factory).ToString("yyyy-MM-dd"),
                    time = "18:00",
                    partySize = 2,
                    guestName = "Ani Petrosyan",
                    guestPhone = phone,
                    clientCommandId = Guid.CreateVersion7(),
                    channel = ReservationChannel.App,
                    note = BookingNote,
                });

        // A number nobody has proved books nothing.
        await ReviewIntegrityTests.AssertProblemAsync(await BookAsync(), HttpStatusCode.Forbidden, "phone-not-verified");

        var code = await RequestCodeAsync(anyone, phone);
        await ExpectAsync(HttpStatusCode.OK, diner.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code }));

        var booked = await JsonAsync(await ExpectAsync(HttpStatusCode.Created, BookAsync()));
        var bookingId = booked.GetProperty("id").GetGuid();
        var bookingCode = booked.GetProperty("code").GetString()!;
        Assert.Equal(ReservationStatus.PendingApproval, EnumOf<ReservationStatus>(booked.GetProperty("status")));
        Assert.Equal(BookingNote, booked.GetProperty("note").GetString());

        // The note reached the venue, which confirms the booking.
        var pending = await ReadAsync(manager, $"/api/branches/{branch.BranchId}/reservations?status={(int)ReservationStatus.PendingApproval}");
        Assert.Equal(
            BookingNote,
            pending.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == bookingId).GetProperty("note").GetString());

        await ExpectAsync(HttpStatusCode.OK, manager.PostAsJsonAsync($"/api/reservations/{bookingId}/approve", new { }));

        // ...and the diner is told, in the feed (K12).
        var feed = await ReadAsync(diner, "/api/diner/notifications");
        var confirmed = Assert.Single(
            feed.GetProperty("items").EnumerateArray(),
            n => n.GetProperty("kind").GetString() == "booking-confirmed");
        Assert.Equal(bookingId, confirmed.GetProperty("reservationId").GetGuid());
        Assert.Equal(branch.BranchId, confirmed.GetProperty("branchId").GetGuid());
        Assert.False(confirmed.GetProperty("read").GetBoolean());
        Assert.True(feed.GetProperty("unreadCount").GetInt32() >= 1);

        await ExpectAsync(
            HttpStatusCode.NoContent,
            diner.PostAsJsonAsync(
                "/api/diner/notifications/read", new { upTo = confirmed.GetProperty("notificationId").GetGuid() }));
        Assert.Equal(0, (await ReadAsync(diner, "/api/diner/notifications")).GetProperty("unreadCount").GetInt32());

        // ------------------------------------------------------------ 4. dinner time: the diner sits down and orders

        var mine = await ReadAsync(diner, "/api/reservations/mine");
        var upcoming = Assert.Single(mine.GetProperty("upcoming").EnumerateArray(), r => r.GetProperty("id").GetGuid() == bookingId);
        Assert.Equal(ReservationStatus.Confirmed, EnumOf<ReservationStatus>(upcoming.GetProperty("status")));
        Assert.Equal(BookingNote, upcoming.GetProperty("note").GetString());

        // The one write after setup that is not HTTP - see the class remarks.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var startUtc = factory.Clock.UtcNow.AddMinutes(10);
            var length = await db.Reservations.AsNoTracking()
                .Where(r => r.Id == bookingId)
                .Select(r => r.EndUtc - r.StartUtc)
                .SingleAsync();

            await db.Reservations
                .Where(r => r.Id == bookingId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.StartUtc, startUtc)
                    .SetProperty(r => r.EndUtc, startUtc + length));
        }

        var opened = await JsonAsync(await ExpectAsync(
            HttpStatusCode.OK,
            diner.PostAsJsonAsync(
                "/api/tabs/open-by-booking",
                new { bookingCode, deviceId = "ani-phone", clientCommandId = Guid.CreateVersion7(), displayName = "Ani" })));

        var tabId = opened.GetProperty("tab").GetProperty("tabId").GetGuid();
        using var atTable = factory.CreateClientWithToken(opened.GetProperty("token").GetProperty("accessToken").GetString()!);

        var placed = await JsonAsync(await ExpectAsync(
            HttpStatusCode.Created,
            atTable.PostAsJsonAsync(
                $"/api/tabs/{tabId}/orders",
                new
                {
                    items = new[] { new { menuItemId = menu.Coffee, quantity = 2 } },
                    clientCommandId = Guid.CreateVersion7(),
                })));

        var orderId = placed.GetProperty("orderId").GetGuid();

        // ------------------------------------------------------------ 5. the Orders tab follows the kitchen

        // Ordered from the phone the booking opened: the account's order, through the participant's link to it.
        var active = await ReadAsync(diner, "/api/diner/orders?status=active");
        var order = Assert.Single(active.EnumerateArray());
        Assert.Equal(orderId, order.GetProperty("orderId").GetGuid());
        Assert.Equal("confirmed", order.GetProperty("status").GetString());
        Assert.Equal(branch.BranchId, order.GetProperty("branchId").GetGuid());
        Assert.Equal(2 * TestMenu.CoffeeAmd, order.GetProperty("totalAmd").GetInt64());

        await MoveOrderAsync(manager, orderId, TabOrderStatus.InKitchen);
        Assert.Equal(
            "preparing",
            Assert.Single((await ReadAsync(diner, "/api/diner/orders?status=active")).EnumerateArray()).GetProperty("status").GetString());

        // The kitchen rail has no skipping from preparing to served: Ready is a step of its own.
        await MoveOrderAsync(manager, orderId, TabOrderStatus.Ready);
        await MoveOrderAsync(manager, orderId, TabOrderStatus.Served);

        Assert.Empty((await ReadAsync(diner, "/api/diner/orders?status=active")).EnumerateArray());

        var done = Assert.Single((await ReadAsync(diner, "/api/diner/orders?status=history")).EnumerateArray());
        Assert.Equal(orderId, done.GetProperty("orderId").GetGuid());
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal(
            ["confirmed", "preparing", "ready", "completed"],
            done.GetProperty("timeline").EnumerateArray().Select(t => t.GetProperty("status").GetString()));

        // ------------------------------------------------------------ 6. the review

        // The booking the diner sat down on is the visit a review needs (K8).
        var review = await JsonAsync(await ExpectAsync(
            HttpStatusCode.Created,
            diner.PostAsJsonAsync($"/api/diner/branches/{branch.BranchId}/review", new { rating = 5, text = "Lavash straight off the fire." })));

        var reviewId = review.GetProperty("reviewId").GetGuid();

        var page = await ReadAsync(anyone, $"/api/public/branches/{branch.BranchId}/reviews?page=1");
        Assert.Equal(5.0, page.GetProperty("rating").GetDouble());
        Assert.Equal(1, page.GetProperty("reviewCount").GetInt32());
        var written = Assert.Single(page.GetProperty("reviews").EnumerateArray());
        Assert.Equal(reviewId, written.GetProperty("reviewId").GetGuid());
        Assert.Equal("Ani P.", written.GetProperty("authorName").GetString());

        // The browse list serves its numbers from a fifteen-second cache, filled when the list was read
        // above; within that window it catches up.
        var deadline = DateTime.UtcNow + PublicVenueQuery.LiveFor + TimeSpan.FromSeconds(10);
        JsonElement listed;

        while (true)
        {
            listed = await CardAsync(anyone, branch.BranchId);

            if (listed.GetProperty("reviewCount").GetInt32() == 1 || DateTime.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(1, listed.GetProperty("reviewCount").GetInt32());
        Assert.Equal(5.0, listed.GetProperty("rating").GetDouble());

        // ------------------------------------------------------------ 7. somebody else reports it

        var (reporterToken, _) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var reporter = factory.CreateClientWithToken(reporterToken);

        await ExpectAsync(
            HttpStatusCode.NoContent,
            reporter.PostAsJsonAsync($"/api/diner/reviews/{reviewId}/report", new { reason = "not-a-visit", note = "They were never here." }));

        // Once per diner per review: again is the same 204 and writes nothing.
        await ExpectAsync(
            HttpStatusCode.NoContent,
            reporter.PostAsJsonAsync($"/api/diner/reviews/{reviewId}/report", new { reason = "spam", note = (string?)null }));

        // Nobody reports their own.
        await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PostAsJsonAsync($"/api/diner/reviews/{reviewId}/report", new { reason = "spam", note = (string?)null }),
            HttpStatusCode.Conflict,
            "conflicting-state");

        var reported = await ReadAsync(manager, $"/api/branches/{branch.BranchId}/reviews?filter=reported");
        var flagged = Assert.Single(reported.GetProperty("items").EnumerateArray());
        Assert.Equal(reviewId, flagged.GetProperty("reviewId").GetGuid());
        Assert.Equal(1, flagged.GetProperty("reportCount").GetInt32());
        Assert.False(flagged.GetProperty("hidden").GetBoolean());

        // A report takes nothing down.
        Assert.Equal(
            1,
            (await ReadAsync(anyone, $"/api/public/branches/{branch.BranchId}/reviews?page=1")).GetProperty("reviewCount").GetInt32());

        // ------------------------------------------------------------ 8. a profile photo

        var avatar = await JsonAsync(await ExpectAsync(HttpStatusCode.Created, UploadAvatarAsync(diner, Png(60))));
        await AssertImageAsync(anyone, avatar.GetProperty("thumbnailUrl").GetString()!);

        // The place is still kept, and the card in the favourites list now carries the review.
        var keptAtEnd = Assert.Single((await ReadAsync(diner, "/api/diner/favorites")).GetProperty("items").EnumerateArray());
        Assert.Equal(branch.BranchId, keptAtEnd.GetProperty("branchId").GetGuid());
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A date the default policy accepts: two days out, past the lead time and inside the window.</summary>
    private static DateOnly BookingDate(YallaApiFactory factory)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        return DateOnly.FromDateTime(localNow).AddDays(2);
    }

    private static async Task<JsonElement> CardAsync(HttpClient client, Guid branchId)
    {
        var list = await ReadAsync(client, "/api/public/branches");

        return Assert.Single(list.EnumerateArray(), b => b.GetProperty("branchId").GetGuid() == branchId);
    }

    private static async Task<HttpResponseMessage> ExpectAsync(HttpStatusCode status, Task<HttpResponseMessage> sending)
    {
        var response = await sending;

        if (response.StatusCode != status)
        {
            Assert.Fail(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} answered "
                + $"{(int)response.StatusCode}, not {(int)status}: {await response.Content.ReadAsStringAsync()}");
        }

        return response;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url) =>
        await JsonAsync(await ExpectAsync(HttpStatusCode.OK, client.GetAsync(url)));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertImageAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} answered {(int)response.StatusCode}.");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.True(mediaType.StartsWith("image/", StringComparison.Ordinal), $"GET {url} served '{mediaType}', not an image.");
    }

    private static async Task MoveOrderAsync(HttpClient staff, Guid orderId, TabOrderStatus status) =>
        await ExpectAsync(HttpStatusCode.OK, staff.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status }));

    private static async Task<string> RequestCodeAsync(HttpClient client, string phone)
    {
        var requested = await ExpectAsync(
            HttpStatusCode.OK, client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone }));

        return (await JsonAsync(requested)).GetProperty("developmentCode").GetString()!;
    }

    private static async Task<Guid> UploadBranchPhotoAsync(HttpClient client, Guid branchId, byte[] png)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "place.png");

        var uploaded = await ExpectAsync(HttpStatusCode.Created, client.PostAsync($"/api/branches/{branchId}/photos", form));

        // The branch upload answers the photo wrapped with what it is used for; the diner's answers it bare.
        return (await JsonAsync(uploaded)).GetProperty("photo").GetProperty("photoId").GetGuid();
    }

    private static async Task<HttpResponseMessage> UploadAvatarAsync(HttpClient client, byte[] png)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "me.png");

        return await client.PostAsync("/api/diner/me/photo", form);
    }

    private static int? OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static T EnumOf<T>(JsonElement element) where T : struct, Enum =>
        element.ValueKind == JsonValueKind.Number
            ? (T)Enum.ToObject(typeof(T), element.GetInt32())
            : Enum.Parse<T>(element.GetString()!, ignoreCase: true);

    /// <summary>A small PNG with a colour of its own, so no two uploads are the same file.</summary>
    private static byte[] Png(byte seed)
    {
        using var bitmap = new SKBitmap(64, 48);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(seed, (byte)(255 - seed), (byte)Random.Shared.Next(256)));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
