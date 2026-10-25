using System.Reflection;

namespace Yalla.Api.Errors;

/// <summary>One member of a problem code's <c>context</c>.</summary>
/// <param name="Name">Its name on the wire.</param>
/// <param name="Type">Its CLR type, as serialised (see <see cref="ProblemContextAttribute"/>).</param>
/// <param name="Required">Present whenever the context is.</param>
/// <param name="Nullable">The value can be <c>null</c>.</param>
public sealed record ProblemContextField(string Name, Type Type, bool Required, bool Nullable);

/// <summary>A problem code as <see cref="ProblemAttribute"/> declares it.</summary>
/// <param name="Name">The <see cref="ErrorCodes"/> constant's name, e.g. <c>TakeawayBusy</c>.</param>
/// <param name="Code">The slug on the wire, e.g. <c>takeaway-busy</c>.</param>
/// <param name="Status">The HTTP status it always answers with.</param>
/// <param name="Title">The RFC 7807 <c>title</c>.</param>
/// <param name="Meaning">What it means, in one line.</param>
/// <param name="Context">The members its <c>context</c> may carry; empty when it carries none.</param>
public sealed record ProblemDefinition(
    string Name,
    string Code,
    int Status,
    string Title,
    string Meaning,
    IReadOnlyList<ProblemContextField> Context);

/// <summary>
/// Every problem code this API answers with, read from the <see cref="ProblemAttribute"/> on each
/// <see cref="ErrorCodes"/> constant.
/// </summary>
/// <remarks>
/// The one place a code's status, title, meaning and context are written down. The exception mapper
/// takes its statuses from here, the OpenAPI document publishes it as <c>x-problem-codes</c>, and
/// <c>ProblemCatalogueTests</c> holds the rest of the codebase to it: every code thrown anywhere is in
/// it, every code in it reaches the wire, each exception maps to its code's status with no context
/// member the catalogue does not declare, and the table in <c>docs/error-contract.md</c> is generated
/// from it.
/// </remarks>
public static class ProblemCatalogue
{
    private static readonly IReadOnlyDictionary<string, ProblemDefinition> ByCode = Build();

    /// <summary>Every code, in ordinal order of the slug.</summary>
    public static IReadOnlyList<ProblemDefinition> All { get; } =
        ByCode.Values.OrderBy(definition => definition.Code, StringComparer.Ordinal).ToArray();

    /// <summary>The code's definition, or <c>null</c> when it is not a catalogued code (or is null).</summary>
    public static ProblemDefinition? Find(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var definition) ? definition : null;

    /// <summary>The code's definition.</summary>
    /// <exception cref="KeyNotFoundException">The code is not in the catalogue.</exception>
    public static ProblemDefinition Get(string code) =>
        Find(code) ?? throw new KeyNotFoundException(
            $"'{code}' is not a catalogued problem code. Declare it in ErrorCodes with [Problem].");

    /// <summary>A short name for a context member's type, as the catalogue table shows it.</summary>
    public static string TypeLabel(Type type)
    {
        var underlying = System.Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            return "string";
        }

        if (underlying == typeof(Guid))
        {
            return "uuid";
        }

        if (underlying == typeof(int))
        {
            return "int32";
        }

        if (underlying == typeof(long))
        {
            return "int64";
        }

        if (underlying == typeof(bool))
        {
            return "boolean";
        }

        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
        {
            return "date-time";
        }

        if (underlying == typeof(DateOnly))
        {
            return "date";
        }

        if (underlying == typeof(TimeOnly))
        {
            return "time";
        }

        if (underlying == typeof(object))
        {
            return "any";
        }

        if (ElementTypeOf(underlying) is { } element)
        {
            return TypeLabel(element) + "[]";
        }

        return underlying.Name;
    }

    /// <summary>The item type of an array or a sequence, or <c>null</c> for anything else.</summary>
    public static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type.IsGenericType && type.GetGenericArguments().Length == 1
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
                ? type.GetGenericArguments()[0]
                : null;
    }

    private static Dictionary<string, ProblemDefinition> Build()
    {
        var definitions = new Dictionary<string, ProblemDefinition>(StringComparer.Ordinal);

        foreach (var field in typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral
                || field.GetRawConstantValue() is not string code
                || field.GetCustomAttribute<ProblemAttribute>() is not { } problem)
            {
                continue;
            }

            var context = field.GetCustomAttributes<ProblemContextAttribute>()
                .OrderBy(member => member.Line)
                .Select(member => new ProblemContextField(
                    member.Name,
                    member.Type,
                    Required: !member.Optional,
                    Nullable: member.Nullable || System.Nullable.GetUnderlyingType(member.Type) is not null))
                .ToArray();

            if (!definitions.TryAdd(
                    code,
                    new ProblemDefinition(
                        field.Name,
                        code,
                        problem.Status,
                        problem.Title ?? ErrorCodes.Humanise(code),
                        problem.Meaning,
                        context)))
            {
                throw new InvalidOperationException($"Problem code '{code}' is declared twice in ErrorCodes.");
            }
        }

        return definitions;
    }
}
