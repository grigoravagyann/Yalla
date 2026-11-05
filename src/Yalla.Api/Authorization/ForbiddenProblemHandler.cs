using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Yalla.Api.Errors;

namespace Yalla.Api.Authorization;

/// <summary>
/// A policy's refusal written as the <c>forbidden</c> problem, like every other refusal.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a request that fails an authorization policy - a guest a host has not approved
/// yet placing an order, a waiter reading another branch's floor - is answered by the framework
/// with a <b>bare 403 and no body</b>. Every refusal a service throws goes through
/// <see cref="ApiExceptionMapper"/> and comes back as a problem document with a code, a title, a
/// trace id and the media type <c>docs/error-contract.md</c> promises; the policy's refusal was the
/// one answer in the API that did not, and the client's <c>ForbiddenError</c> carried no code to
/// show. The load test (<c>tests/load/load.mjs</c>, tab scenario) is where it showed: two in three
/// phones at a table are joiners waiting for the host, and their orders came back as 403s with
/// nothing to say.
/// </para>
/// <para>
/// The challenge (401) is left to the bearer handler, which already carries the reason the token
/// was refused in <c>WWW-Authenticate</c> (see <c>AuthenticationExtensions</c>).
/// </para>
/// </remarks>
internal sealed class ForbiddenProblemHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Forbidden || context.Response.HasStarted)
        {
            await fallback.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        // Which requirement said no, by its type name, so a client developer reading the problem knows
        // whether it was the role, the branch, the principal kind or the participant's standing. The
        // contract lists `requirement` as an optional string on `forbidden` for exactly this.
        var requirement = authorizeResult.AuthorizationFailure?.FailedRequirements
            .Select(r => r.GetType().Name)
            .FirstOrDefault();

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = Filters.ErrorResponsesOperationFilter.ProblemMediaType;

        await context.Response.WriteAsJsonAsync(
            new UnifiedErrorEnvelope
            {
                Type = ErrorCodes.TypeFor(ErrorCodes.Forbidden),
                Title = ErrorCodes.TitleFor(ErrorCodes.Forbidden),
                Status = StatusCodes.Status403Forbidden,
                Detail = "You are not allowed to perform this action.",
                Instance = context.Request.Path.Value,
                Code = ErrorCodes.Forbidden,
                TraceId = context.TraceIdentifier,
                Context = requirement is null
                    ? null
                    : new Dictionary<string, object?> { ["requirement"] = requirement },
            },
            options: null,
            contentType: Filters.ErrorResponsesOperationFilter.ProblemMediaType,
            context.RequestAborted);
    }
}
