namespace Yalla.Api.Middleware;

/// <summary>
/// The two response headers an API that serves JSON and images to browsers owes every caller.
/// </summary>
/// <remarks>
/// <para>
/// <c>X-Content-Type-Options: nosniff</c> on everything: the photo route serves bytes that a venue
/// uploaded, and a browser that second-guesses <c>image/webp</c> from their content is a browser
/// that can be talked into running them. The web image's Caddy sends the same header for the static
/// build, so the console's origin and the API's origin agree.
/// </para>
/// <para>
/// <c>Cache-Control: no-store</c> under <c>/api/auth</c>: those bodies carry access and refresh
/// tokens, verification codes in Development and the roster of who may sign in on a tablet. None
/// of it belongs in a browser cache, a proxy cache or the back button.
/// </para>
/// <para>
/// Set from <c>OnStarting</c> rather than before <c>next</c>, so an endpoint that sets its own
/// <c>Cache-Control</c> - a photo variant that is immutable by content hash - keeps it.
/// </para>
/// </remarks>
internal sealed class ResponseHeadersMiddleware(RequestDelegate next)
{
    private static readonly PathString AuthPrefix = new("/api/auth");

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            var headers = http.Response.Headers;

            headers.XContentTypeOptions = "nosniff";

            if (http.Request.Path.StartsWithSegments(AuthPrefix) && headers.CacheControl.Count == 0)
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
