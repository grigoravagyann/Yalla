using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>The health endpoint and the shape it answers in.</summary>
public static class HealthCheckExtensions
{
    public static WebApplication MapYallaHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponse,
        });

        return app;
    }

    /// <summary>
    /// Names each check and its verdict rather than answering a bare "Healthy", so an operator
    /// polling this can tell a process that is up from a database that is reachable.
    /// </summary>
    private static Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,

                // The exception message from a failed check, never a stack trace.
                error = entry.Value.Exception?.Message,
            }),
        });

        return context.Response.WriteAsync(payload);
    }
}
