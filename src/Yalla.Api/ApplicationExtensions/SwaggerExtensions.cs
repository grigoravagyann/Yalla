using System.Reflection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Yalla.Api.Filters;
using Yalla.Api.Middleware;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// Swagger/OpenAPI generation for the Yalla API.
/// </summary>
/// <remarks>
/// <para>
/// This document is a dependency of the frontend, not a convenience. The React monorepo generates
/// its TypeScript types from it with <c>openapi-typescript</c>, so anything the schema gets wrong
/// becomes a hand-written type that silently drifts from the DTO it is supposed to mirror. That
/// is the reason for the operation-id discipline, the nullability settings and the enum filter
/// below: each of them is the difference between a generated client that compiles into the truth
/// and one that compiles into a plausible lie.
/// </para>
/// <para>
/// Exposure is a separate question from generation, and the answer is off by default. See
/// <see cref="UseYallaSwagger"/>.
/// </para>
/// </remarks>
public static class SwaggerExtensions
{
    private const string DocumentName = "v1";

    /// <summary>Configuration section holding <c>Enabled</c> and <c>AllowedIps</c>.</summary>
    public const string SectionName = "Swagger";

    /// <summary>The path everything Swagger serves lives under.</summary>
    public const string BasePath = "/swagger";

    public static IServiceCollection AddYallaSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "Yalla API",
                Version = DocumentName,
                Description =
                    "Table reservation and in-app ordering for restaurants and cafes. Consumed by "
                    + "the diner app, the staff tablet and the admin panel.\n\n"
                    + "Conventions: money is a whole count of Armenian dram (properties suffixed "
                    + "`Amd`); every instant is UTC; wall-clock values (opening hours, the local "
                    + "date and time on a booking) are dates and times in the branch's own time "
                    + "zone and are never converted; enums are integers matching the database, "
                    + "with each member documented on the schema.\n\n"
                    + "Every failure is an RFC 7807 problem document. Branch on its `code`, which "
                    + "is a stable slug, never on the message.",
            });

            AddBearerSecurity(options);

            // Every operation publishes the problem-document responses it can produce, so a
            // generated client sees the error shape instead of discovering it in production.
            options.OperationFilter<ErrorResponsesOperationFilter>();

            // After the generic one: it replaces the envelope with the real shape, and a
            // oneOf where a status code can arrive in more than one form.
            options.OperationFilter<ProblemShapesOperationFilter>();

            // Puts the endpoint's own words on the responses it declared, which is where the
            // "someone just took that table" 409 gets described as the normal outcome it is.
            options.OperationFilter<ResponseDescriptionOperationFilter>();

            // A non-nullable C# string must not become `string | null` in TypeScript, and a
            // required property must not become optional. Without these two, every generated
            // model is a sea of `?` and the frontend writes null checks for values that cannot be
            // null.
            options.SupportNonNullableReferenceTypes();
            options.NonNullableReferenceTypesAsRequired();

            // And the case those two cannot see: a [Required] nullable value - a booking's date and
            // time - which is nullable only so the attribute can tell absent from midnight.
            options.SchemaFilter<RequiredValueSchemaFilter>();

            // Records and DTOs from four assemblies share short names. Qualifying the schema id
            // with the namespace keeps two different `TabView`s from silently becoming one.
            options.CustomSchemaIds(type => type.FullName?.Replace('+', '.') ?? type.Name);

            // Lets a $ref carry its own description and nullability instead of being a bare
            // pointer, which is what makes property-level XML comments survive into the document.
            options.UseAllOfToExtendReferenceSchemas();

            // Surfaces the XML doc comments generated for every project (see
            // Directory.Build.props) as endpoint and schema descriptions.
            IncludeXmlComments(options);

            // Registered AFTER the XML comments, and that order is load-bearing: schema filters
            // run in registration order, and Swashbuckle's XML filter assigns Description rather
            // than appending to it. Registered first, the enum legend below would be silently
            // overwritten by the enum's own doc comment.
            //
            // Enums are integers on the wire; this is what stops them being anonymous integers in
            // the generated types.
            options.SchemaFilter<EnumDescriptionSchemaFilter>();
        });

        return services;
    }

    /// <summary>
    /// Whether Swagger is exposed at all. Off unless <c>Swagger:Enabled</c> says otherwise.
    /// </summary>
    /// <remarks>
    /// Deliberately a setting rather than an environment check. "Development and Staging" is a
    /// rule that breaks the day someone adds a QA environment, and it cannot be switched off
    /// during an incident without a redeploy.
    /// </remarks>
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetSection(SectionName).GetValue<bool>("Enabled");

    /// <summary>
    /// Serves the document and the UI, when enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When it is not enabled nothing is mapped, so <c>/swagger/index.html</c> and
    /// <c>/swagger/v1/swagger.json</c> fall through to a plain <b>404</b> - not a 401 and not a
    /// redirect. Either of those would confirm that Swagger is there, which is the one thing a
    /// probe should not learn about a production ordering and payment API.
    /// </para>
    /// <para>
    /// The IP allowlist, when configured, answers 404 as well, for the same reason.
    /// </para>
    /// </remarks>
    public static WebApplication UseYallaSwagger(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration))
        {
            app.Logger.LogInformation(
                "Swagger is disabled. Set Swagger:Enabled to true to expose {BasePath}.", BasePath);

            return app;
        }

        // Ahead of UseSwagger, so a disallowed address never reaches the document generator.
        app.UseMiddleware<SwaggerIpAllowlistMiddleware>();

        app.UseSwagger(options => options.RouteTemplate = "swagger/{documentName}/swagger.json");
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint($"/swagger/{DocumentName}/swagger.json", "Yalla API v1");
            options.DocumentTitle = "Yalla API";
            options.DisplayRequestDuration();
        });

        app.Logger.LogWarning(
            "Swagger is exposed at {BasePath} in the {Environment} environment.",
            BasePath, app.Environment.EnvironmentName);

        return app;
    }

    /// <summary>
    /// Declares the bearer scheme and applies it to every operation, so the UI has an
    /// Authorize button and a generated client knows how to send a token.
    /// </summary>
    /// <remarks>
    /// Applied globally rather than per-operation because the anonymous endpoints are the
    /// exception - four sign-in routes and a health check - and a security requirement on an
    /// anonymous operation is harmless, while a missing one on a protected operation is not.
    /// </remarks>
    private static void AddBearerSecurity(SwaggerGenOptions options)
    {
        const string schemeId = "bearer";

        options.AddSecurityDefinition(schemeId, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description =
                "A token from one of the four sign-in flows under `/api/auth`. Paste the token "
                + "itself; the `Bearer` prefix is added for you.\n\n"
                + "Which token you need depends on the endpoint: a tab participant token for the "
                + "`/api/tabs/{tabId}` routes, a staff session token for the branch routes, an "
                + "admin-panel token for the venue routes.",
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = schemeId },
            }] = [],
        });
    }

    private static void IncludeXmlComments(SwaggerGenOptions options)
    {
        var assemblyNames = new[]
        {
            Assembly.GetExecutingAssembly().GetName().Name,
            "Yalla.Application",
            "Yalla.Domain",
        };

        foreach (var assemblyName in assemblyNames)
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.xml");
            if (File.Exists(path))
            {
                options.IncludeXmlComments(path, includeControllerXmlComments: true);
            }
        }
    }
}
