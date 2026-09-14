using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Yalla.Application.Media;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Identity type 2, the other door: a username or email and a password, and the account behind it.
/// </summary>
/// <remarks>
/// Everything here goes through the real pipeline, because most of what is being proved is about
/// the pipeline: which 409 a taken name gets, that the login form cannot tell a stranger which
/// accounts exist, that a profile route never reaches anybody else's row, and that a code sent to a
/// registered number signs that account in rather than minting a second one.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DinerAccountTests(SqlServerFixture fixture) : IDisposable
{
    private const string Password = "khachapuri-2026";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-diner-photo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------ register

    [SkippableFact]
    public async Task Registering_creates_a_signed_in_account_whose_number_is_not_yet_verified()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        // Mixed case on purpose: what is stored and what is signed in with is the lowercased form.
        var suffix = Suffix();
        var response = await RegisterAsync(
            anonymous, username: $"Ani.K_{suffix}", email: $"Ani.K.{suffix}@Example.TEST", displayName: " Ani ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/diner/me", response.Headers.Location?.ToString());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("refreshToken").GetString()));
        Assert.True(body.GetProperty("isNewAccount").GetBoolean());

        var dinerUserId = body.GetProperty("dinerUserId").GetGuid();

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.DinerUsers.SingleAsync(d => d.Id == dinerUserId);

            Assert.Equal($"ani.k_{suffix}", row.Username);
            Assert.Equal($"ani.k.{suffix}@example.test", row.Email);
            Assert.Equal("Ani", row.DisplayName);
            Assert.NotNull(row.PasswordHash);
            Assert.NotEqual(Password, row.PasswordHash);

            // Typed, not proved. The code flow is the only thing that sets this.
            Assert.Null(row.PhoneVerifiedAtUtc);
            Assert.NotNull(row.LastSignInAtUtc);
        }

        // And the token it handed back reaches the profile, which says the same.
        using var diner = factory.CreateClientWithToken(body.GetProperty("accessToken").GetString()!);
        var me = await diner.GetFromJsonAsync<JsonElement>("/api/diner/me");

        Assert.Equal(dinerUserId, me.GetProperty("dinerUserId").GetGuid());
        Assert.Equal($"ani.k_{suffix}", me.GetProperty("username").GetString());
        Assert.False(me.GetProperty("phoneVerified").GetBoolean());
        Assert.True(me.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("Ani", me.GetProperty("displayName").GetString());
        Assert.False(me.TryGetProperty("photo", out var photo) && photo.ValueKind != JsonValueKind.Null);
    }

    [SkippableFact]
    public async Task A_taken_username_email_or_number_answers_its_own_409_naming_the_field()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var existing = await RegisteredAsync(anonymous);

        // Case-insensitively: the stored form is lowercase and the index is on that.
        var username = await RegisterAsync(anonymous, username: existing.Username.ToUpperInvariant());
        await AssertProblemAsync(username, HttpStatusCode.Conflict, "username-taken", field: "username");

        var email = await RegisterAsync(anonymous, email: existing.Email.ToUpperInvariant());
        await AssertProblemAsync(email, HttpStatusCode.Conflict, "email-taken", field: "email");

        var phone = await RegisterAsync(anonymous, phone: existing.Phone);
        await AssertProblemAsync(phone, HttpStatusCode.Conflict, "phone-in-use", field: "phoneE164");

        // The number of an account the code flow created is just as taken - this is the case the
        // app answers with "log in with a code instead", because that person can prove the number.
        var codeOnlyPhone = NewPhone();
        await SignInByCodeAsync(anonymous, codeOnlyPhone);

        var codeOnly = await RegisterAsync(anonymous, phone: codeOnlyPhone);
        await AssertProblemAsync(codeOnly, HttpStatusCode.Conflict, "phone-in-use", field: "phoneE164");

        // None of the refusals wrote anything.
        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await db.DinerUsers.CountAsync(d => d.PhoneE164 == existing.Phone));
    }

    [SkippableFact]
    public async Task A_malformed_sign_up_answers_400_naming_the_field()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var email = NewEmail();

        var cases = new (Task<HttpResponseMessage> Response, string Field)[]
        {
            (RegisterAsync(anonymous, username: "-bad"), "username"),
            (RegisterAsync(anonymous, username: "ab"), "username"),
            (RegisterAsync(anonymous, email: "not-an-address"), "email"),
            (RegisterAsync(anonymous, password: "short7"), "password"),
            (RegisterAsync(anonymous, email: email, password: email), "password"),
            (RegisterAsync(anonymous, phone: "12345"), "phoneE164"),
            (RegisterAsync(anonymous, displayName: null), "displayName"),
        };

        foreach (var (pending, field) in cases)
        {
            await AssertProblemAsync(await pending, HttpStatusCode.BadRequest, "invalid-request", field);
        }
    }

    // ------------------------------------------------------------ login

    [SkippableFact]
    public async Task Login_accepts_the_username_or_the_email_case_insensitively()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var account = await RegisteredAsync(anonymous);

        foreach (var identifier in new[] { account.Username.ToUpperInvariant(), $" {account.Email.ToUpperInvariant()} " })
        {
            var response = await anonymous.PostAsJsonAsync(
                "/api/auth/diner/login", new { identifier, password = Password, localeCode = "ru" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(account.DinerUserId, body.GetProperty("dinerUserId").GetGuid());
            Assert.False(body.GetProperty("isNewAccount").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        }

        // A recognised locale on the way in moves the stored one.
        using var diner = factory.CreateClientWithToken(account.AccessToken);
        var me = await diner.GetFromJsonAsync<JsonElement>("/api/diner/me");
        Assert.Equal("ru", me.GetProperty("localeCode").GetString());
    }

    /// <summary>
    /// The four ways a password sign-in fails all answer the same thing, so the form cannot be used
    /// to find out which usernames and addresses have accounts.
    /// </summary>
    [SkippableFact]
    public async Task Wrong_password_unknown_identifier_no_password_and_inactive_all_answer_one_401()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var registered = await RegisteredAsync(anonymous);

        // An account the code flow created, given a username but never a password.
        var (_, codeOnlyToken) = await SignInByCodeAsync(anonymous, NewPhone());
        var codeOnlyUsername = NewUsername();

        using (var codeOnly = factory.CreateClientWithToken(codeOnlyToken))
        {
            var named = await codeOnly.PutAsJsonAsync("/api/diner/me", new { username = codeOnlyUsername });
            Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        }

        // A registered account that has since been deactivated.
        var inactive = await RegisteredAsync(anonymous);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            (await db.DinerUsers.SingleAsync(d => d.Id == inactive.DinerUserId)).SetActive(false);
            await db.SaveChangesAsync();
        }

        var attempts = new (string Identifier, string Password)[]
        {
            (registered.Username, "not-the-password"),
            ($"nobody-{Suffix()}", Password),
            (codeOnlyUsername, Password),
            (inactive.Username, Password),
        };

        foreach (var (identifier, password) in attempts)
        {
            var response = await anonymous.PostAsJsonAsync("/api/auth/diner/login", new { identifier, password });

            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "invalid-credentials");
        }
    }

    /// <summary>
    /// A deactivated account proving its number by code is refused at the door with the password
    /// sign-in's answer, rather than handed a token the authority check refuses on first use.
    /// </summary>
    [SkippableFact]
    public async Task A_deactivated_account_verifying_a_code_is_refused_like_a_password_sign_in()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var phone = NewPhone();
        var (dinerUserId, _) = await SignInByCodeAsync(anonymous, phone);

        int refreshTokensBefore;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            (await db.DinerUsers.SingleAsync(d => d.Id == dinerUserId)).SetActive(false);
            await db.SaveChangesAsync();

            refreshTokensBefore = await db.RefreshTokens.CountAsync(t => t.SubjectId == dinerUserId);
        }

        var requested = await anonymous.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();
        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code }),
            HttpStatusCode.Unauthorized,
            "invalid-credentials");

        // The right code was spent by the refusal, so presenting it again gets no further.
        Assert.NotEqual(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code })).StatusCode);

        await using var context = fixture.CreateContext(factory.Clock);

        // Nothing issued, nothing created: the one account, still switched off.
        Assert.Equal(refreshTokensBefore, await context.RefreshTokens.CountAsync(t => t.SubjectId == dinerUserId));
        var account = await context.DinerUsers.AsNoTracking().SingleAsync(d => d.PhoneE164 == phone);
        Assert.Equal(dinerUserId, account.Id);
        Assert.False(account.IsActive);
    }

    /// <summary>
    /// Ten attempts a quarter-hour per identifier, right or wrong. The eleventh is a 429 even with
    /// the right password - "wait", not "wrong", to somebody who may have been typing it correctly.
    /// </summary>
    [SkippableFact]
    public async Task The_eleventh_attempt_on_one_identifier_is_too_many_even_when_it_is_right()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var account = await RegisteredAsync(anonymous);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var refused = await anonymous.PostAsJsonAsync(
                "/api/auth/diner/login", new { identifier = account.Username, password = "wrong-every-time" });

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var eleventh = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/login", new { identifier = account.Username, password = Password });

        await AssertProblemAsync(eleventh, HttpStatusCode.TooManyRequests, "too-many-attempts");

        // Somebody else's budget is their own.
        var other = await RegisteredAsync(anonymous);
        var fine = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/login", new { identifier = other.Username, password = Password });

        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
    }

    // ------------------------------------------------------------ the profile

    [SkippableFact]
    public async Task The_profile_changes_only_the_fields_sent_and_refuses_a_taken_name()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var mine = await RegisteredAsync(anonymous);
        var theirs = await RegisteredAsync(anonymous);

        using var me = factory.CreateClientWithToken(mine.AccessToken);

        // One field: the others stay.
        var renamed = await me.PutAsJsonAsync("/api/diner/me", new { displayName = "Anahit" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var afterRename = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Anahit", afterRename.GetProperty("displayName").GetString());
        Assert.Equal(mine.Username, afterRename.GetProperty("username").GetString());
        Assert.Equal(mine.Email, afterRename.GetProperty("email").GetString());

        // Somebody else's, either way round.
        await AssertProblemAsync(
            await me.PutAsJsonAsync("/api/diner/me", new { username = theirs.Username.ToUpperInvariant() }),
            HttpStatusCode.Conflict, "username-taken", field: "username");

        await AssertProblemAsync(
            await me.PutAsJsonAsync("/api/diner/me", new { email = theirs.Email }),
            HttpStatusCode.Conflict, "email-taken", field: "email");

        // Blanking the name is refused rather than stored: it is read out at the door.
        await AssertProblemAsync(
            await me.PutAsJsonAsync("/api/diner/me", new { displayName = "  " }),
            HttpStatusCode.BadRequest, "invalid-request", field: "displayName");

        // One's own current name is not a clash.
        Assert.Equal(
            HttpStatusCode.OK,
            (await me.PutAsJsonAsync("/api/diner/me", new { username = mine.Username })).StatusCode);

        // A new name, lowercased, and it signs in.
        var newUsername = $"Renamed.{Suffix()}";
        var changed = await me.PutAsJsonAsync("/api/diner/me", new { username = newUsername });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(
            newUsername.ToLowerInvariant(),
            (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());

        var signIn = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/login", new { identifier = newUsername, password = Password });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        // And nothing above touched the neighbour.
        await using var db = fixture.CreateContext(factory.Clock);
        var neighbour = await db.DinerUsers.SingleAsync(d => d.Id == theirs.DinerUserId);
        Assert.Equal(theirs.Username, neighbour.Username);
        Assert.Equal(theirs.Email, neighbour.Email);
    }

    [SkippableFact]
    public async Task A_code_created_account_sets_a_first_password_freely_and_then_must_give_it_to_change()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var (dinerUserId, token) = await SignInByCodeAsync(anonymous, NewPhone());
        using var me = factory.CreateClientWithToken(token);

        var before = await me.GetFromJsonAsync<JsonElement>("/api/diner/me");
        Assert.False(before.GetProperty("hasPassword").GetBoolean());
        Assert.True(before.GetProperty("phoneVerified").GetBoolean());
        Assert.False(before.TryGetProperty("username", out var noName) && noName.ValueKind != JsonValueKind.Null);

        // Something to sign in with, then a password - no current one, because there is none.
        var username = NewUsername();
        Assert.Equal(HttpStatusCode.OK, (await me.PutAsJsonAsync("/api/diner/me", new { username })).StatusCode);

        var first = await me.PutAsJsonAsync("/api/diner/me/password", new { newPassword = Password });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Setting a password ends every access token the account holds, the one it was set with
        // included - that is the session generation moving on, and it is immediate.
        await AssertSessionRevokedAsync(await me.GetAsync("/api/diner/me"));

        var signIn = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/login", new { identifier = username, password = Password });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var signedIn = await signIn.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(dinerUserId, signedIn.GetProperty("dinerUserId").GetGuid());

        using var again = factory.CreateClientWithToken(signedIn.GetProperty("accessToken").GetString()!);

        var after = await again.GetFromJsonAsync<JsonElement>("/api/diner/me");
        Assert.True(after.GetProperty("hasPassword").GetBoolean());

        // Now there is one, changing it needs it.
        const string replacement = "dolma-and-lavash-9";

        await AssertProblemAsync(
            await again.PutAsJsonAsync("/api/diner/me/password", new { currentPassword = "wrong", newPassword = replacement }),
            HttpStatusCode.Unauthorized, "invalid-credentials");

        await AssertProblemAsync(
            await again.PutAsJsonAsync("/api/diner/me/password", new { newPassword = replacement }),
            HttpStatusCode.Unauthorized, "invalid-credentials");

        // The new one is under the same rule as registration.
        await AssertProblemAsync(
            await again.PutAsJsonAsync("/api/diner/me/password", new { currentPassword = Password, newPassword = username }),
            HttpStatusCode.BadRequest, "invalid-request", field: "newPassword");

        var changed = await again.PutAsJsonAsync(
            "/api/diner/me/password", new { currentPassword = Password, newPassword = replacement });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync("/api/auth/diner/login", new { identifier = username, password = Password }),
            HttpStatusCode.Unauthorized, "invalid-credentials");

        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync(
                "/api/auth/diner/login", new { identifier = username, password = replacement })).StatusCode);
    }

    // ------------------------------------------------------------ the picture

    [SkippableFact]
    public async Task A_diner_sets_replaces_and_removes_a_picture_and_the_sweep_keeps_the_current_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var account = await RegisteredAsync(anonymous);
        using var me = factory.CreateClientWithToken(account.AccessToken);

        // Set.
        var first = await UploadAsync(me, Png(1));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var firstView = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstView.GetProperty("photoId").GetGuid();
        var firstCardUrl = firstView.GetProperty("cardUrl").GetString()!;
        Assert.Contains(firstId.ToString(), firstCardUrl);

        var afterFirst = await me.GetFromJsonAsync<JsonElement>("/api/diner/me");
        Assert.Equal(firstId, afterFirst.GetProperty("photo").GetProperty("photoId").GetGuid());

        // Served anonymously like every photo, and owned by the person under their own prefix.
        var served = await anonymous.GetAsync(firstCardUrl);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType?.MediaType);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Photos.SingleAsync(p => p.Id == firstId);
            Assert.Equal(account.DinerUserId, row.DinerUserId);
            Assert.Null(row.BranchId);
            Assert.StartsWith($"diner-{account.DinerUserId}/", row.FullPath, StringComparison.Ordinal);
        }

        // The same bytes again are the same photo.
        var again = await UploadAsync(me, Png(1));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(firstId, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("photoId").GetGuid());

        // Replace.
        var second = await UploadAsync(me, Png(2));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondView = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondView.GetProperty("photoId").GetGuid();
        var secondCardUrl = secondView.GetProperty("cardUrl").GetString()!;
        Assert.NotEqual(firstId, secondId);

        var afterSecond = await me.GetFromJsonAsync<JsonElement>("/api/diner/me");
        Assert.Equal(secondId, afterSecond.GetProperty("photo").GetProperty("photoId").GetGuid());

        // A day later the replaced one is an orphan and the current one is not.
        factory.Clock.Advance(TimeSpan.FromHours(25));
        await SweepAsync(factory);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.Photos.AnyAsync(p => p.Id == firstId), "The replaced picture survived the sweep.");
            Assert.True(await db.Photos.AnyAsync(p => p.Id == secondId), "The current picture was swept.");
            Assert.Equal(secondId, (await db.DinerUsers.SingleAsync(d => d.Id == account.DinerUserId)).PhotoId);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(firstCardUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(secondCardUrl)).StatusCode);

        // Remove: deleted there and then, row and files, so the link stops answering on the next
        // request rather than a day later when the sweep would have got to it.
        Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync("/api/diner/me/photo")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync("/api/diner/me/photo")).StatusCode);

        var afterRemove = await me.GetFromJsonAsync<JsonElement>("/api/diner/me");
        Assert.False(afterRemove.TryGetProperty("photo", out var gone) && gone.ValueKind != JsonValueKind.Null);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(secondCardUrl)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.Photos.AnyAsync(p => p.Id == secondId), "The removed picture's row survived.");
        }

        // And the bytes decide what is a picture, exactly as for a branch.
        var notAnImage = await UploadAsync(me, "<?php echo 'definitely a photo'; ?>"u8.ToArray());
        await AssertProblemAsync(notAnImage, HttpStatusCode.Conflict, "unsupported-image");
    }

    // ------------------------------------------------------------ the code flow meets a registered number

    [SkippableFact]
    public async Task A_code_to_a_registered_number_verifies_it_and_signs_that_account_in()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var account = await RegisteredAsync(anonymous);

        var (signedInAs, token) = await SignInByCodeAsync(anonymous, account.Phone, expectNewAccount: false);

        // The same account, not a second row for the same number.
        Assert.Equal(account.DinerUserId, signedInAs);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(1, await db.DinerUsers.CountAsync(d => d.PhoneE164 == account.Phone));
            Assert.NotNull((await db.DinerUsers.SingleAsync(d => d.Id == account.DinerUserId)).PhoneVerifiedAtUtc);
        }

        using var me = factory.CreateClientWithToken(token);
        var profile = await me.GetFromJsonAsync<JsonElement>("/api/diner/me");

        Assert.True(profile.GetProperty("phoneVerified").GetBoolean());
        Assert.Equal(account.Username, profile.GetProperty("username").GetString());

        // Anonymous, the first proof displaces the registered password - the verifier sets their own.
        Assert.False(profile.GetProperty("hasPassword").GetBoolean());
    }

    [SkippableFact]
    public async Task Verifying_with_the_registered_accounts_own_token_keeps_its_password_and_sessions()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        // The normal sign-up: register, then verify from the same app holding register's token.
        var account = await RegisteredAsync(anonymous);
        using var holder = factory.CreateClientWithToken(account.AccessToken);

        var (signedInAs, token) = await SignInByCodeAsync(holder, account.Phone, expectNewAccount: false);
        Assert.Equal(account.DinerUserId, signedInAs);

        using var me = factory.CreateClientWithToken(token);
        var profile = await me.GetFromJsonAsync<JsonElement>("/api/diner/me");
        Assert.True(profile.GetProperty("phoneVerified").GetBoolean());
        Assert.True(profile.GetProperty("hasPassword").GetBoolean());

        var login = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/login", new { identifier = account.Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Register's session survives: its refresh token still works.
        var refreshed = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/refresh", new { refreshToken = account.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [SkippableFact]
    public async Task Verifying_under_a_different_diners_token_still_clears_the_registered_password()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        // A registrant squats a number; the number's owner already has an account of their own and
        // verifies the squatted number while signed in to it.
        var squatter = await RegisteredAsync(anonymous);
        var (_, otherToken) = await SignInByCodeAsync(anonymous, NewPhone());
        using var other = factory.CreateClientWithToken(otherToken);

        await SignInByCodeAsync(other, squatter.Phone, expectNewAccount: false);

        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync(
                "/api/auth/diner/login", new { identifier = squatter.Username, password = Password }),
            HttpStatusCode.Unauthorized,
            "invalid-credentials");

        var refreshed = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/refresh", new { refreshToken = squatter.RefreshToken });
        Assert.False(refreshed.IsSuccessStatusCode);
    }

    [SkippableFact]
    public async Task A_squatter_loses_the_password_and_every_session_once_the_numbers_owner_verifies()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var anonymous = factory.CreateClient();

        // Somebody registers with a number that is not theirs, and keeps both tokens. The access
        // token works, and using it fills the authority cache with the account as it is now - so
        // what follows proves the owner's sign-in evicts it rather than waiting it out.
        var squatter = await RegisteredAsync(anonymous);
        using var squatterClient = factory.CreateClientWithToken(squatter.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await squatterClient.GetAsync("/api/diner/me")).StatusCode);

        // The number's owner signs in with a code.
        var (ownerId, ownerToken) = await SignInByCodeAsync(anonymous, squatter.Phone, expectNewAccount: false);
        Assert.Equal(squatter.DinerUserId, ownerId);

        // The access token the squatter still holds is refused at once, on every route - including
        // the one that would have set a new password on the owner's account with no current one.
        var reviewRoute = $"/api/diner/branches/{branch.BranchId}/review";

        await AssertSessionRevokedAsync(await squatterClient.GetAsync("/api/diner/me"));
        await AssertSessionRevokedAsync(
            await squatterClient.PutAsJsonAsync("/api/diner/me/password", new { newPassword = "the-squatter-again-2026" }));
        await AssertSessionRevokedAsync(
            await squatterClient.PutAsJsonAsync("/api/diner/me", new { displayName = "Squatter" }));
        await AssertSessionRevokedAsync(await squatterClient.PostAsJsonAsync(reviewRoute, new { rating = 1 }));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.DinerUsers.AsNoTracking().SingleAsync(d => d.Id == squatter.DinerUserId);

            Assert.Null(row.PasswordHash);
            Assert.Equal("Ani", row.DisplayName);
            Assert.False(await db.BranchReviews.AnyAsync(r => r.DinerUserId == squatter.DinerUserId));
        }

        // The squatter's password no longer opens anything - the same 401 as a wrong one.
        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync(
                "/api/auth/diner/login", new { identifier = squatter.Username, password = Password }),
            HttpStatusCode.Unauthorized,
            "invalid-credentials");

        // And the session they already held is gone: the refresh token is refused and revoked.
        var refreshed = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/refresh", new { refreshToken = squatter.RefreshToken });
        Assert.False(refreshed.IsSuccessStatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            // One live token for the account: the owner's, issued by the code after the revocation.
            Assert.Equal(
                1,
                await db.RefreshTokens.CountAsync(t => t.SubjectId == squatter.DinerUserId && t.RevokedAtUtc == null));
        }

        // The owner's token works on every one of those routes. The password goes last, because
        // setting one ends the token it was set with too.
        using var owner = factory.CreateClientWithToken(ownerToken);
        Assert.False((await owner.GetFromJsonAsync<JsonElement>("/api/diner/me")).GetProperty("hasPassword").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/diner/me", new { displayName = "Owner" })).StatusCode);
        // A first review needs a visit (K8): the owner sat at one of the branch's tables.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(db, branch, factory.Clock.UtcNow, ownerId);
        }

        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(reviewRoute, new { rating = 5 })).StatusCode);

        var set = await owner.PutAsJsonAsync("/api/diner/me/password", new { newPassword = "the-owners-own-2026" });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        await AssertSessionRevokedAsync(await owner.GetAsync("/api/diner/me"));

        var login = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/login", new { identifier = squatter.Username, password = "the-owners-own-2026" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    /// <summary>
    /// A deactivated account's token stops working without anybody evicting anything - at most the
    /// authority check's five-second window later, measured on the application clock.
    /// </summary>
    [SkippableFact]
    public async Task A_deactivated_accounts_token_is_refused_everywhere_once_the_cache_window_has_passed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var anonymous = factory.CreateClient();
        var (dinerUserId, token) = await SignInByCodeAsync(anonymous, NewPhone());
        using var diner = factory.CreateClientWithToken(token);

        // Works, and the account's state is now cached.
        Assert.Equal(HttpStatusCode.OK, (await diner.GetAsync("/api/diner/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await diner.GetAsync("/api/diner/orders")).StatusCode);

        // Switched off straight in the database, the way an operator would - nothing tells the cache.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            (await db.DinerUsers.SingleAsync(d => d.Id == dinerUserId)).SetActive(false);
            await db.SaveChangesAsync();
        }

        factory.Clock.Advance(TimeSpan.FromSeconds(6));

        await AssertSessionRevokedAsync(await diner.GetAsync("/api/diner/me"));
        await AssertSessionRevokedAsync(await diner.GetAsync("/api/diner/orders"));
        await AssertSessionRevokedAsync(
            await diner.PostAsJsonAsync($"/api/diner/branches/{branch.BranchId}/review", new { rating = 4 }));
    }

    [SkippableFact]
    public async Task An_account_whose_number_is_already_verified_keeps_its_password_when_it_verifies_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var account = await RegisteredAsync(anonymous);
        var (_, token) = await SignInByCodeAsync(anonymous, account.Phone, expectNewAccount: false);

        // A password set after the number was proved is the owner's own.
        using var me = factory.CreateClientWithToken(token);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await me.PutAsJsonAsync("/api/diner/me/password", new { newPassword = Password })).StatusCode);

        await SignInByCodeAsync(anonymous, account.Phone, expectNewAccount: false);

        var login = await anonymous.PostAsJsonAsync(
            "/api/auth/diner/login", new { identifier = account.Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.True(
            await db.RefreshTokens.CountAsync(t => t.SubjectId == account.DinerUserId && t.RevokedAtUtc == null) >= 3,
            "Verifying an already-proved number must not revoke the sessions it already has.");
    }

    // ------------------------------------------------------------ an unproved number cannot book

    [SkippableFact]
    public async Task An_unverified_diner_is_refused_booking_and_opening_a_tab_until_a_code_comes_back()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var anonymous = factory.CreateClient();
        var account = await RegisteredAsync(anonymous);
        using var unverified = factory.CreateClientWithToken(account.AccessToken);

        await AssertProblemAsync(
            await BookAsync(unverified, factory, branch), HttpStatusCode.Forbidden, "phone-not-verified");

        await AssertProblemAsync(
            await OpenByBookingAsync(unverified, "ABCD-EFGH"), HttpStatusCode.Forbidden, "phone-not-verified");

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.Reservations.AnyAsync(r => r.DinerUserId == account.DinerUserId));
        }

        // Reading one's own bookings stays open.
        Assert.Equal(HttpStatusCode.OK, (await unverified.GetAsync("/api/reservations/mine")).StatusCode);

        // The same diner proves the number, and the same routes let them through.
        var (_, verifiedToken) = await SignInByCodeAsync(anonymous, account.Phone, expectNewAccount: false);
        using var verified = factory.CreateClientWithToken(verifiedToken);

        var booked = await BookAsync(verified, factory, branch);
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);

        var code = (await booked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        // Past the gate: the booking is two days out, so what answers is the booking's own timing
        // rule rather than the phone.
        var opened = await OpenByBookingAsync(verified, code);
        Assert.NotEqual(HttpStatusCode.Forbidden, opened.StatusCode);
        await AssertProblemAsync(opened, HttpStatusCode.Conflict, "booking-too-early");
    }

    // ------------------------------------------------------------ who gets in

    [SkippableFact]
    public async Task Only_a_diner_token_reaches_the_profile()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/diner/me")).StatusCode);

        // A manager is signed in and is not a diner.
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/diner/me")).StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private sealed record Account(
        Guid DinerUserId, string Username, string Email, string Phone, string AccessToken, string RefreshToken);

    /// <summary>Books the branch's first table two days out, through the real endpoint.</summary>
    private static Task<HttpResponseMessage> BookAsync(HttpClient diner, YallaApiFactory factory, AuthBranch branch)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

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
                guestPhone = "+37411223344",
                clientCommandId = Guid.CreateVersion7(),
            });
    }

    private static Task<HttpResponseMessage> OpenByBookingAsync(HttpClient client, string bookingCode) =>
        client.PostAsJsonAsync(
            "/api/tabs/open-by-booking",
            new
            {
                bookingCode,
                deviceId = "phone-" + Suffix(),
                clientCommandId = Guid.CreateVersion7(),
                displayName = "Ani",
            });

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root);

    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    private static string NewPhone() => $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

    private static string NewUsername() => $"ani_{Suffix()}";

    private static string NewEmail() => $"ani-{Suffix()}@example.test";

    /// <summary>Posts the sign-up form, with every field valid unless a test says otherwise.</summary>
    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string? username = null,
        string? email = null,
        string? phone = null,
        string? password = null,
        string? displayName = "Ani") =>
        client.PostAsJsonAsync(
            "/api/auth/diner/register",
            new
            {
                username = username ?? NewUsername(),
                email = email ?? NewEmail(),
                password = password ?? Password,
                phoneE164 = phone ?? NewPhone(),
                displayName,
            });

    private static async Task<Account> RegisteredAsync(HttpClient client)
    {
        var username = NewUsername();
        var email = NewEmail();
        var phone = NewPhone();

        var response = await RegisterAsync(client, username, email, phone);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return new Account(
            body.GetProperty("dinerUserId").GetGuid(),
            username,
            email,
            phone,
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>The code flow, end to end, reading the code out of the Development response.</summary>
    private static async Task<(Guid DinerUserId, string AccessToken)> SignInByCodeAsync(
        HttpClient client,
        string phone,
        bool expectNewAccount = true)
    {
        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectNewAccount, body.GetProperty("isNewAccount").GetBoolean());

        return (body.GetProperty("dinerUserId").GetGuid(), body.GetProperty("accessToken").GetString()!);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "me.png");

        return await client.PostAsync("/api/diner/me/photo", form);
    }

    /// <summary>The orphan sweep, through the hosted API's own services and on its clock.</summary>
    private static async Task SweepAsync(YallaApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IPhotoService>().SweepOrphansAsync();
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string? field = null)
    {
        Assert.Equal(status, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(code, problem.GetProperty("code").GetString());

        if (field is not null)
        {
            Assert.Equal(field, problem.GetProperty("context").GetProperty("field").GetString());
        }
    }

    /// <summary>
    /// A 401 for an ended diner session: the code the app branches on, and the header that says the
    /// token itself is what is wrong.
    /// </summary>
    internal static async Task AssertSessionRevokedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            h => h.Scheme == "Bearer" && h.Parameter == "error=\"invalid_token\"");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("session-revoked", problem.GetProperty("code").GetString());
    }

    /// <summary>A small PNG, different per seed so no two uploads deduplicate into one row.</summary>
    private static byte[] Png(byte seed)
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(seed, (byte)(255 - seed), 128));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
