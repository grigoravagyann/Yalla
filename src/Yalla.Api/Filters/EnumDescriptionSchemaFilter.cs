using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Yalla.Api.Filters;

/// <summary>
/// Names the members of every integer enum in the document.
/// </summary>
/// <remarks>
/// <para>
/// Enums travel as integers, because that is what the database stores and because a renamed
/// member must not orphan existing rows. The cost is that an untreated schema says
/// <c>enum: [1, 2, 4, 5]</c> and nothing else, and the frontend ends up with a magic number in a
/// comparison and a comment guessing at what it means.
/// </para>
/// <para>
/// This filter fixes that twice over: <c>x-enum-varnames</c>, which <c>openapi-typescript</c> and
/// most other generators read to emit named members, and a written-out list in the description
/// for anyone reading the document by eye. Values that are deliberately absent - <c>3</c> in both
/// <c>TableStatus</c> and <c>ReservationStatus</c>, retired rather than reused - simply do not
/// appear, which is itself worth seeing.
/// </para>
/// </remarks>
internal sealed class EnumDescriptionSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (!context.Type.IsEnum || schema.Enum.Count == 0)
        {
            return;
        }

        var names = Enum.GetNames(context.Type);
        var members = names
            .Select(name => $"{Convert.ToInt64(Enum.Parse(context.Type, name))} {name}")
            .ToArray();

        schema.Extensions["x-enum-varnames"] = new OpenApiArray();

        foreach (var name in names)
        {
            ((OpenApiArray)schema.Extensions["x-enum-varnames"]).Add(new OpenApiString(name));
        }

        var legend = "Values: " + string.Join(", ", members) + ".";

        schema.Description = string.IsNullOrWhiteSpace(schema.Description)
            ? legend
            : $"{schema.Description}\n\n{legend}";
    }
}
