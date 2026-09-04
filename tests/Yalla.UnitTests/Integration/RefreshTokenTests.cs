using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Refresh token rotation, and what happens when a rotated one comes back.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class RefreshTokenTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Reuse is the theft signal. There is no way to tell the real client from the thief, so both
    /// are made to sign in again - revoking only the reused token would leave whichever of them
    /// refreshed last in possession of a live session.
    /// </summary>
    [SkippableFact]
    public async Task A_rotated_refresh_token_cannot_be_reused_and_reuse_revokes_the_chain()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var client = factory.CreateClient();

        var signIn = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in",
            new { email = branch.ManagerEmail, password = branch.ManagerPassword });

        signIn.EnsureSuccessStatusCode();

        var first = await ReadRefreshTokenAsync(signIn);

        // One normal rotation. The handle we sent is now spent; its successor is live.
        var rotated = await client.PostAsJsonAsync(
            "/api/auth/venue/refresh", new { refreshToken = first });

        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = await ReadRefreshTokenAsync(rotated);
        Assert.NotEqual(first, second);

        // Now replay the spent one, as a thief with a stolen copy would.
        var reused = await client.PostAsJsonAsync(
            "/api/auth/venue/refresh", new { refreshToken = first });

        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        var problem = await reused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refresh-token-reused", problem.GetProperty("code").GetString());

        // The successor the real client is holding is dead too. That is the point.
        var successor = await client.PostAsJsonAsync(
            "/api/auth/venue/refresh", new { refreshToken = second });

        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);

        await using var verify = fixture.CreateContext(factory.Clock);

        var chain = await verify.RefreshTokens
            .AsNoTracking()
            .Where(t => t.SubjectType == RefreshTokenSubject.VenueUser && t.SubjectId == branch.ManagerId)
            .ToListAsync();

        Assert.NotEmpty(chain);
        Assert.All(chain, token => Assert.NotNull(token.RevokedAtUtc));
        Assert.Contains(chain, token => token.RevokedReason == "rotated-token-reused");
    }

    [SkippableFact]
    public async Task Signing_out_revokes_the_chain()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var client = factory.CreateClient();

        var signIn = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in",
            new { email = branch.ManagerEmail, password = branch.ManagerPassword });

        var refreshToken = await ReadRefreshTokenAsync(signIn);

        var signOut = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-out", new { refreshToken });

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);

        var afterwards = await client.PostAsJsonAsync(
            "/api/auth/venue/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);

        // Signing out is never allowed to fail, including for a handle that is already dead.
        var again = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-out", new { refreshToken });

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    /// <summary>
    /// A diner's refresh token is not an admin-panel refresh token, even though both are opaque
    /// handles in the same table.
    /// </summary>
    [SkippableFact]
    public async Task A_diner_refresh_token_is_refused_on_the_venue_refresh_endpoint()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = phone });

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        var dinerRefresh = await ReadRefreshTokenAsync(verified);

        var crossed = await client.PostAsJsonAsync(
            "/api/auth/venue/refresh", new { refreshToken = dinerRefresh });

        Assert.Equal(HttpStatusCode.Unauthorized, crossed.StatusCode);

        // And it still works where it belongs - the failed attempt above must not have spent it.
        var proper = await client.PostAsJsonAsync(
            "/api/auth/diner/refresh", new { refreshToken = dinerRefresh });

        Assert.Equal(HttpStatusCode.OK, proper.StatusCode);
    }

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static async Task<string> ReadRefreshTokenAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("refreshToken").GetString()!;
    }
}
