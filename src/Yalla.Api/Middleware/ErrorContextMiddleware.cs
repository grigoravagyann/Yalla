using Serilog.Context;

namespace Yalla.Api.Middleware;

/// <summary>
/// Pushes request correlation onto the Serilog log context so every entry written while handling
/// a request carries it.
/// </summary>
/// <remarks>
/// This middleware deliberately does <b>not</b> catch or log exceptions.
/// <see cref="ExceptionHandler.UnifiedExceptionHandler"/> is the single log point for failures;
/// two of them produce duplicate entries and disagreeing status codes.
/// </remarks>
internal sealed class ErrorContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // The same value that goes into the response envelope, so a support request quoting a
        // traceId leads straight to the log entries for that request.
        using (LogContext.PushProperty("TraceId", context.TraceIdentifier))
        using (LogContext.PushProperty("RequestPath", context.Request.Path.Value))
        {
            await next(context);
        }
    }
}
