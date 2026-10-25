using System.Runtime.CompilerServices;

namespace Yalla.Api.Errors;

/// <summary>
/// Declares a constant in <see cref="ErrorCodes"/> as a problem code: the HTTP status it always answers
/// with, a one-line meaning, and - with <see cref="ProblemContextAttribute"/> beside it - the
/// <c>context</c> it carries.
/// </summary>
/// <remarks>
/// <para>
/// This is the single declaration of a code. <see cref="ProblemCatalogue"/> reads it, and from there
/// <see cref="ApiExceptionMapper"/> takes the status, <see cref="ErrorCodes.TitleFor"/> the title, the
/// OpenAPI document its <c>x-problem-codes</c> extension (which the frontend generates its code
/// constants and context types from), and <c>docs/error-contract.md</c> its catalogue table.
/// </para>
/// <para>
/// One code, one status: a code that needed two statuses would be two codes.
/// </para>
/// </remarks>
/// <param name="status">The HTTP status every answer with this code carries.</param>
/// <param name="meaning">What it means, in one line, for the catalogue and the generated client.</param>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ProblemAttribute(int status, string meaning) : Attribute
{
    /// <summary>The HTTP status every answer with this code carries.</summary>
    public int Status { get; } = status;

    /// <summary>What the code means, in one line.</summary>
    public string Meaning { get; } = meaning;

    /// <summary>
    /// The RFC 7807 <c>title</c>. Omitted, the slug is humanised: <c>pin-invalid</c> becomes "Pin invalid".
    /// </summary>
    public string? Title { get; init; }
}

/// <summary>One member of a problem code's <c>context</c>, declared beside its <see cref="ProblemAttribute"/>.</summary>
/// <remarks>
/// <para>
/// <see cref="Type"/> is the member's CLR type as it goes on the wire: an enum is its integer, a
/// <c>DateTime</c> an ISO instant, a <c>DateOnly</c> a <c>yyyy-MM-dd</c> date, an array its items, a
/// record its properties. <c>typeof(Guid?)</c> and friends say the value can be <c>null</c>; for a
/// reference type that can be, set <see cref="Nullable"/>.
/// </para>
/// <para>
/// <b>Required</b> (the default) means present whenever <c>context</c> is. Some codes answer without
/// a context at all on some routes - <c>not-found</c> for a missing row, say, and with one when a
/// staff order named a guest - and that is still a required member: <see cref="Optional"/> is for a
/// member that is left out of a context that is there.
/// </para>
/// <para>
/// Members are kept in the order they are written, which is the order the catalogue lists them.
/// </para>
/// </remarks>
/// <param name="name">The member's name on the wire, camelCase.</param>
/// <param name="type">Its CLR type.</param>
/// <param name="line">Filled in by the compiler; keeps the declared order.</param>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public sealed class ProblemContextAttribute(string name, Type type, [CallerLineNumber] int line = 0) : Attribute
{
    /// <summary>The member's name on the wire.</summary>
    public string Name { get; } = name;

    /// <summary>Its CLR type, as it is serialised.</summary>
    public Type Type { get; } = type;

    /// <summary>Where it was declared, so the catalogue keeps the written order.</summary>
    public int Line { get; } = line;

    /// <summary>Left out of some contexts that are there - only when it has a value, say.</summary>
    public bool Optional { get; init; }

    /// <summary>The value can be <c>null</c>. Implied by a <c>Nullable&lt;T&gt;</c> <see cref="Type"/>.</summary>
    public bool Nullable { get; init; }
}
