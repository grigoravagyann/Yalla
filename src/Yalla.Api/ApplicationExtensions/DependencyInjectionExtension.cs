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
            throw new InvalidOperationException(
                "Connection string 'Yalla' is not configured. Set ConnectionStrings:Yalla in "
                + "appsettings.{Environment}.json, or as the ConnectionStrings__Yalla environment variable.");
        }

        builder.Services.AddInfrastructure(connectionString);

        // Enums travel as strings on the wire and as ints in the database. A client reading
        // "Occupied" cannot silently mean something else after a member is inserted, while the
        // stored int keeps a rename from orphaning rows.
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.JsonSerializerOptions.DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull;
            });

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
}
