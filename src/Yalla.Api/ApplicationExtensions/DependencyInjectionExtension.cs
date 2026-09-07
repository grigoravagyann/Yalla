using System.Text.Json.Serialization;
using Yalla.Api.ExceptionHandler;
using Yalla.Infrastructure;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// The composition root. Everything the host registers goes through here so
/// <c>Program.cs</c> stays a readable description of the pipeline rather than a list of
/// registrations.
/// </summary>
public static class DependencyInjectionExtension
{
    public static WebApplicationBuilder AddServices(this WebApplicationBuilder builder, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Yalla");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail at startup, not on the first request that happens to touch the database.
            //
            // Naming the environment matters more than it looks: the usual cause of this is not a
            // missing setting but an unexpected environment. ASPNETCORE_ENVIRONMENT unset means
            // Production, which reads no appsettings.Development.json - so the connection string
            // sitting right there in that file is simply never loaded.
            var environmentName = builder.Environment.EnvironmentName;

            throw new InvalidOperationException(
                $"Connection string 'Yalla' is not configured for the '{environmentName}' environment. "
                + $"Set ConnectionStrings:Yalla in appsettings.{environmentName}.json, or as the "
                + "ConnectionStrings__Yalla environment variable. "
                + (builder.Environment.IsDevelopment()
                    ? "appsettings.Development.json is loaded but has no value for it."
                    : $"If you meant to run locally, set ASPNETCORE_ENVIRONMENT=Development - "
                      + $"appsettings.Development.json carries the local connection string and is not read in '{environmentName}'."));
        }

        RequireManageBookingUrl(builder);

        builder.Services.AddInfrastructure(connectionString, configuration);

        // Enums are integers on the wire and integers in the database, and the two must agree.
        //
        // They previously did not. The string converter here applied to MVC only, while the
        // minimal APIs that serve every endpoint in this system used the framework default and
        // wrote integers - and Swashbuckle reads MVC's options, so the generated schema described
        // enums as strings that no endpoint ever produced. The frontend generates its TypeScript
        // from that schema, so it would have been comparing `status === "Occupied"` against a 4.
        //
        // Integers are also the right answer on their own terms: the stored int keeps a renamed
        // member from orphaning existing rows. What the schema owes the frontend instead is the
        // meaning of each number, which EnumDescriptionSchemaFilter supplies as x-enum-varnames.
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
                options.JsonSerializerOptions.DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull);

        // The options the minimal APIs actually use. Set explicitly so the two pipelines cannot
        // drift apart again.
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

        // One failure shape for the whole API, one log point. See UnifiedExceptionHandler.
        builder.Services.AddExceptionHandler<UnifiedExceptionHandler>();
        builder.Services.AddProblemDetails();

        // /health verifies the database is actually reachable rather than only that the process
        // is up - a liveness probe that cannot fail is not worth polling.
        builder.Services
            .AddHealthChecks()
            .AddDbContextCheck<YallaDbContext>("database");

        builder.Services.AddYallaSwagger();
        builder.Services.AddYallaRateLimiting(configuration, builder.Environment);

        return builder;
    }
    /// <summary>
    /// Refuses to start outside Development without a usable manage-booking link template.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This one cannot be repaired after the fact, which is why it fails startup.</b> The manage
    /// URL is written into the reminder payload at the moment a web booking is created, and the
    /// server keeps only the token's <i>hash</i> - so a booking made while this setting is wrong
    /// carries a dead cancel link forever, and no later fix can mint the token again to rewrite it.
    /// </para>
    /// <para>
    /// The person holding that dead link is a diner who booked from the public page: no app, no
    /// push, and no other way to cancel. A link that goes nowhere turns them into the no-show the
    /// whole manage-booking feature exists to prevent. A degraded start is worse than no start.
    /// </para>
    /// <para>
    /// <b>Loopback is rejected, not just absence.</b> The default used to sit in
    /// <c>appsettings.json</c>, which loads in every environment - so the setting was never
    /// <i>unset</i>, and a check for absence alone would have passed a production deployment
    /// straight through while it shipped <c>localhost</c> links. The value now lives in
    /// <c>appsettings.Development.json</c>, and this refuses loopback anyway, because the failure
    /// being prevented is "points somewhere the diner cannot reach" rather than "is blank".
    /// </para>
    /// <para>
    /// The join-link template is deliberately <i>not</i> guarded here. It is generated per request
    /// and returned live rather than persisted, so a wrong value is fixed by correcting the setting
    /// - and the host has the QR code, which is the primary way that token is handed over anyway.
    /// The distinction is persistence, not importance.
    /// </para>
    /// </remarks>
    private static void RequireManageBookingUrl(WebApplicationBuilder builder)
    {
        // Development gets the local default and no argument about it.
        if (builder.Environment.IsDevelopment())
        {
            return;
        }

        const string key = "PublicWeb:ManageBookingUrlTemplate";
        var template = builder.Configuration.GetValue<string>(key);
        var environmentName = builder.Environment.EnvironmentName;

        string? problem = null;

        if (string.IsNullOrWhiteSpace(template))
        {
            problem = "it is not set";
        }
        else if (!template.Contains("{token}", StringComparison.Ordinal))
        {
            // Without the placeholder every booking gets the same link, which is not a link to a
            // booking at all.
            problem = "it does not contain the {token} placeholder";
        }
        else if (!Uri.TryCreate(
                     template.Replace("{token}", "t", StringComparison.Ordinal),
                     UriKind.Absolute,
                     out var url)
                 || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            problem = $"'{template}' is not an absolute http or https URL";
        }
        else if (url.IsLoopback)
        {
            problem = $"'{template}' points at loopback, which no diner's phone can reach";
        }

        if (problem is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{key} is unusable in the '{environmentName}' environment: {problem}. "
            + "Set it as the PublicWeb__ManageBookingUrlTemplate environment variable, to the public "
            + "address of the booking page with {token} where the manage token goes - for example "
            + "https://yalla.app/booking/{token}. "
            + "This is not optional and startup will not continue without it: the URL is written "
            + "into a web booking's reminder when the booking is made, only the token's hash is "
            + "stored, and a booking created with the wrong value carries a dead cancel link that "
            + "cannot be repaired afterwards.");
    }
}
