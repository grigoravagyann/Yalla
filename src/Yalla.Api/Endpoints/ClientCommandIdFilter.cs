namespace Yalla.Api.Endpoints;

/// <summary>
/// Rejects a state-change request that arrives without an idempotency key.
/// </summary>
/// <remarks>
/// Applied once to the whole group rather than repeated in each handler. Without a
/// <c>clientCommandId</c> the server cannot tell a replayed offline command from a genuine second
/// request, so accepting one and hoping would mean double-seating a table on the first flaky
/// wifi - which is the exact failure this whole mechanism exists to prevent. Better to refuse the
/// request than to guess.
/// </remarks>
internal sealed class ClientCommandIdFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is IClientCommandRequest { ClientCommandId: var id } && id == Guid.Empty)
            {
                throw new ArgumentException(
                    "clientCommandId is required. Generate one per command and reuse it when "
                    + "retrying, so a replay is recognised instead of applied twice.",
                    "clientCommandId");
            }
        }

        return await next(context);
    }
}
