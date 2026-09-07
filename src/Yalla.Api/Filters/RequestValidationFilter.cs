using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Yalla.Domain;

namespace Yalla.Api.Filters;

/// <summary>
/// Enforces the <c>DataAnnotations</c> on a request body, in the API's own error shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>These attributes reached the OpenAPI schema and nothing enforced them.</b> Forty
/// <c>[Required]</c>, <c>[StringLength]</c>, <c>[EmailAddress]</c>, <c>[Range]</c> and
/// <c>[MaxLength]</c> declarations across the request records were a promise to every generated
/// client and every gateway schema check, and the server kept none of them. A client's schema check
/// found that before the server ever did, which is the wrong way round.
/// </para>
/// <para>
/// What that cost was not a missing refusal - the domain guards catch most of it - but a refusal
/// that <b>described the wrong problem</b>. A tab opened without a <c>qrToken</c> answered
/// <i>404, that QR code does not belong to a table in service</i>, telling a diner their code was
/// bad when they had sent none. A join with no <c>joinToken</c> answered <i>401, that invitation is
/// no longer valid</i>. A PIN below the declared minimum answered a bare 401 with no body at all.
/// In each case a required string went missing, bound to null, and travelled down to a lookup that
/// answered about the thing it failed to find.
/// </para>
/// <para>
/// <b>Why a stock validation filter would have done nothing.</b> Every request here is a positional
/// record, and an attribute written on a positional parameter binds to the <i>parameter</i>, not to
/// the generated property, whenever the attribute permits both - which <see cref="RequiredAttribute"/>
/// and its siblings all do. <c>Validator.TryValidateObject</c> reads properties, so it finds an
/// unannotated type and passes everything. Writing <c>[property: Required]</c> on all forty would
/// also fix it, and would need every future author to remember - which is the failure mode this
/// exists to remove. So the rules are read from wherever the author put them.
/// </para>
/// <para>
/// <b>422 and every violation at once</b>, through <see cref="FieldValidationException"/> - the
/// rule <c>docs/error-contract.md</c> already states: a refusal that can collect is a 422 carrying
/// <c>context.fields</c>, and the 400s are the single-field guard refusals that cannot. One error
/// shape, so a client parses one.
/// </para>
/// <para>
/// <b>What this deliberately does not change.</b> <c>ClientCommandIdFilter</c> keeps its 400: a
/// published single-purpose refusal whose message is about idempotency, which no schema attribute
/// could express. And <c>[Required]</c> on a non-nullable value type - <c>Guid</c>,
/// <c>DateOnly</c>, an enum - still cannot fire, because a missing one binds its default rather
/// than null and <see cref="RequiredAttribute"/> only rejects null. Those attributes remain correct
/// as <i>schema</i>: the field genuinely is required and the gateway check reads that. They are
/// enforced further down, by the guards that know <c>Guid.Empty</c> and <c>(SettlementMode)0</c>
/// are not real values.
/// </para>
/// </remarks>
internal sealed class RequestValidationFilter : IEndpointFilter
{
    /// <summary>
    /// The rules for a type, worked out once.
    /// </summary>
    /// <remarks>
    /// The alternative - reflecting on every argument of every request - would walk the services,
    /// the <c>HttpContext</c> and the <c>CancellationToken</c> each time to discover they have no
    /// rules. This asks once and remembers, so the steady-state cost is a dictionary lookup per
    /// argument and nothing at all for the arguments that are not request bodies.
    /// </remarks>
    private static readonly ConcurrentDictionary<Type, Rule[]> Rules = new();

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        List<FieldViolation>? violations = null;

        foreach (var argument in context.Arguments)
        {
            if (argument is null)
            {
                continue;
            }

            foreach (var rule in Rules.GetOrAdd(argument.GetType(), BuildRules))
            {
                var value = rule.Property.GetValue(argument);

                foreach (var attribute in rule.Attributes)
                {
                    if (attribute.IsValid(value))
                    {
                        continue;
                    }

                    violations ??= [];
                    violations.Add(new FieldViolation(
                        rule.WireName,
                        attribute.FormatErrorMessage(rule.WireName),
                        FieldBounds.Required));
                }
            }
        }

        if (violations is { Count: > 0 })
        {
            throw new FieldValidationException(violations);
        }

        return await next(context);
    }

    /// <summary>One property, its wire name, and the attributes that must hold for it.</summary>
    private sealed record Rule(string WireName, PropertyInfo Property, ValidationAttribute[] Attributes);

    /// <summary>
    /// Collects a type's validation attributes from the properties <b>and</b> from the primary
    /// constructor's parameters, which is where a positional record actually puts them.
    /// </summary>
    /// <remarks>
    /// Framework and service types fall out with an empty array on their first request and are
    /// never reflected on again.
    /// </remarks>
    private static Rule[] BuildRules(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type.Namespace?.StartsWith(
                "Microsoft.AspNetCore", StringComparison.Ordinal) == true)
        {
            return [];
        }

        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        var byProperty = properties.Values.Select(p => (
            Property: p,
            Attributes: p.GetCustomAttributes<ValidationAttribute>(inherit: true).ToArray()));

        // The positional-record case. Matched by name because that is exactly how the compiler
        // pairs a primary-constructor parameter with the property it generates.
        var byParameter = type
            .GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .Take(1)
            .SelectMany(c => c.GetParameters())
            .Where(p => p.Name is not null && properties.ContainsKey(p.Name))
            .Select(p => (
                Property: properties[p.Name!],
                Attributes: p.GetCustomAttributes<ValidationAttribute>(inherit: true).ToArray()));

        return byProperty
            .Concat(byParameter)
            .Where(x => x.Attributes.Length > 0)
            .GroupBy(x => x.Property.Name, StringComparer.Ordinal)
            .Select(g => new Rule(

                // The wire name, because the mapper drops a field name that is not camelCase - a
                // capitalised one is a label, not something a form can key on.
                JsonNamingPolicy.CamelCase.ConvertName(g.Key),
                g.First().Property,

                // An attribute could be written on both the parameter and the property; running it
                // twice would report one problem twice.
                [.. g.SelectMany(x => x.Attributes).DistinctBy(a => a.GetType())]))
            .ToArray();
    }
}
