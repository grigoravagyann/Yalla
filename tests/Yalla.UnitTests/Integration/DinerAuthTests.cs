using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Identity type 2: phone number, one-time code, no password.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class DinerAuthTests(SqlServerFixture fixture)
{
    private static string NewPhone() =>
        $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

    [SkippableFact]
    public async Task The_whole_flow_issues_tokens_and_creates_the_account_on_first_verification()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var phone = NewPhone();

        var code = await RequestCodeAsync(client, phone);
        Assert.NotNull(code);

        var verify = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        var body = await verify.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("refreshToken").GetString()));
        Assert.True(body.GetProperty("isNewAccount").GetBoolean());

        // The account is created by verifying, not by a separate registration step - a separate
        // registration step is a step people abandon.
        await using var db = fixture.CreateContext(factory.Clock);
        Assert.True(await db.DinerUsers.AnyAsync(d => d.PhoneE164 == phone));

        // And the code is single use: the same one will not work twice.
        var replay = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [SkippableFact]
    public async Task A_wrong_code_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var phone = NewPhone();

        var code = await RequestCodeAsync(client, phone);

        var response = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code = Wrong(code!) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("verification-code-invalid", await ErrorCodeAsync(response));
    }

    /// <summary>
    /// Six digits is only 20 bits, which is safe solely because the attempt limit holds. This is
    /// the test that says so.
    /// </summary>
    [SkippableFact]
    public async Task The_sixth_attempt_is_rejected_outright()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var phone = NewPhone();

        var code = await RequestCodeAsync(client, phone);
        var wrong = Wrong(code!);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var refused = await client.PostAsJsonAsync(
                "/api/auth/diner/verify-code", new { phoneE164 = phone, code = wrong });

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // The sixth is not checked at all: the code is dead. 429 rather than 401, because the
        // useful signal is "stop retrying and ask for a new one".
        var sixth = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code = wrong });

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.Equal("too-many-attempts", await ErrorCodeAsync(sixth));

        // And the burnt code cannot be redeemed even with the right digits.
        var correct = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    [SkippableFact]
    public async Task An_expired_code_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var phone = NewPhone();

        var code = await RequestCodeAsync(client, phone);

        // One second past the five-minute lifetime.
        factory.Clock.Advance(PhoneVerificationCode.Lifetime + TimeSpan.FromSeconds(1));

        var response = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("verification-code-expired", await ErrorCodeAsync(response));
    }

    /// <summary>
    /// The response must not be a way to ask a stranger's phone book who uses Yalla.
    /// </summary>
    [SkippableFact]
    public async Task Request_code_answers_identically_for_a_known_and_an_unknown_number()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var registered = NewPhone();
        var unknown = NewPhone();

        // Give the first number an account by signing it in once.
        var code = await RequestCodeAsync(client, registered);
        var signIn = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = registered, code });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.True(await db.DinerUsers.AnyAsync(d => d.PhoneE164 == registered));
            Assert.False(await db.DinerUsers.AnyAsync(d => d.PhoneE164 == unknown));
        }

        var known = await client.PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = registered });

        var stranger = await client.PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = unknown });

        Assert.Equal(known.StatusCode, stranger.StatusCode);

        // Identical apart from the development code itself, which is a random six digits and says
        // nothing about whether the number is registered.
        var knownBody = Redact(await known.Content.ReadAsStringAsync());
        var strangerBody = Redact(await stranger.Content.ReadAsStringAsync());

        Assert.Equal(knownBody, strangerBody);
    }

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>
    /// Requests a code and reads it out of the response.
    /// </summary>
    /// <remarks>
    /// <c>developmentCode</c> is only populated because the host is running in Development. In any
    /// other environment it is null, whatever configuration says.
    /// </remarks>
    private static async Task<string?> RequestCodeAsync(HttpClient client, string phone)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = phone, localeCode = "hy" });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("developmentCode").GetString();
    }

    private static string Wrong(string code) =>
        code == "000000" ? "111111" : "000000";

    private static string Redact(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"developmentCode\":\"\\d{6}\"", "\"developmentCode\":\"******\"");

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        return problem.GetProperty("code").GetString();
    }
}
