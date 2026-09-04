using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Yalla.Api.Errors;

namespace Yalla.Api.Filters;

/// <summary>
/// Documents the unified failure envelope on every operation, so a generated client knows the
/// error shape without anyone having to write it down per endpoint.
/// </summary>
internal sealed class ErrorResponsesOperationFilter : IOperationFilter
{
    private static readonly (string Status, string Description)[] Responses =
    [
        ("400", "The request violated a domain rule or arrived malformed."),
        ("403", "The caller is not allowed to perform this action."),
        ("404", "The addressed record does not exist."),
        ("409", "The operation conflicts with the current state, or another writer won the race."),
        ("429", "Too many requests in the window."),
        ("500", "Unexpected failure. Quote the traceId from the body."),
    ];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var schema = context.SchemaGenerator.GenerateSchema(typeof(UnifiedErrorEnvelope), context.SchemaRepository);

        foreach (var (status, description) in Responses)
        {
            // Never overwrite a response the endpoint documented itself.
            if (operation.Responses.ContainsKey(status))
            {
                continue;
            }

            operation.Responses[status] = new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = schema },
                },
            };
        }
    }
}
