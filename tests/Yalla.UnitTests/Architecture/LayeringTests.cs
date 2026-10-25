using System.Reflection;

namespace Yalla.UnitTests.Architecture;

/// <summary>
/// The layers of <c>AGENTS.md</c>, "Repo map": Domain is entities and rules with no I/O, Application is contracts over
/// Domain only, Infrastructure implements them, the Api hosts it all. Checked twice: through the assembly references
/// the compiler wrote, and through the types the non-private signatures name - so a failure says which member.
/// </summary>
public sealed class LayeringTests
{
    private const string Doc = "AGENTS.md, \"Repo map\" (and \"Architecture tests\")";

    private static readonly string[] Persistence =
        ["Microsoft.EntityFrameworkCore", "Microsoft.Data.SqlClient", "Microsoft.AspNetCore"];

    [Fact]
    public void Domain_references_no_other_Yalla_assembly_and_no_EF_Core_or_ASP_NET()
    {
        AssertReferencesNone(
            ProductionCode.Domain,
            name => name.StartsWith("Yalla.", StringComparison.Ordinal) || Persistence.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
            "Yalla.Domain is entities and rules with no I/O: it references no other Yalla assembly, no EF Core, no SqlClient and "
            + "no ASP.NET Core.",
            "keep the rule in Domain and take what it needs as plain values; put persistence in "
            + "Yalla.Infrastructure (Persistence/Configurations) and contracts in Yalla.Application.");
    }

    [Fact]
    public void Application_references_only_Domain_of_the_Yalla_assemblies_and_no_EF_Core_or_ASP_NET()
    {
        AssertReferencesNone(
            ProductionCode.Application,
            name => (name.StartsWith("Yalla.", StringComparison.Ordinal) && name != ProductionCode.Domain.GetName().Name)
                    || Persistence.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
            "Yalla.Application is contracts only - service interfaces, DTO records, application exceptions - over "
            + "Yalla.Domain: it references no other Yalla assembly, no EF Core, no SqlClient and no ASP.NET Core.",
            "declare the interface or record in Yalla.Application and implement it in Yalla.Infrastructure; a type the "
            + "contract needs from the Api or Infrastructure belongs in Application instead.");
    }

    [Fact]
    public void Infrastructure_does_not_reference_the_Api()
    {
        AssertReferencesNone(
            ProductionCode.Infrastructure,
            name => name == ProductionCode.Api.GetName().Name,
            "Yalla.Infrastructure implements the Application's contracts and never reaches up into Yalla.Api.",
            "move what both need (a contract, an exception the mapper reads) into Yalla.Application or Yalla.Domain; "
            + "problem codes stay in Api/Errors and are mapped from the exception there.");
    }

    private static void AssertReferencesNone(Assembly assembly, Func<string, bool> forbids, string rule, string instead)
    {
        bool forbidden(string name) => name != assembly.GetName().Name && forbids(name);

        var references = assembly.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(forbidden)
            .Select(name => $"{assembly.GetName().Name} references assembly {name}");

        var signatures = ProductionCode.TypesOf(assembly)
            .Where(ProductionCode.IsWritten)
            .SelectMany(SignatureUses)
            .Where(use => forbidden(use.Used.Assembly.GetName().Name!))
            .Select(use => $"{use.Where} uses {ProductionCode.NameOf(use.Used)} ({use.Used.Assembly.GetName().Name})")
            .Distinct();

        var offenders = references.Concat(signatures).ToList();

        Assert.True(offenders.Count == 0, ProductionCode.Violation(rule, offenders, $"{instead} See {Doc}."));
    }

    /// <summary>
    /// The types the type's public, internal and protected surface names: its base and interfaces, field, property and
    /// event types, and method and constructor signatures - generic arguments and element types unwrapped.
    /// </summary>
    private static IEnumerable<(string Where, Type Used)> SignatureUses(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                                 | BindingFlags.DeclaredOnly;
        var name = ProductionCode.NameOf(type);

        if (type.BaseType is { } baseType)
        {
            foreach (var used in Unwrap(baseType))
            {
                yield return ($"{name} (base type)", used);
            }
        }

        foreach (var used in type.GetInterfaces().SelectMany(Unwrap))
        {
            yield return ($"{name} (interface)", used);
        }

        foreach (var field in type.GetFields(all).Where(f => !f.IsPrivate))
        {
            foreach (var used in Unwrap(field.FieldType))
            {
                yield return (ProductionCode.NameOf(field), used);
            }
        }

        foreach (var property in type.GetProperties(all).Where(p => p.GetAccessors(nonPublic: true).Any(a => !a.IsPrivate)))
        {
            foreach (var used in Unwrap(property.PropertyType))
            {
                yield return (ProductionCode.NameOf(property), used);
            }
        }

        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all))
                     .Where(m => !m.IsPrivate && (m.IsConstructor || !m.IsSpecialName)))
        {
            var signature = method.GetParameters().Select(p => p.ParameterType);

            if (method is MethodInfo info)
            {
                signature = signature.Append(info.ReturnType);
            }

            foreach (var used in signature.SelectMany(Unwrap))
            {
                yield return (ProductionCode.NameOf(method), used);
            }
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var inner in Unwrap(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        if (type.IsGenericParameter)
        {
            yield break;
        }

        if (type.IsGenericType)
        {
            yield return type.GetGenericTypeDefinition();

            foreach (var inner in type.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type;
    }
}
