using Microsoft.AspNetCore.Diagnostics;
using Yalla.Api.Errors;

namespace Yalla.Api.ExceptionHandler;

/// <summary>
/// The single place an unhandled exception becomes a response, and the single place one is logged.
/// </summary>
/// <remarks>
/// Registered for every environment. There is deliberately no developer exception page anywhere:
/// it preempts this handler, so a staging environment ends up leaking stack traces while
/// answering in a shape no client can parse. Developers get the detail from the log, correlated
/// by the <c>traceId</c> that is in the response body.
/// </remarks>
internal sealed class UnifiedExceptionHandler(
    ILogger<UnifiedExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // The client hung up. Nothing to answer and nothing worth logging as a failure.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request {Method} {Path} was aborted by the client.",
                httpContext.Request.Method, httpContext.Request.Path);
            return true;
        }

        var mapped = ApiExceptionMapper.Map(exception);
        var traceId = httpContext.TraceIdentifier;

        if (mapped.LogAsError)
        {
            logger.LogError(
                exception,
                "Unhandled exception on {Method} {Path}. TraceId={TraceId} Status={Status} Code={Code}",
                httpContext.Request.Method, httpContext.Request.Path, traceId, mapped.Status, mapped.Code);
        }
        else
        {
            // A refused request is not a fault of the server. Logged at warning so it is still
            // visible when diagnosing a client, without burying real errors.
            logger.LogWarning(
                "Request refused on {Method} {Path}. TraceId={TraceId} Status={Status} Code={Code} Reason={Reason}",
                httpContext.Request.Method, httpContext.Request.Path, traceId, mapped.Status, mapped.Code,
                exception.Message);
        }

        // If the response has already begun there is no way to replace it with an envelope;
        // saying so in the log beats throwing a second exception out of the handler.
        if (httpContext.Response.HasStarted)
        {
            logger.LogWarning(
                "Response for {Path} had already started; the error envelope could not be written. TraceId={TraceId}",
                httpContext.Request.Path, traceId);
            return true;
        }

        httpContext.Response.StatusCode = mapped.Status;
        httpContext.Response.ContentType = "application/json";

        await httpContext.Response.WriteAsJsonAsync(
            new UnifiedErrorEnvelope
            {
                TraceId = traceId,
                Status = mapped.Status,
                Code = mapped.Code,
                Message = mapped.Message,
            },
            cancellationToken);

        return true;
    }
}
