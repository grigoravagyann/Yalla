using System.Reflection;
using System.Text.Json;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Yalla.Api.Errors;

namespace Yalla.Api.Filters;

/// <summary>
/// Publishes the <see cref="ProblemCatalogue"/> as the document's <c>x-problem-codes</c> extension.
/// </summary>
/// <remarks>
/// <para>
/// Shape, at the document root:
/// <c>"x-problem-codes": { "takeaway-busy": { "name": "TakeawayBusy", "status": 409, "title": "Kitchen busy",
/// "description": "...", "context": { "type": "object", "properties": { ... }, "required": [ ... ] } } }</c>,
/// in ordinal order of the slug. <c>context</c> is absent for a code that carries none; it is a plain
/// JSON schema otherwise - enums inline as integers with <c>x-enum-varnames</c> and <c>x-enum-name</c>,
/// a type the document already has as a component as a <c>$ref</c> to it.
/// </para>
/// <para>
/// The frontend generates its code constants, statuses and typed context interfaces from this
/// (<c>packages/api/src/generated/problemCodes.ts</c>), so the codes are written once, in
/// <see cref="ErrorCodes"/>. An extension rather than component schemas, so the rest of the
/// document - and every type generated from it - is untouched.
/// </para>
/// </remarks>
internal sealed class ProblemCodesDocumentFilter : IDocumentFilter
{
    /// <summary>The extension's name at the document root.</summary>
    public const string ExtensionName = "x-problem-codes";

    private static readonly NullabilityInfoContext Nullability = new();

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var components = swaggerDoc.Components?.Schemas ?? new Dictionary<string, OpenApiSchema>();
        var codes = new OpenApiObject();

        foreach (var definition in ProblemCatalogue.All)
        {
            var entry = new OpenApiObject
            {
                ["name"] = new OpenApiString(definition.Name),
                ["status"] = new OpenApiInteger(definition.Status),
                ["title"] = new OpenApiString(definition.Title),
                ["description"] = new OpenApiString(definition.Meaning),
            };

            if (definition.Context.Count > 0)
            {
                var properties = new OpenApiObject();
                var required = new OpenApiArray();

                foreach (var field in definition.Context)
                {
                    properties[field.Name] = SchemaFor(field.Type, field.Nullable, components);

                    if (field.Required)
                    {
                        required.Add(new OpenApiString(field.Name));
                    }
                }

                var schema = new OpenApiObject
                {
                    ["type"] = new OpenApiString("object"),
                    ["properties"] = properties,
                };

                if (required.Count > 0)
                {
                    schema["required"] = required;
                }

                schema["additionalProperties"] = new OpenApiBoolean(false);
                entry["context"] = schema;
            }

            codes[definition.Code] = entry;
        }

        swaggerDoc.Extensions[ExtensionName] = codes;
    }

    /// <summary>A JSON schema for one value as it goes on the wire.</summary>
    private static OpenApiObject SchemaFor(Type type, bool nullable, IDictionary<string, OpenApiSchema> components)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var schema = new OpenApiObject();

        if (underlying == typeof(string))
        {
            schema["type"] = new OpenApiString("string");
        }
        else if (underlying == typeof(Guid))
        {
            schema["type"] = new OpenApiString("string");
            schema["format"] = new OpenApiString("uuid");
        }
        else if (underlying == typeof(int))
        {
            schema["type"] = new OpenApiString("integer");
            schema["format"] = new OpenApiString("int32");
        }
        else if (underlying == typeof(long))
        {
            schema["type"] = new OpenApiString("integer");
            schema["format"] = new OpenApiString("int64");
        }
        else if (underlying == typeof(bool))
        {
            schema["type"] = new OpenApiString("boolean");
        }
        else if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
        {
            schema["type"] = new OpenApiString("string");
            schema["format"] = new OpenApiString("date-time");
        }
        else if (underlying == typeof(DateOnly))
        {
            schema["type"] = new OpenApiString("string");
            schema["format"] = new OpenApiString("date");
        }
        else if (underlying == typeof(TimeOnly))
        {
            schema["type"] = new OpenApiString("string");
            schema["format"] = new OpenApiString("time");
        }
        else if (underlying.IsEnum)
        {
            var values = new OpenApiArray();
            var names = new OpenApiArray();

            foreach (var name in Enum.GetNames(underlying))
            {
                values.Add(new OpenApiInteger(Convert.ToInt32(Enum.Parse(underlying, name), System.Globalization.CultureInfo.InvariantCulture)));
                names.Add(new OpenApiString(name));
            }

            schema["type"] = new OpenApiString("integer");
            schema["format"] = new OpenApiString("int32");
            schema["enum"] = values;
            schema["x-enum-varnames"] = names;
            schema["x-enum-name"] = new OpenApiString(underlying.Name);
        }
        else if (ProblemCatalogue.ElementTypeOf(underlying) is { } element)
        {
            schema["type"] = new OpenApiString("array");
            schema["items"] = SchemaFor(element, nullable: false, components);
        }
        else if (underlying == typeof(object))
        {
            // Any JSON value: the argument a guard refused, whatever its type.
        }
        else if (components.ContainsKey(SchemaIdOf(underlying)))
        {
            schema["$ref"] = new OpenApiString("#/components/schemas/" + SchemaIdOf(underlying));
        }
        else
        {
            var properties = new OpenApiObject();
            var required = new OpenApiArray();

            foreach (var property in underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                var propertyNullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
                    || Nullability.Create(property).ReadState == NullabilityState.Nullable;

                properties[name] = SchemaFor(property.PropertyType, propertyNullable, components);

                // Null members of a nested object are left out (WhenWritingNull), so only the
                // non-nullable ones are always there.
                if (!propertyNullable)
                {
                    required.Add(new OpenApiString(name));
                }
            }

            schema["type"] = new OpenApiString("object");
            schema["properties"] = properties;

            if (required.Count > 0)
            {
                schema["required"] = required;
            }
        }

        if (nullable)
        {
            schema["nullable"] = new OpenApiBoolean(true);
        }

        return schema;
    }

    /// <summary>The component id <c>SwaggerExtensions</c> gives a type: its full name, nested types dotted.</summary>
    private static string SchemaIdOf(Type type) => type.FullName?.Replace('+', '.') ?? type.Name;
}
