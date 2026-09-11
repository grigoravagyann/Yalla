using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// Fixed-window rate limiting, configured from the <c>RateLimiting</c> section.
/// </summary>
/// <remarks>
/// Enabled by default in Production and disabled elsewhere, so local work and automated tests are
/// not throttled. Setting <c>RateLimiting:Enabled</c> explicitly overrides that either way - it
/// is the kill switch, and it works from an environment variable with no redeploy.
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>Policy name for the general authentication endpoints.</summary>
    public const string AuthPolicy = "auth";

    /// <summary>
    /// Tighter policy for <c>request-code</c>.
    /// </summary>
    /// <remarks>
    /// Codes cost money to send once a real provider is wired in, so an unlimited request endpoint
    /// is an invoice generator. This is the per-address half of the limit; the per-number half
    /// lives in <c>PhoneCodeRateLimiter</c>, because the number is in the request body and this
    /// middleware runs long before anything has read it.
    /// </remarks>
    public const string CodeRequestPolicy = "auth-code-request";

    /// <summary>
    /// Tighter policy for the PIN exchange.
    /// </summary>
    /// <remarks>
    /// Complements, rather than replaces, the per-staff-member lockout: this one throttles a
    /// tablet grinding through staff ids, the lockout protects one person's four digits.
    /// </remarks>
    public const string PinPolicy = "auth-pin";

    /// <summary>
    /// The availability query: anonymous, and the hottest endpoint in the product.
    /// </summary>
    /// <remarks>
    /// A diner sliding the time picker re-asks with every change, and the endpoint takes no token,
    /// so there is nothing else standing between one phone and the branch's whole floor read. The
    /// limit is deliberately generous - a person browsing genuinely does make a burst of these -
    /// but finite, which is the difference between a busy endpoint and an open one.
    /// </remarks>
    public const string AvailabilityPolicy = "availability";

    /// <summary>
    /// The public branch pages: anonymous, unauthenticated, and the surface a scraper finds first.
    /// </summary>
    /// <remarks>
    /// <b>Tighter than the app routes, not looser.</b> Everything else anonymous here is reached by
    /// somebody who has at least scanned a QR code at a table; this is reached by anybody with the
    /// URL, and the URL is meant to be pasted into Instagram. A per-address limit is what stops one
    /// client walking the estate.
    /// </remarks>
    public const string PublicPolicy = "public";

    /// <summary>
    /// The browse list, <c>GET /api/public/venues</c>: its own budget per caller, instead of the page
    /// budget above.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the diner app's Explore screen as well as the web chooser, so phones open it far more
    /// often than any one page - and a carrier puts thousands of phones behind one address, which
    /// spent the thirty-a-minute page budget before dinner. A 429 there is the no-restaurants
    /// screen. The page budget exists to stop one client walking the estate page by page; this
    /// route <i>is</i> the estate, in one cached response, so there is nothing to walk.
    /// </para>
    /// <para>
    /// It also has its own city-wide ceiling in the chained limiter, instead of the branch-sized one
    /// it used to share - see <see cref="PublicBrowsePath"/>.
    /// </para>
    /// </remarks>
    public const string PublicBrowsePolicy = "public-browse";

    /// <summary>
    /// The browse list's path, which carries no branch.
    /// </summary>
    /// <remarks>
    /// With no branch id and no slug pair in its route it fell through to one shared partition under
    /// the per-branch ceiling - three hundred a minute for every phone in the city at once. It is
    /// cached for seconds and costs next to nothing between refreshes, so it gets a ceiling of its
    /// own, sized for a city rather than for one branch.
    /// </remarks>
    public const string PublicBrowsePath = "/api/public/venues";

    /// <summary>
    /// A cap on one branch's public traffic, whoever is asking.
    /// </summary>
    /// <remarks>
    /// The per-address limit above does nothing about a distributed scrape, and the thing worth
    /// protecting is a branch's free-table count - the one number here that is not cached for long.
    /// This is the second half: past it, everyone gets a 429 for that branch until the window turns.
    /// Deliberately generous enough that a venue whose link goes round a group chat is unaffected.
    /// <para>
    /// Applied through the <b>global chain</b> rather than as an endpoint policy, because an
    /// endpoint carries one policy: a second <c>RequireRateLimiting</c> replaces the first rather
    /// than composing with it, which silently disabled the per-address limit the first time this
    /// was written that way.
    /// </para>
    /// </remarks>
    public const string PublicPathPrefix = "/api/public";

    /// <summary>
    /// The manage-booking routes, which are limited a third time - per token.
    /// </summary>
    /// <remarks>
    /// The per-address limit already bounds somebody guessing tokens, because every guess is a
    /// different address's budget and a different partition. What it does not bound is somebody who
    /// <i>has</i> a link - it was pasted into a group chat - hammering that one booking's cancel.
    /// This is that half: one booking, one budget, however many people are holding its URL.
    /// </remarks>
    public const string PublicBookingPathPrefix = "/api/public/bookings";

    /// <summary>
    /// Whether rate limiting is switched on for this environment. Both the registration and the
    /// middleware read this one decision - asking the configuration twice is how you end up
    /// calling <c>UseRateLimiter</c> without the services behind it.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetSection("RateLimiting").GetValue<bool?>("Enabled") ?? environment.IsProduction();

    public static IServiceCollection AddYallaRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (!IsEnabled(configuration, environment))
        {
            return services;
        }

        var section = configuration.GetSection("RateLimiting");

        var globalPermitLimit = section.GetValue<int?>("GlobalPermitLimit") ?? 300;
        var globalWindowSeconds = section.GetValue<int?>("GlobalWindowSeconds") ?? 60;
        var authPermitLimit = section.GetValue<int?>("AuthPermitLimit") ?? 10;
        var availabilityPermitLimit = section.GetValue<int?>("AvailabilityPermitLimit") ?? 60;
        var availabilityWindowSeconds = section.GetValue<int?>("AvailabilityWindowSeconds") ?? 60;
        var authWindowSeconds = section.GetValue<int?>("AuthWindowSeconds") ?? 60;
        var codeRequestPermitLimit = section.GetValue<int?>("CodeRequestPermitLimit") ?? 5;
        var codeRequestWindowSeconds = section.GetValue<int?>("CodeRequestWindowSeconds") ?? 300;
        var pinPermitLimit = section.GetValue<int?>("PinPermitLimit") ?? 10;
        var pinWindowSeconds = section.GetValue<int?>("PinWindowSeconds") ?? 60;

        // Thirty a minute per address is a person browsing; it is not a crawler walking the estate.
        var publicPermitLimit = section.GetValue<int?>("PublicPermitLimit") ?? 30;
        var publicWindowSeconds = section.GetValue<int?>("PublicWindowSeconds") ?? 60;

        // And a ceiling per branch, whoever is asking, for the distributed case.
        var publicBranchPermitLimit = section.GetValue<int?>("PublicBranchPermitLimit") ?? 300;
        var publicBranchWindowSeconds = section.GetValue<int?>("PublicBranchWindowSeconds") ?? 60;

        // The browse list: its own budget per caller, and its own city-wide ceiling.
        var publicBrowsePermitLimit = section.GetValue<int?>("PublicBrowsePermitLimit") ?? 120;
        var publicBrowseWindowSeconds = section.GetValue<int?>("PublicBrowseWindowSeconds") ?? 60;
        var publicBrowseCeilingPermitLimit = section.GetValue<int?>("PublicBrowseCeilingPermitLimit") ?? 6000;
        var publicBrowseCeilingWindowSeconds = section.GetValue<int?>("PublicBrowseCeilingWindowSeconds") ?? 60;

        // And a ceiling per manage token, for the link that went round a group chat.
        var publicBookingPermitLimit = section.GetValue<int?>("PublicBookingPermitLimit") ?? 20;
        var publicBookingWindowSeconds = section.GetValue<int?>("PublicBookingWindowSeconds") ?? 60;

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Chained, not two policies. An endpoint carries one rate-limiting policy - a second
            // RequireRateLimiting replaces the first rather than composing with it - so the
            // per-branch ceiling lives here, where chaining is the supported shape. Every request
            // passes the per-address global limiter; only the public routes also pass the second.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        PartitionKey(context),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = globalPermitLimit,
                            Window = TimeSpan.FromSeconds(globalWindowSeconds),
                            QueueLimit = 0,
                        })),

                // One branch's public page has one budget, however many addresses are asking for
                // it - which is the half a per-address limit cannot do anything about. The browse
                // list, which is no branch's, has a city-sized budget of its own. Off the public
                // routes this is a no-op.
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    if (!context.Request.Path.StartsWithSegments(PublicPathPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return RateLimitPartition.GetNoLimiter<string>("not-public");
                    }

                    // Its own key and its own options. A partition keeps the options of whichever
                    // request created it, so the browse list must never share a key with anything
                    // sized differently.
                    if (IsBrowseList(context))
                    {
                        return RateLimitPartition.GetFixedWindowLimiter(
                            BrowsePartitionKey,
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = publicBrowseCeilingPermitLimit,
                                Window = TimeSpan.FromSeconds(publicBrowseCeilingWindowSeconds),
                                QueueLimit = 0,
                            });
                    }

                    return RateLimitPartition.GetFixedWindowLimiter(
                        PublicBranchPartitionKey(context),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = publicBranchPermitLimit,
                            Window = TimeSpan.FromSeconds(publicBranchWindowSeconds),
                            QueueLimit = 0,
                        });
                }),

                // And one manage link has one budget. Chained rather than an endpoint policy for
                // the same reason as the branch ceiling above: an endpoint carries one policy, and
                // these routes already carry the per-address one. Off the booking routes, a no-op.
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    context.Request.Path.StartsWithSegments(
                        PublicBookingPathPrefix, StringComparison.OrdinalIgnoreCase)
                        ? RateLimitPartition.GetFixedWindowLimiter(
                            ManageTokenPartitionKey(context),
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = publicBookingPermitLimit,
                                Window = TimeSpan.FromSeconds(publicBookingWindowSeconds),
                                QueueLimit = 0,
                            })
                        : RateLimitPartition.GetNoLimiter<string>("not-a-booking")));

            options.AddPolicy(AuthPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authPermitLimit,
                        Window = TimeSpan.FromSeconds(authWindowSeconds),
                        QueueLimit = 0,
                    }));

            options.AddPolicy(CodeRequestPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = codeRequestPermitLimit,
                        Window = TimeSpan.FromSeconds(codeRequestWindowSeconds),
                        QueueLimit = 0,
                    }));

            options.AddPolicy(AvailabilityPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = availabilityPermitLimit,
                        Window = TimeSpan.FromSeconds(availabilityWindowSeconds),
                        QueueLimit = 0,
                    }));

            options.AddPolicy(PublicPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = publicPermitLimit,
                        Window = TimeSpan.FromSeconds(publicWindowSeconds),
                        QueueLimit = 0,
                    }));

            // The browse list's own per-caller budget, replacing the page budget on that one route.
            options.AddPolicy(PublicBrowsePolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = publicBrowsePermitLimit,
                        Window = TimeSpan.FromSeconds(publicBrowseWindowSeconds),
                        QueueLimit = 0,
                    }));

            options.AddPolicy(PinPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = pinPermitLimit,
                        Window = TimeSpan.FromSeconds(pinWindowSeconds),
                        QueueLimit = 0,
                    }));

            // A rejection answers in the same envelope as every other failure, rather than an
            // empty 429 the clients would each have to special-case.
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.ContentType = "application/problem+json";

                await context.HttpContext.Response.WriteAsync(
                    // Web defaults, so this envelope is camelCase like every other response. The
                    // default serializer options are PascalCase, which would hand the clients a
                    // 429 body they cannot parse with the same reader as every other error.
                    JsonSerializer.Serialize(new UnifiedErrorEnvelope
                    {
                        Type = ErrorCodes.TypeFor(ErrorCodes.RateLimited),
                        Title = ErrorCodes.TitleFor(ErrorCodes.RateLimited),
                        Status = StatusCodes.Status429TooManyRequests,
                        Detail = "Too many requests. Try again shortly.",
                        Instance = context.HttpContext.Request.Path.Value,
                        Code = ErrorCodes.RateLimited,
                        TraceId = context.HttpContext.TraceIdentifier,
                    },
                    JsonSerializerOptions.Web),
                    cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// Which branch a public request is about, for the per-branch ceiling.
    /// </summary>
    /// <remarks>
    /// Read from the route, which is where every public branch route carries it - either as a
    /// <c>branchId</c> or as the venue/branch slug pair. The browse list, which has neither, never
    /// gets here: it has a partition of its own, sized for a city - see <see cref="PublicBrowsePath"/>.
    /// </remarks>
    /// <summary>
    /// One manage link's partition, keyed by a digest of the token rather than the token.
    /// </summary>
    /// <remarks>
    /// The limiter holds its partition keys in memory for the life of the window, and a bearer
    /// capability that opens somebody's booking has no business sitting in that dictionary - or in
    /// whatever dumps it during a diagnosis. A digest partitions exactly as well.
    /// </remarks>
    private static string ManageTokenPartitionKey(HttpContext context) =>
        context.Request.RouteValues.TryGetValue("token", out var token) && token is string text
            ? "token:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32]

            // No token in the route: one shared budget, which is the safe direction to be wrong in.
            : "token:none";

    private static string PublicBranchPartitionKey(HttpContext context)
    {
        var route = context.Request.RouteValues;

        // A manage link belongs to no branch on the wire, and bucketing every one of them into the
        // shared browse partition below would put all manage traffic, everywhere, under a single
        // branch-sized budget. They are limited per token instead - see ManageTokenPartitionKey.
        if (context.Request.Path.StartsWithSegments(
                PublicBookingPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "public:booking";
        }

        if (route.TryGetValue("branchId", out var branchId) && branchId is not null)
        {
            return $"branch:{branchId}";
        }

        return route.TryGetValue("venueSlug", out var venueSlug)
               && route.TryGetValue("branchSlug", out var branchSlug)
            ? $"slug:{venueSlug}/{branchSlug}"

            // Not the browse list's key: that one is sized for a city, and a partition keeps the
            // options of whichever request created it.
            : "public:unaddressed";
    }

    /// <summary>The browse list's one city-wide partition under the chained public limiter.</summary>
    private const string BrowsePartitionKey = "public:browse";

    /// <summary>Whether this request is the browse list, with or without a trailing slash.</summary>
    private static bool IsBrowseList(HttpContext context) =>
        context.Request.Path.StartsWithSegments(PublicBrowsePath, StringComparison.OrdinalIgnoreCase, out var rest)
        && (!rest.HasValue || rest.Value == "/");

    /// <summary>
    /// Adds the limiter to the pipeline, but only when it was registered. Call this after routing
    /// (and, once identity exists, after authentication) so the partition key can be the user.
    /// </summary>
    public static WebApplication UseYallaRateLimiting(this WebApplication app)
    {
        if (IsEnabled(app.Configuration, app.Environment))
        {
            app.UseRateLimiter();
        }
        else
        {
            app.Logger.LogInformation(
                "Rate limiting is disabled for the {Environment} environment. "
                + "Set RateLimiting:Enabled to true to switch it on.",
                app.Environment.EnvironmentName);
        }

        return app;
    }

    /// <summary>
    /// One budget per caller: the principal where a request has one, the remote address otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This read <c>Identity.Name</c>, which <c>AddJwtBearer</c> is configured to take from the
    /// <c>StaffMemberId</c> claim - and <b>three of the five principal types do not carry one</b>. A
    /// tab participant, a diner and an enrolled tablet each produced a null name and therefore the
    /// single key <c>user:</c>, so every caller of all three kinds, everywhere on the platform,
    /// shared one budget with the other two.
    /// </para>
    /// <para>
    /// Two things followed, and the second is the worse one. The PIN limiter is reached with a
    /// <b>device</b> token, so ten attempts a minute was ten for every tablet in the product at
    /// once: anybody holding any valid token of those three kinds could spend it and stop every
    /// waiter on the platform signing in. And <see cref="RateLimiterOptions.GlobalLimiter"/> keys
    /// on this for <i>every</i> request, so its three hundred a minute was three hundred shared by
    /// every diner and every open tab there is. Tab participants are the highest-volume identity in
    /// the product and ordering is the thing they do; that ceiling is a busy Friday evening rather
    /// than an attack.
    /// </para>
    /// <para>
    /// The switch covers <see cref="Domain.Enums.PrincipalType"/> member by member on purpose, and
    /// a test walks the enum and requires each one to produce its own key - so a sixth identity
    /// type fails that test rather than quietly joining somebody else's bucket, which is how the
    /// first three got there.
    /// </para>
    /// </remarks>
    internal static string PartitionKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Address(context);
        }

        var principal = context.User.PrincipalType();

        var subject = principal switch
        {
            Domain.Enums.PrincipalType.TabParticipant => context.User.Guid(YallaClaims.ParticipantId),
            Domain.Enums.PrincipalType.Diner => context.User.Guid(YallaClaims.DinerUserId),

            // A tablet is not a person, but it is the caller the PIN endpoint sees, and one budget
            // per tablet is exactly what that endpoint wants.
            Domain.Enums.PrincipalType.StaffDevice => context.User.Guid(YallaClaims.DeviceId),

            Domain.Enums.PrincipalType.StaffSession => context.User.Guid(YallaClaims.StaffMemberId),
            Domain.Enums.PrincipalType.VenueUser => context.User.Guid(YallaClaims.StaffMemberId),
            _ => null,
        };

        // An authenticated token that names nobody falls back to its address rather than to a key
        // it would share: never worse than an anonymous request, which is the safe direction for
        // this to be wrong in.
        return subject is { } id ? $"{principal}:{id}" : Address(context);
    }

    private static string Address(HttpContext context) =>
        $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
