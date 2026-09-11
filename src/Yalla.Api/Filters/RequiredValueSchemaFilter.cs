using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Yalla.Api.Filters;

/// <summary>
/// Publishes a <c>[Required]</c> nullable value - <c>DateOnly?</c>, <c>TimeOnly?</c>, a nullable
/// enum - as required and not nullable.
/// </summary>
/// <remarks>
/// <para>
/// Those members are nullable in C# for one reason: a missing <c>DateOnly</c> binds year one and a
/// missing <c>TimeOnly</c> binds midnight, so only a null lets <c>[Required]</c> tell "left out"
/// from "meant". On the wire they are required, and a request without them is refused.
/// </para>
/// <para>
/// Swashbuckle saw the <c>?</c> and published them as optional and nullable, and it does not read
/// <c>[Required]</c> off a positional record parameter at all. So a generated client could build a
/// booking with no date and no time, satisfy its type, and learn otherwise at runtime - which is the
/// one thing generating the client is meant to prevent.
/// </para>
/// </remarks>
internal sealed class RequiredValueSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Properties is not { Count: > 0 })
        {
            return;
        }

        foreach (var name in RequiredNullableValues(context.Type))
        {
            // The document is camelCase and the member is PascalCase; the names are otherwise one.
            var key = schema.Properties.Keys.FirstOrDefault(
                k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

            if (key is null)
            {
                continue;
            }

            schema.Required.Add(key);
            schema.Properties[key].Nullable = false;
        }
    }

    private static IEnumerable<string> RequiredNullableValues(Type type)
    {
        // Positional record parameters, which is where these requests put their attributes.
        foreach (var constructor in type.GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                if (parameter.Name is { } name
                    && IsNullableValue(parameter.ParameterType)
                    && parameter.GetCustomAttribute<RequiredAttribute>() is not null)
                {
                    yield return name;
                }
            }
        }

        // And properties, for a type that declares them there instead.
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsNullableValue(property.PropertyType) && property.GetCustomAttribute<RequiredAttribute>() is not null)
            {
                yield return property.Name;
            }
        }
    }

    private static bool IsNullableValue(Type type) => Nullable.GetUnderlyingType(type) is not null;
}
