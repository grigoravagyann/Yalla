using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Yalla.Api.Errors;

namespace Yalla.Api.Filters;

/// <summary>
/// Declares that one status code on this endpoint answers with a particular problem shape.
/// </summary>
/// <remarks>
/// Metadata rather than a second <c>Produces</c>, because ApiExplorer keeps exactly one response
/// type per status code and the last one silently wins. An endpoint that can answer 409 in two
/// different ways would therefore document one of them and drop the other - which is worse than the
/// untyped bag it replaced, because it looks precise and is wrong.
/// </remarks>
/// <param name="statusCode">The status this shape describes.</param>
/// <param name="shape">A type from <c>ProblemShapes.cs</c>.</param>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
internal sealed class ProblemShapeAttribute(int statusCode, Type shape) : Attribute
{
    public int StatusCode { get; } = statusCode;

    public Type Shape { get; } = shape;
}

/// <summary>
/// Replaces each documented error response with its real shape, or with a <c>oneOf</c> when a
/// status code can arrive in more than one form.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <c>context</c> a type on the wire instead of
/// <c>Record&lt;string, unknown&gt;</c>. Prompt 3 argued a generated client is only worth having if
/// the schema is precise enough to catch a mistake at compile time; the error extensions were the
/// hole in that argument, and every named error a client branches on fell through it.
/// </para>
/// <para>
/// A <c>oneOf</c> rather than a merged type with everything optional: <c>code</c> is what a client
/// switches on, and a union lets TypeScript narrow the context off that switch. Flattening the
/// families into one object with a dozen optional fields would type-check and tell the reader
/// nothing about which fields arrive together.
/// </para>
/// </remarks>
internal sealed class ProblemShapesOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var declared = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<ProblemShapeAttribute>()
            .GroupBy(a => a.StatusCode);

        foreach (var group in declared)
        {
            var status = group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!operation.Responses.TryGetValue(status, out var response))
            {
                continue;
            }

            var schemas = group
                .Select(a => a.Shape)
                .Distinct()
                .Select(t => context.SchemaGenerator.GenerateSchema(t, context.SchemaRepository))
                .ToList();

            var schema = schemas.Count == 1
                ? schemas[0]
                : new OpenApiSchema { OneOf = schemas };

            response.Content[ErrorResponsesOperationFilter.ProblemMediaType] =
                new OpenApiMediaType { Schema = schema };
        }
    }
}
