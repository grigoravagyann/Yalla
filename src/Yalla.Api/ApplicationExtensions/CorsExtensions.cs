namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// CORS, driven by the <c>Cors:AllowedOrigins</c> configuration list.
/// </summary>
/// <remarks>
/// <para>
/// Origins come from configuration (environment variables <c>Cors__AllowedOrigins__0</c>,
/// <c>__1</c>, …) rather than from code, because the three clients live at different addresses in
/// every environment.
/// </para>
/// <para>
/// In Development, the admin panel's Vite server and the Expo dev server are added on top of
/// whatever is configured, so a fresh clone works with no CORS setup. <b>Nowhere else</b>: an
/// empty list outside Development is a locked-down policy that allows no cross-origin browser
/// call at all, and logs loudly about it. The permissive fallback that used to live here would
/// have shipped <c>Access-Control-Allow-Origin: *</c> to production the first time somebody
/// forgot a variable, and a warning in a log nobody reads is not a control.
/// </para>
/// </remarks>
public static class CorsExtensions
{
    public const string PolicyName = "YallaClients";

    /// <summary>
    /// The local frontends. The Vite dev server for the admin panel, and the two ports the Expo
    /// dev server uses for the diner and staff apps.
    /// </summary>
    private static readonly string[] DevelopmentOrigins =
    [
        "http://localhost:5173",
        "http://localhost:8081",
        "http://localhost:19006",
    ];

    public static WebApplicationBuilder AddYallaCors(this WebApplicationBuilder builder)
    {
        var allowedOrigins = ResolveOrigins(builder.Configuration, builder.Environment);

        builder.Services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (allowedOrigins.Length == 0)
            {
                // No origins, no cross-origin access. Same-origin callers and non-browser clients
                // - the staff tablet, the mobile apps - are unaffected, because CORS is a browser
                // mechanism and they do not send an Origin header.
                return;
            }

            policy.WithOrigins(allowedOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }));

        return builder;
    }

    public static WebApplication UseYallaCors(this WebApplication app)
    {
        var allowedOrigins = ResolveOrigins(app.Configuration, app.Environment);

        if (allowedOrigins.Length == 0)
        {
            app.Logger.LogError(
                "Cors:AllowedOrigins is empty in the {Environment} environment. No browser origin "
                + "can call this API. Configure the client origins.",
                app.Environment.EnvironmentName);
        }
        else
        {
            app.Logger.LogInformation(
                "CORS allows {OriginCount} origin(s): {Origins}",
                allowedOrigins.Length, string.Join(", ", allowedOrigins));
        }

        app.UseCors(PolicyName);

        return app;
    }

    private static string[] ResolveOrigins(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        return environment.IsDevelopment()
            ? [.. configured.Concat(DevelopmentOrigins).Distinct(StringComparer.OrdinalIgnoreCase)]
            : configured;
    }
}
