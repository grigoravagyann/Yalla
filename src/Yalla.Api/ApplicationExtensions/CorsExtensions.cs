namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// CORS, driven by the <c>Cors:AllowedOrigins</c> configuration list.
/// </summary>
/// <remarks>
/// Origins come from configuration (environment variables <c>Cors__AllowedOrigins__0</c>,
/// <c>__1</c>, …) rather than from code, because the three clients live at different addresses in
/// every environment. While the list is empty the permissive behaviour is kept and logged as a
/// warning, so a deployment cannot silently cut off a frontend - but the warning is there to be
/// acted on before production.
/// </remarks>
public static class CorsExtensions
{
    public const string PolicyName = "YallaClients";

    public static WebApplicationBuilder AddYallaCors(this WebApplicationBuilder builder)
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        builder.Services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials();
            }
            else
            {
                policy.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            }
        }));

        return builder;
    }

    public static WebApplication UseYallaCors(this WebApplication app)
    {
        var allowedOrigins = app.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        if (allowedOrigins.Length == 0)
        {
            app.Logger.LogWarning(
                "Cors:AllowedOrigins is not configured - falling back to Access-Control-Allow-Origin: *. "
                + "Configure the client origins before this reaches production.");
        }

        app.UseCors(PolicyName);

        return app;
    }
}
