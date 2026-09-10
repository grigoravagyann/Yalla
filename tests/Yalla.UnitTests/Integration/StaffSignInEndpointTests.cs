using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The sign-in issue route on the wire, and the anonymous reset flow it hands over to.
/// </summary>
/// <remarks>
/// <para>
/// <c>StaffSignInIssuanceTests</c> proves the rules against the service. What only a request
/// through the real pipeline can prove is that the route <i>carries</i> them: the policies on the
/// group, the validation filter answering 422 by field name, and the credential-minting route
/// sitting behind the sign-in rate budget rather than the global one.
/// </para>
/// <para>
/// The last test is about the flow that existed before any of this. Nothing pinned it: the
/// forgot-password mail was minted, handed to a sender that delivers nothing outside Development,
/// and never exercised end to end. The guarantees the new route leans on - single use, one hour,
/// stored as a hash - were promises with no test, and a promise with no test is one that survives
/// being broken.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class StaffSignInEndpointTests(SqlServerFixture fixture)
{
    private const string Password = "a-perfectly-long-password";

    /// <summary>
    /// Waiters and neighbouring venues are refused by the group's policies before the service
    /// sees them, a manager cannot hand a fellow manager or the owner a sign-in, none of the four
    /// mints anything, and an owner or platform admin can.
    /// </summary>
    [SkippableFact]
    public async Task The_route_carries_the_hierarchy_and_the_venue_scope()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch mine;
        AuthBranch theirs;
        PlatformAdminAccount admin;
        Guid secondManagerId;
        OwnerAccount owner;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
            owner = await SeedOwnerAsync(db, mine.VenueId);
            secondManagerId = await SeedManagerAsync(db, mine.VenueId);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, mine));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        using var ownerClient = factory.CreateClientWithToken(await SignInAsync(factory, owner.Email, owner.Password));

        var route = $"/api/venues/{mine.VenueId}/staff/{secondManagerId}/sign-in";

        // A 403 alone cannot say which layer refused: the service answers the same status to the
        // same callers. What tells a policy refusal apart is that it never ran the service, so
        // there is no `context.operation` - only the service's refusal names the operation.
        var byWaiter = await waiter.PostAsJsonAsync(route, Body("waiter"));
        var byNeighbour = await neighbour.PostAsJsonAsync(route, Body("neighbour"));

        Assert.Equal(HttpStatusCode.Forbidden, byWaiter.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byNeighbour.StatusCode);
        Assert.DoesNotContain("\"operation\"", await byWaiter.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"operation\"", await byNeighbour.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Same venue, same rank: the policies pass a manager to the route, the service refuses.
        var peer = await manager.PostAsJsonAsync(route, Body("peer"));

        Assert.Equal(HttpStatusCode.Forbidden, peer.StatusCode);
        Assert.Equal("forbidden", await CodeAsync(peer));

        // Same venue, higher rank: the owner's account is not a manager's to hand out.
        var above = await manager.PostAsJsonAsync($"/api/venues/{mine.VenueId}/staff/{owner.StaffMemberId}/sign-in", Body("above"));

        Assert.Equal(HttpStatusCode.Forbidden, above.StatusCode);
        Assert.Equal("forbidden", await CodeAsync(above));

        // Refused before minting, whichever layer did the refusing.
        await using (var check = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(0, await check.PasswordResetTokens.CountAsync(t => t.StaffMemberId == secondManagerId || t.StaffMemberId == owner.StaffMemberId));
        }

        var byPlatform = await platform.PostAsJsonAsync(route, Body("platform"));

        Assert.Equal(HttpStatusCode.OK, byPlatform.StatusCode);
        AssertLinkShape(await byPlatform.Content.ReadFromJsonAsync<JsonElement>(), secondManagerId);

        var byOwner = await ownerClient.PostAsJsonAsync(route, Body("owner"));

        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        AssertLinkShape(await byOwner.Content.ReadFromJsonAsync<JsonElement>(), secondManagerId);
    }

    /// <summary>
    /// Minting a credential spends the sign-in budget, not the three-hundred-a-minute global one.
    /// </summary>
    /// <remarks>
    /// Ten a minute is plenty for a person and not much for a script holding a stolen owner
    /// session; the global budget is sized for a busy floor. The route carries no policy of its
    /// own by inheritance - the staff group has none - so this is the line that would be lost.
    /// </remarks>
    [SkippableFact]
    public async Task Issuing_sign_ins_spends_the_sign_in_budget()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // Two a minute, so the budget is spent in two requests. The global limit is raised out of
        // the way: the question is which bucket, not whether there is one.
        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:AuthPermitLimit", "2")
            .With("RateLimiting:AuthWindowSeconds", "60")
            .With("RateLimiting:GlobalPermitLimit", "1000");

        AuthBranch mine;
        OwnerAccount owner;
        Guid secondManagerId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            owner = await SeedOwnerAsync(db, mine.VenueId);
            secondManagerId = await SeedManagerAsync(db, mine.VenueId);
        }

        using var ownerClient = factory.CreateClientWithToken(await SignInAsync(factory, owner.Email, owner.Password));

        var route = $"/api/venues/{mine.VenueId}/staff/{secondManagerId}/sign-in";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(route, Body("budget"))).StatusCode);
        }

        var spent = await ownerClient.PostAsJsonAsync(route, Body("budget"));

        Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);
        Assert.Equal("rate-limited", await CodeAsync(spent));
    }

    /// <summary>
    /// The body is validated before anything is looked up, and the refusal names the field -
    /// for an address that is malformed, missing, or longer than a column can hold.
    /// </summary>
    /// <remarks>
    /// The overlong one is the case the <c>[StringLength]</c> exists for: without it the address
    /// reaches the domain guard, which refuses with a 400 that names nothing a form can highlight.
    /// </remarks>
    [SkippableFact]
    public async Task A_bad_or_missing_address_is_a_422_naming_the_field()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch mine;
        OwnerAccount owner;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            owner = await SeedOwnerAsync(db, mine.VenueId);
        }

        using var ownerClient = factory.CreateClientWithToken(await SignInAsync(factory, owner.Email, owner.Password));

        var route = $"/api/venues/{mine.VenueId}/staff/{mine.ManagerId}/sign-in";

        // 323 characters: past the 320 the column holds, and well-formed enough that only the
        // length can be what refuses it.
        var overlong = new string('a', 310) + "@example.test";

        foreach (var body in new object[] { new { email = "not-an-address" }, new { }, new { email = overlong } })
        {
            var refused = await ownerClient.PostAsJsonAsync(route, body);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal("validation-failed", problem.GetProperty("code").GetString());
            Assert.Equal("email", problem.GetProperty("context").GetProperty("field").GetString());
        }

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await verify.PasswordResetTokens.CountAsync(t => t.StaffMemberId == mine.ManagerId));
    }

    /// <summary>
    /// The whole hand-off, on the wire: issue, open the link, choose a password, sign in - and
    /// the link is spent.
    /// </summary>
    [SkippableFact]
    public async Task The_issued_link_sets_a_password_once_and_the_person_signs_in()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch mine;
        OwnerAccount owner;
        Guid newcomerId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            owner = await SeedOwnerAsync(db, mine.VenueId);
            newcomerId = await SeedManagerAsync(db, mine.VenueId);
        }

        using var ownerClient = factory.CreateClientWithToken(await SignInAsync(factory, owner.Email, owner.Password));

        var email = $"newcomer-{Guid.NewGuid():N}@example.test";
        var issued = await ownerClient.PostAsJsonAsync(
            $"/api/venues/{mine.VenueId}/staff/{newcomerId}/sign-in", new { email });

        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);

        var link = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resetLink").GetString()!;
        var resetToken = TokenFrom(link);

        using var anonymous = factory.CreateClient();

        var reset = await anonymous.PostAsJsonAsync(
            "/api/auth/venue/reset-password", new { resetToken, newPassword = Password });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var signedIn = await anonymous.PostAsJsonAsync("/api/auth/venue/sign-in", new { email, password = Password });

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(newcomerId, (await signedIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("staffMemberId").GetGuid());

        var reused = await anonymous.PostAsJsonAsync(
            "/api/auth/venue/reset-password", new { resetToken, newPassword = "a-different-long-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal("reset-token-invalid", await CodeAsync(reused));

        // And the list now says so, which is what the console's badge reads.
        var listed = await (await ownerClient.GetAsync($"/api/venues/{mine.VenueId}/staff"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var row = listed.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == newcomerId);

        Assert.Equal(email, row.GetProperty("email").GetString());
        Assert.True(row.GetProperty("hasPasswordSignIn").GetBoolean());
    }

    /// <summary>
    /// The forgot-password flow that was never pinned: the sender receives exactly one link, the
    /// server keeps only its hash, and the link dies on use and after an hour.
    /// </summary>
    [SkippableFact]
    public async Task The_forgot_password_flow_hands_the_sender_one_link_that_dies_on_use_or_after_an_hour()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var sender = new RecordingResetSender();

        await using var factory = NewFactory().WithServices(services =>
        {
            // Registered with TryAdd by the application, so the stand-in has to go first or this
            // one is silently ignored and the test reads an empty list.
            services.RemoveAll<IPasswordResetSender>();
            services.AddSingleton<IPasswordResetSender>(sender);
        });

        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var anonymous = factory.CreateClient();

        var requested = await anonymous.PostAsJsonAsync(
            "/api/auth/venue/request-password-reset", new { email = branch.ManagerEmail });

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);

        var delivery = Assert.Single(sender.Deliveries);
        Assert.Equal(branch.ManagerEmail, delivery.Email);

        var resetToken = TokenFrom(delivery.Link);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var stored = await db.PasswordResetTokens.AsNoTracking()
                .SingleAsync(t => t.StaffMemberId == branch.ManagerId);

            // Hashed at rest: the row cannot be turned back into the link.
            Assert.Equal(Secrets.Hash(resetToken), stored.TokenHash);
            Assert.DoesNotContain(resetToken, stored.TokenHash, StringComparison.Ordinal);
        }

        var reset = await anonymous.PostAsJsonAsync(
            "/api/auth/venue/reset-password", new { resetToken, newPassword = Password });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(
                "/api/auth/venue/sign-in", new { email = branch.ManagerEmail, password = branch.ManagerPassword })).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync(
                "/api/auth/venue/sign-in", new { email = branch.ManagerEmail, password = Password })).StatusCode);

        var reused = await anonymous.PostAsJsonAsync(
            "/api/auth/venue/reset-password", new { resetToken, newPassword = "a-different-long-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal("reset-token-invalid", await CodeAsync(reused));

        // The hour, from both sides. A second link is still good just inside it - a shorter
        // lifetime would refuse this one and pass the test below all the same.
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/request-password-reset", new { email = branch.ManagerEmail })).StatusCode);

        Assert.Equal(2, sender.Deliveries.Count);
        var fresh = TokenFrom(sender.Deliveries[1].Link);

        factory.Clock.Advance(TimeSpan.FromMinutes(59));

        var inTime = await anonymous.PostAsJsonAsync(
            "/api/auth/venue/reset-password", new { resetToken = fresh, newPassword = "a-third-long-enough-password" });

        Assert.Equal(HttpStatusCode.NoContent, inTime.StatusCode);

        // And a third, left for longer than the mailed link's hour.
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await anonymous.PostAsJsonAsync("/api/auth/venue/request-password-reset", new { email = branch.ManagerEmail })).StatusCode);

        Assert.Equal(3, sender.Deliveries.Count);
        var stale = TokenFrom(sender.Deliveries[2].Link);

        factory.Clock.Advance(TimeSpan.FromMinutes(61));

        var expired = await anonymous.PostAsJsonAsync(
            "/api/auth/venue/reset-password", new { resetToken = stale, newPassword = "a-fourth-long-enough-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal("reset-token-invalid", await CodeAsync(expired));
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>An owner who can sign in to the panel, seeded the way the fixture manager is.</summary>
    private sealed record OwnerAccount(Guid StaffMemberId, string Email, string Password);

    private static async Task<OwnerAccount> SeedOwnerAsync(YallaDbContext db, Guid venueId)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.test";
        const string password = "owner-password-that-is-long-enough";

        var owner = new StaffMember(venueId, "Founder", $"+374{Random.Shared.Next(10_000_000, 99_999_999)}", StaffRole.Owner, "hash");
        owner.SetPasswordCredentials(email, new SecretHasher().Hash(password));

        db.StaffMembers.Add(owner);
        await db.SaveChangesAsync();

        return new OwnerAccount(owner.Id, email, password);
    }

    /// <summary>A manager with a PIN and nothing else - the console's own creation, as shipped.</summary>
    private static async Task<Guid> SeedManagerAsync(YallaDbContext db, Guid venueId)
    {
        var manager = new StaffMember(venueId, "Second Manager", $"+374{Random.Shared.Next(10_000_000, 99_999_999)}", StaffRole.Manager, "hash");

        db.StaffMembers.Add(manager);
        await db.SaveChangesAsync();

        return manager.Id;
    }

    private static async Task<string> SignInAsync(YallaApiFactory factory, string email, string password)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/venue/sign-in", new { email, password });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static object Body(string part) => new { email = $"{part}-{Guid.NewGuid():N}@example.test" };

    private static void AssertLinkShape(JsonElement body, Guid staffMemberId)
    {
        Assert.Equal(staffMemberId, body.GetProperty("staffMemberId").GetGuid());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("email").GetString()));
        Assert.NotEmpty(TokenFrom(body.GetProperty("resetLink").GetString()!));
        Assert.True(body.TryGetProperty("expiresAtUtc", out _));
        Assert.False(body.GetProperty("replacedExistingSignIn").GetBoolean());
    }

    /// <summary>The handle out of the link, the way the console's reset page reads it.</summary>
    private static string TokenFrom(string resetLink)
    {
        const string marker = "#token=";
        var at = resetLink.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(at >= 0, $"The link does not carry the token in its fragment: {resetLink}");

        return Uri.UnescapeDataString(resetLink[(at + marker.Length)..]);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    /// <summary>A sender the test can read, in place of the one that only logs.</summary>
    private sealed class RecordingResetSender : IPasswordResetSender
    {
        public List<(string Email, string Link)> Deliveries { get; } = [];

        public Task SendAsync(string email, string resetLink, string localeCode, CancellationToken ct)
        {
            Deliveries.Add((email, resetLink));

            return Task.CompletedTask;
        }
    }
}
