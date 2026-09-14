using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Api.Authorization;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Every route the API serves asks for authorization, unless it is on a short, written-down list of
/// routes that are anonymous on purpose.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against is not a policy that answers wrongly - the matrix and the boundary
/// tests cover that - but a policy that was never attached. Delete one <c>RequireAuthorization</c>
/// from a group and every route in it quietly becomes public, and no test that only signs in the right
/// people would notice: the right people still get in.
/// </para>
/// <para>
/// It reads the real routing table rather than the source, for the reason
/// <see cref="RequestShapeContractTests"/> gives: what the app serves cannot drift from what the check
/// sees. No database is needed - the table exists as soon as the host is composed.
/// </para>
/// <para>
/// <b>Anonymous means what the middleware thinks it means.</b> An <see cref="IAllowAnonymous"/> anywhere
/// in an endpoint's metadata wins over every <see cref="IAuthorizeData"/> beside it, so a route that
/// carries both is anonymous however protective it looks. Swagger is not listed: it is middleware, not
/// an endpoint, and has its own gate (<see cref="SwaggerExposureTests"/>).
/// </para>
/// </remarks>
public class EveryRouteIsAuthorizedTests
{
    /// <summary>Everything under this prefix is the public surface, anonymous by design and rate limited.</summary>
    private const string PublicPrefix = "/api/public/";

    /// <summary>
    /// The routes outside <see cref="PublicPrefix"/> that take no token, each with the reason. A route
    /// added here needs one; an entry that stops matching a route, or stops being anonymous, fails.
    /// </summary>
    private static readonly Dictionary<string, string> Anonymous = new(StringComparer.Ordinal)
    {
        ["POST /api/auth/diner/request-code"] = "Asking for a sign-in code is how a diner without a token gets one.",
        ["POST /api/auth/diner/verify-code"] = "Exchanges the code for the first token.",
        ["POST /api/auth/diner/register"] = "Creates the account the first token belongs to.",
        ["POST /api/auth/diner/login"] = "Exchanges a password for a token.",
        ["POST /api/auth/diner/refresh"] = "Runs when the access token has already expired; the refresh handle is the credential.",
        ["POST /api/auth/diner/sign-out"] = "Revokes a refresh handle, which is the credential; works with an expired access token.",
        ["POST /api/auth/staff/enrol"] = "A new tablet has no token; the one-time enrolment code is the credential.",
        ["POST /api/auth/staff/renew"] = "Runs after the session token expired; the refresh handle is the credential.",
        ["POST /api/auth/staff/sign-out"] = "Revokes a refresh handle, which is the credential.",
        ["POST /api/auth/venue/sign-in"] = "Exchanges an email and password for a token.",
        ["POST /api/auth/venue/refresh"] = "Runs after the access token expired; the refresh handle is the credential.",
        ["POST /api/auth/venue/sign-out"] = "Revokes a refresh handle, which is the credential.",
        ["POST /api/auth/venue/request-password-reset"] = "Somebody who forgot their password has no token.",
        ["POST /api/auth/venue/reset-password"] = "The emailed reset token is the credential.",
        ["GET /api/photos/{photoId:guid}/{variant}"] = "Photo bytes are linked from public pages and the app's image tags, which send no header.",
        ["* /health"] = "A liveness probe must not depend on a credential.",
        ["GET /api/branches/{branchId:guid}/menu"] = "The menu behind the table's QR code, read before anybody has scanned or signed in.",
        ["GET /api/branches/{branchId:guid}/availability"] = "The public booking page draws the room from it before the diner has an account.",
        ["POST /api/tabs/open"] = "Scanning the table's QR code is the product's front door; the QR token is the credential.",
        ["POST /api/tabs/join"] = "The host's invitation token is the credential.",
    };

    /// <summary>
    /// Routes that need a token but name no policy, because what the token must be is checked by the
    /// handler against the token itself (a tablet's device token, before any staff member taps a PIN).
    /// </summary>
    private static readonly HashSet<string> AuthenticatedOnly = new(StringComparer.Ordinal)
    {
        "GET /api/auth/staff/device",
        "GET /api/auth/staff/roster",
        "POST /api/auth/staff/pin",
    };

    [Fact]
    public void Every_route_requires_authorization_unless_it_is_anonymous_on_purpose()
    {
        var routes = Routes();

        // A check that walked an empty table would pass. The API has well over a hundred routes.
        Assert.True(routes.Count >= 100, $"Only {routes.Count} routes were found; the routing table was not read.");

        var unguarded = routes
            .Where(r => r.IsAnonymous && !IsDeliberatelyAnonymous(r.Key))
            .Select(r => r.HasAuthorizeData
                ? $"{r.Key} carries RequireAuthorization but also AllowAnonymous, which wins - it is public."
                : $"{r.Key} requires no authorization. Add RequireAuthorization with a policy, or list it in Anonymous with the reason.")
            .ToList();

        Assert.True(unguarded.Count == 0, string.Join(Environment.NewLine, unguarded));
    }

    [Fact]
    public void Every_anonymous_allowlist_entry_still_names_a_route_that_is_still_anonymous()
    {
        var routes = Routes().ToDictionary(r => r.Key, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var (key, reason) in Anonymous)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"The allowlist entry {key} has no reason.");

            if (!routes.TryGetValue(key, out var route))
            {
                problems.Add($"{key} is on the anonymous allowlist but no longer exists. Delete the entry.");
            }
            else if (!route.IsAnonymous)
            {
                problems.Add($"{key} is on the anonymous allowlist but now requires authorization. Delete the entry.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        // And the public surface exists, so the prefix is not excusing nothing.
        Assert.Contains(routes.Keys, key => key.Contains(PublicPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// A route that asks for authorization asks for the right kind: a diner route for a diner, a platform
    /// route for the platform, a branch route scoped to the branch in it.
    /// </summary>
    /// <remarks>
    /// A bare <c>RequireAuthorization()</c> admits any signed-in token - a tab participant's, a tablet's -
    /// so swapping a named policy for it passes the test above and opens the route to the wrong people.
    /// </remarks>
    [Fact]
    public void Each_surface_asks_for_the_policy_its_callers_need()
    {
        var problems = new List<string>();

        foreach (var route in Routes().Where(r => !r.IsAnonymous))
        {
            if (route.Policies.Count == 0 && !AuthenticatedOnly.Contains(route.Key))
            {
                problems.Add($"{route.Key} requires a token but names no policy, so any signed-in token passes.");
            }

            Require(route, "/api/diner/", YallaPolicies.VerifiedDiner, problems);
            Require(route, "/api/platform/", YallaPolicies.PlatformAdminOnly, problems);
            Require(route, "/api/branches/{branchId:guid}", YallaPolicies.BranchScoped, problems);
            Require(route, "/api/venues/{venueId:guid}", YallaPolicies.VenueScoped, problems);

            if (route.Pattern.StartsWith("/api/tabs/{tabId:guid}", StringComparison.Ordinal)
                && !route.Policies.Any(p => p.StartsWith(YallaPolicies.TabParticipant, StringComparison.Ordinal))
                && !route.Policies.Contains(YallaPolicies.BranchScoped))
            {
                problems.Add($"{route.Key} is a tab route with neither a tab-participant policy nor BranchScoped.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void Require(RouteInfo route, string prefix, string policy, List<string> problems)
    {
        if (route.Pattern.StartsWith(prefix, StringComparison.Ordinal) && !route.Policies.Contains(policy))
        {
            problems.Add($"{route.Key} is under {prefix} but does not require {policy} (it has [{string.Join(", ", route.Policies)}]).");
        }
    }

    private static bool IsDeliberatelyAnonymous(string key) =>
        key.Contains(" " + PublicPrefix, StringComparison.Ordinal) || Anonymous.ContainsKey(key);

    private sealed record RouteInfo(
        string Method,
        string Pattern,
        IReadOnlyList<string> Policies,
        bool HasAuthorizeData,
        bool IsAnonymous)
    {
        public string Key => $"{Method} {Pattern}";
    }

    /// <summary>Every route in the app's routing table, one row per HTTP method.</summary>
    private static List<RouteInfo> Routes()
    {
        using var factory = new YallaApiFactory();
        using var scope = factory.Services.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();
        var routes = new List<RouteInfo>();

        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var hasPolicyObject = endpoint.Metadata.GetMetadata<AuthorizationPolicy>() is not null;
            var hasAuthorizeData = authorize.Count > 0 || hasPolicyObject;
            var allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

            var policies = authorize
                .Select(a => a.Policy)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;

            if (pattern.Length > 1)
            {
                pattern = "/" + pattern.Trim('/');
            }

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } verbs
                ? verbs
                : ["*"];

            foreach (var method in methods)
            {
                routes.Add(new RouteInfo(method, pattern, policies, hasAuthorizeData, allowsAnonymous || !hasAuthorizeData));
            }
        }

        return routes;
    }
}
