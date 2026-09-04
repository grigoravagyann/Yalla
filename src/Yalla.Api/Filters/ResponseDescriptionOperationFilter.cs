using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Yalla.Api.Endpoints;

namespace Yalla.Api.Filters;

/// <summary>
/// Applies the per-endpoint response descriptions attached by
/// <see cref="EndpointConventions.ProducesProblemDetails"/>.
/// </summary>
/// <remarks>
/// <c>Produces</c> declares a status code and a schema but has no argument for prose, so the
/// generated document says "Error" against every failure. That is exactly the wrong thing for the
/// two responses that matter here - the 409 for "someone just took that table" and the 422 for an
/// impossible transition are normal outcomes the frontend handles, not errors, and the document
/// has to say so.
/// </remarks>
internal sealed class ResponseDescriptionOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var descriptions = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<ResponseDescriptionAttribute>();

        foreach (var described in descriptions)
        {
            var key = described.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (operation.Responses.TryGetValue(key, out var response))
            {
                response.Description = described.Description;
            }
        }
    }
}
