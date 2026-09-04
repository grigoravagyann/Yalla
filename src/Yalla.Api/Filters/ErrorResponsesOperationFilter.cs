using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Yalla.Api.Errors;

namespace Yalla.Api.Filters;

/// <summary>
/// Documents the problem-document responses every operation can produce.
/// </summary>
/// <remarks>
/// <para>
/// These are the failures the pipeline can raise for any endpoint - a malformed body, an expired
/// token, a rate limit, an unhandled fault - so writing them out per endpoint would be eight
/// lines of noise on every route and one endpoint that forgot.
/// </para>
/// <para>
/// Responses an endpoint declared itself are never overwritten. The ones that matter most are
/// declared explicitly: the 409 a table endpoint returns when someone else took the table, and
/// the 422 for an impossible transition. Those carry endpoint-specific descriptions because they
/// are normal outcomes the frontend has to handle, not generic errors.
/// </para>
/// </remarks>
internal sealed class ErrorResponsesOperationFilter : IOperationFilter
{
    /// <summary>The media type every failure in this API is served as.</summary>
    public const string ProblemMediaType = "application/problem+json";

    private static readonly (string Status, string Description)[] Responses =
    [
        ("400", "The request violated a domain rule or arrived malformed."),
        ("401", "No usable token was presented, or the one presented was rejected."),
        ("403", "The caller is authenticated but not allowed to perform this action."),
        ("429", "Too many requests in the window, or a one-time credential is out of attempts."),
        ("500", "Unexpected failure. Quote the traceId from the body."),
    ];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var schema = context.SchemaGenerator.GenerateSchema(
            typeof(UnifiedErrorEnvelope), context.SchemaRepository);

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
                    [ProblemMediaType] = new() { Schema = schema },
                },
            };
        }
    }
}
