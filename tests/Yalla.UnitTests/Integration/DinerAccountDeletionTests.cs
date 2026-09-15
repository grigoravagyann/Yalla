using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Yalla.Domain.Identity;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>DELETE /api/diner/me</c>: the person goes, the venue's records stay, and nothing identifying
/// is left on either.
/// </summary>
/// <remarks>
/// Through the real pipeline, because most of what is being proved is that the pieces agree: the
/// token check refuses the old token, the refresh endpoint refuses the old handle, the public review
/// list recomputes, the photo route answers 404, and a number, a username and an email that belonged
/// to a deleted account can make a new one.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DinerAccountDeletionTests(SqlServerFixture fixture) : IDisposable
{
    private const string Password = "khachapuri-2026";

    /// <summary>Numbers from the +374 91 000 xxx test range, never a real subscriber, unique per run.</summary>
    private static int _nextPhone = Random.Shared.Next(200, 700);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-diner-delete-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Deleting_an_account_removes_the_person_and_keeps_the_venues_records_without_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        AuthBranch branch;
        TestMenu menu;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        }

        // An account with everything on it: registered, proved from its own app, a review, a
        // picture, a booking, a place at a table with an order on it, and a phone for pushes.
        var phone = NextPhone();
        var username = $"ani_{Suffix()}";
        var email = $"ani-{Suffix()}@example.test";

        var registered = await RegisterAsync(anonymous, username, email, phone);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var account = await registered.Content.ReadFromJsonAsync<JsonElement>();
        var dinerUserId = account.GetProperty("dinerUserId").GetGuid();
        var refreshToken = account.GetProperty("refreshToken").GetString()!;

        using var diner = factory.CreateClientWithToken(account.GetProperty("accessToken").GetString()!);
        await VerifyNumberAsync(diner, phone);

        var uploaded = await UploadAsync(diner, Png(7));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var photoCardUrl = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cardUrl").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(photoCardUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await BookAsync(diner, factory, branch)).StatusCode);

        Guid reservationId;
        Guid participantId;
        Guid orderId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            reservationId = (await db.Reservations.SingleAsync(r => r.DinerUserId == dinerUserId)).Id;

            var tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.TableIds[1], now);
            var participant = TabParticipant.Guest(tab.TabId, "Ani", $"device-{Suffix()}", now, false, dinerUserId);
            participant.Approve(now);
            db.TabParticipants.Add(participant);

            var order = TabOrder.PlacedByDiner(tab.TabId, participant.Id, now);
            order.AddLine(menu.Coffee, "Flat white", TestMenu.CoffeeAmd, 1);
            db.TabOrders.Add(order);

            db.DinerDevices.Add(new DinerDevice(dinerUserId, $"ExponentPushToken[{Suffix()}]", DevicePlatform.Android, "en", now));

            await db.SaveChangesAsync();
            participantId = participant.Id;
            orderId = order.Id;
        }

        // The place at the table is the visit a first review needs (K8).
        var reviewed = await diner.PostAsJsonAsync(
            $"/api/diner/branches/{branch.BranchId}/review", new { rating = 4, text = "Warm lavash." });
        Assert.Equal(HttpStatusCode.Created, reviewed.StatusCode);

        var before = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branch.BranchId}/reviews?page=1");
        Assert.Equal(1, before.GetProperty("reviewCount").GetInt32());

        // A favourite (K11) and an entry in the feed (K12) - the booking above also wrote its reminder.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await diner.PutAsync($"/api/diner/favorites/{branch.BranchId}", content: null)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            db.DinerNotifications.Add(new DinerNotification(
                dinerUserId, DinerNotificationKinds.OrderReady, "{\"tableLabel\":\"2\"}", factory.Clock.UtcNow, branch.BranchId));
            await db.SaveChangesAsync();
        }

        // Delete.
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAccountAsync(diner, new { password = Password })).StatusCode);

        // Every session is over: the access token, the refresh handle, the password.
        await DinerAccountTests.AssertSessionRevokedAsync(await diner.GetAsync("/api/diner/me"));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/diner/refresh", new { refreshToken })).StatusCode);
        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync("/api/auth/diner/login", new { identifier = username, password = Password }),
            HttpStatusCode.Unauthorized,
            "invalid-credentials");

        // The review is gone from the public list and its numbers, and the picture's link is dead.
        var after = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branch.BranchId}/reviews?page=1");
        Assert.Equal(0, after.GetProperty("reviewCount").GetInt32());
        Assert.Equal(0, after.GetProperty("reviews").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(photoCardUrl)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var tombstone = await db.DinerUsers.AsNoTracking().SingleAsync(d => d.Id == dinerUserId);

            Assert.NotNull(tombstone.DeletedAtUtc);
            Assert.False(tombstone.IsActive);
            Assert.Null(tombstone.PhoneE164);
            Assert.Null(tombstone.Username);
            Assert.Null(tombstone.Email);
            Assert.Null(tombstone.DisplayName);
            Assert.Null(tombstone.PasswordHash);
            Assert.Null(tombstone.PhoneVerifiedAtUtc);
            Assert.Null(tombstone.PhotoId);

            Assert.False(await db.BranchReviews.AnyAsync(r => r.DinerUserId == dinerUserId));
            Assert.False(await db.DinerFavorites.AnyAsync(f => f.DinerUserId == dinerUserId));
            Assert.False(await db.DinerNotifications.AnyAsync(n => n.DinerUserId == dinerUserId));
            Assert.False(await db.Photos.AnyAsync(p => p.DinerUserId == dinerUserId));
            Assert.False(await db.DinerDevices.AnyAsync(d => d.DinerUserId == dinerUserId));
            Assert.Equal(
                0,
                await db.RefreshTokens.CountAsync(t => t.SubjectId == dinerUserId && t.RevokedAtUtc == null));

            // The venue's records stay, without the person.
            Assert.True(await db.TabOrders.AnyAsync(o => o.Id == orderId), "The order was deleted with the account.");

            var place = await db.TabParticipants.AsNoTracking().SingleAsync(p => p.Id == participantId);
            Assert.Null(place.UserId);
            Assert.Equal(DinerAccountDeletion.GuestName, place.DisplayName);

            var booking = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservationId);
            Assert.Null(booking.DinerUserId);
            Assert.Equal("Ani Test", booking.GuestName);

            Assert.Equal(
                1,
                await db.PlatformAuditLogs.CountAsync(l => l.Action == DinerAccountDeletion.AuditAction && l.TargetId == dinerUserId));
        }

        // The number, the username and the email were the person's, and make a new account.
        var again = await RegisterAsync(anonymous, username, email, phone);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.NotEqual(dinerUserId, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dinerUserId").GetGuid());
    }

    [SkippableFact]
    public async Task A_code_only_account_deletes_with_a_code_sent_to_its_number()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var phone = NextPhone();
        var (dinerUserId, token) = await SignInByCodeAsync(anonymous, phone, expectNewAccount: true);
        using var diner = factory.CreateClientWithToken(token);

        // No password on the account, so the code is what it asks for - by name.
        await AssertProblemAsync(
            await DeleteAccountAsync(diner, new { }), HttpStatusCode.UnprocessableEntity, "validation-failed", field: "code");

        var code = await RequestCodeAsync(anonymous, phone);

        await AssertProblemAsync(
            await DeleteAccountAsync(diner, new { code = code == "000000" ? "111111" : "000000" }),
            HttpStatusCode.Unauthorized,
            "invalid-credentials");

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Null((await db.DinerUsers.AsNoTracking().SingleAsync(d => d.Id == dinerUserId)).DeletedAtUtc);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAccountAsync(diner, new { code })).StatusCode);

        // The codes sent to the number went with the account - the one that proved the deletion
        // included. Keyed by the number, they are not reached by clearing the account's column.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.PhoneVerificationCodes.AnyAsync(c => c.PhoneE164 == phone));
        }

        // The number signs in again as somebody new.
        var (newDinerUserId, _) = await SignInByCodeAsync(anonymous, phone, expectNewAccount: true);
        Assert.NotEqual(dinerUserId, newDinerUserId);
    }

    [SkippableFact]
    public async Task A_password_account_must_prove_it_and_ten_wrong_tries_spend_the_budget()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var username = $"ani_{Suffix()}";
        var registered = await RegisterAsync(anonymous, username, $"ani-{Suffix()}@example.test", NextPhone());
        var account = await registered.Content.ReadFromJsonAsync<JsonElement>();
        var dinerUserId = account.GetProperty("dinerUserId").GetGuid();
        using var diner = factory.CreateClientWithToken(account.GetProperty("accessToken").GetString()!);

        // A code does not stand in for the password the account has.
        await AssertProblemAsync(
            await DeleteAccountAsync(diner, new { code = "123456" }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed",
            field: "password");

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await AssertProblemAsync(
                await DeleteAccountAsync(diner, new { password = "not-the-password" }),
                HttpStatusCode.Unauthorized,
                "invalid-credentials");
        }

        // The eleventh is refused before the password is looked at, right or wrong.
        await AssertProblemAsync(
            await DeleteAccountAsync(diner, new { password = Password }),
            HttpStatusCode.TooManyRequests,
            "too-many-attempts");

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Null((await db.DinerUsers.AsNoTracking().SingleAsync(d => d.Id == dinerUserId)).DeletedAtUtc);
        }

        // Nothing was deleted, and the account still signs in.
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/api/auth/diner/login", new { identifier = username, password = Password })).StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root);

    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    private static string NextPhone() => $"+37491000{Interlocked.Increment(ref _nextPhone) % 1000:000}";

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string username, string email, string phone) =>
        client.PostAsJsonAsync(
            "/api/auth/diner/register",
            new { username, email, password = Password, phoneE164 = phone, displayName = "Ani" });

    /// <summary>The body travels on a DELETE, which <c>HttpClient.DeleteAsync</c> cannot send.</summary>
    private static Task<HttpResponseMessage> DeleteAccountAsync(HttpClient client, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/diner/me") { Content = JsonContent.Create(body) });

    private static async Task<string> RequestCodeAsync(HttpClient client, string phone)
    {
        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        return (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString()!;
    }

    /// <summary>Proves the number from the account's own app, so the password and sessions stay.</summary>
    private static async Task VerifyNumberAsync(HttpClient holder, string phone)
    {
        var code = await RequestCodeAsync(holder, phone);
        var verified = await holder.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
    }

    private static async Task<(Guid DinerUserId, string AccessToken)> SignInByCodeAsync(
        HttpClient client, string phone, bool expectNewAccount)
    {
        var code = await RequestCodeAsync(client, phone);
        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectNewAccount, body.GetProperty("isNewAccount").GetBoolean());

        return (body.GetProperty("dinerUserId").GetGuid(), body.GetProperty("accessToken").GetString()!);
    }

    /// <summary>Books the branch's first table two days out, through the real endpoint.</summary>
    private static Task<HttpResponseMessage> BookAsync(HttpClient diner, YallaApiFactory factory, AuthBranch branch)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        return diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId = branch.TableIds[0],
                date = DateOnly.FromDateTime(localNow).AddDays(2).ToString("yyyy-MM-dd"),
                time = "18:00",
                partySize = 2,
                guestName = "Ani Test",
                guestPhone = "+37491000999",
                clientCommandId = Guid.CreateVersion7(),
            });
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "me.png");

        return await client.PostAsync("/api/diner/me/photo", form);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        Assert.Equal(status, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());

        if (field is not null)
        {
            Assert.Equal(field, problem.GetProperty("context").GetProperty("field").GetString());
        }
    }

    private static byte[] Png(byte seed)
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(seed, (byte)(255 - seed), 64));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
