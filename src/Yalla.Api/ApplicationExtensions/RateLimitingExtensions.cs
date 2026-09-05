using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Yalla.Api.Errors;

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

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = globalPermitLimit,
                        Window = TimeSpan.FromSeconds(globalWindowSeconds),
                        QueueLimit = 0,
                    }));

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
    /// Partitions by authenticated user where there is one, falling back to the remote address.
    /// Identity is a later module; this reads the claim as soon as one exists, without a change
    /// here.
    /// </summary>
    private static string PartitionKey(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? $"user:{context.User.Identity.Name}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
