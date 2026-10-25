using System.Reflection;
using System.Runtime.CompilerServices;
using Yalla.Api.Errors;
using Yalla.Application.Abstractions;
using Yalla.Domain.Common;
using Yalla.Infrastructure;

namespace Yalla.UnitTests.Architecture;

/// <summary>
/// The four production assemblies as the architecture tests read them: by reflection over what is loaded, no package.
/// </summary>
internal static class ProductionCode
{
    public static Assembly Domain { get; } = typeof(IDomainEvent).Assembly;

    public static Assembly Application { get; } = typeof(IDomainEventHandler<>).Assembly;

    public static Assembly Infrastructure { get; } = typeof(DependencyInjection).Assembly;

    public static Assembly Api { get; } = typeof(ErrorCodes).Assembly;

    public static IReadOnlyList<Assembly> All { get; } = [Domain, Application, Infrastructure, Api];

    /// <summary>Every type in the assembly, nested ones included.</summary>
    public static IEnumerable<Type> TypesOf(Assembly assembly) => assembly.GetTypes();

    /// <summary>Every type in every production assembly, nested and compiler-generated ones included.</summary>
    public static IEnumerable<Type> AllTypes() => All.SelectMany(TypesOf);

    /// <summary>Types a person wrote: not the compiler's state machines, closures and anonymous types.</summary>
    public static bool IsWritten(Type type) =>
        !type.Name.Contains('<', StringComparison.Ordinal)
        && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && (type.DeclaringType is null || IsWritten(type.DeclaringType));

    /// <summary>Whether the assembly is one of ours.</summary>
    public static bool IsYalla(Assembly assembly) =>
        assembly.GetName().Name?.StartsWith("Yalla.", StringComparison.Ordinal) == true;

    /// <summary>The type's name as a reader finds it in the code: namespace-qualified, nested with a dot.</summary>
    public static string NameOf(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var bare = (definition.FullName ?? definition.Name).Split('`')[0].Replace('+', '.');

            return $"{bare}<{string.Join(", ", type.GetGenericArguments().Select(NameOf))}>";
        }

        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    /// <summary>A member as a reader finds it: <c>Type.Member</c>.</summary>
    public static string NameOf(MemberInfo member) =>
        member.DeclaringType is { } owner ? $"{NameOf(owner)}.{member.Name}" : member.Name;

    /// <summary>
    /// The failure message every architecture test writes: the rule, then each offender on its own line, then what to
    /// do instead and where the rule is written down.
    /// </summary>
    public static string Violation(string rule, IEnumerable<string> offenders, string instead) =>
        $"{rule}\n\nOffending:\n  - {string.Join("\n  - ", offenders.Order(StringComparer.Ordinal))}\n\nInstead: {instead}";
}
