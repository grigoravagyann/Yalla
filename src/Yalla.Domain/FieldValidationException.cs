namespace Yalla.Domain;

/// <summary>
/// One field of a request that was refused, with the bound it broke and what was sent.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Field"/> is the wire name, not the C# name.</b> It is what the client's form is
/// keyed on, so it has to match the property in the OpenAPI schema exactly - camel-cased, and
/// indexed where the payload is an array (<c>[2].closesAt</c>). A refusal keyed on anything else -
/// an English label, a display name - is a refusal a localised form cannot place against an input,
/// which is the whole reason this type exists.
/// </para>
/// <para>
/// <see cref="Min"/>, <see cref="Max"/> and <see cref="Value"/> are boxed rather than typed: a
/// bound is an <c>int</c> for minutes, a <c>decimal</c> for a percentage and a <c>TimeOnly</c> for
/// an opening time, and the client renders whichever it is. <see cref="Bound"/> says which of them
/// is meaningful.
/// </para>
/// </remarks>
/// <param name="Field">The offending property, in the casing the OpenAPI schema uses.</param>
/// <param name="Message">What is wrong, in a sentence safe to show a developer.</param>
/// <param name="Bound">Which rule broke: see <see cref="FieldBounds"/>.</param>
/// <param name="Min">The lowest accepted value, when there is one.</param>
/// <param name="Max">The highest accepted value, when there is one.</param>
/// <param name="Value">What was actually supplied.</param>
public sealed record FieldViolation(
    string Field,
    string Message,
    string? Bound = null,
    object? Min = null,
    object? Max = null,
    object? Value = null);

/// <summary>The vocabulary <see cref="FieldViolation.Bound"/> uses. Stable; clients branch on it.</summary>
public static class FieldBounds
{
    /// <summary>Below <see cref="FieldViolation.Min"/>.</summary>
    public const string Min = "min";

    /// <summary>Above <see cref="FieldViolation.Max"/>.</summary>
    public const string Max = "max";

    /// <summary>Outside <see cref="FieldViolation.Min"/>..<see cref="FieldViolation.Max"/>.</summary>
    public const string Range = "range";

    /// <summary>Missing, blank, or unparseable.</summary>
    public const string Required = "required";

    /// <summary>Well-formed, but it clashes with something else in the same payload or the branch.</summary>
    public const string Conflict = "conflict";
}

/// <summary>
/// A refusal that names the fields it is about, rather than only describing them in prose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every violation in the request, not the first.</b> A form that surfaces one error at a time
/// makes an owner submit six times to find out their reservation policy has six problems, so the
/// rules that produce these collect and throw once.
/// </para>
/// <para>
/// Derives from <see cref="ArgumentException"/> so that the many call sites already catching one
/// keep working, but the API mapper matches it first and answers 422 <c>validation-failed</c> with
/// the fields in <c>errors</c> and in <c>context</c> - not the 400 with a bare sentence that an
/// <see cref="ArgumentOutOfRangeException"/> gets.
/// </para>
/// </remarks>
public sealed class FieldValidationException : ArgumentException
{
    public FieldValidationException(IReadOnlyList<FieldViolation> violations)
        : base(Describe(violations))
    {
        ArgumentNullException.ThrowIfNull(violations);

        if (violations.Count == 0)
        {
            throw new ArgumentException(
                "A field validation failure must name at least one field.", nameof(violations));
        }

        Violations = violations;
    }

    public FieldValidationException(FieldViolation violation)
        : this([violation])
    {
    }

    /// <summary>Every field that was refused, in the order the rules checked them.</summary>
    public IReadOnlyList<FieldViolation> Violations { get; }

    /// <summary>
    /// The first offending field, for a client that can only highlight one.
    /// </summary>
    /// <remarks>
    /// This is what lands in <c>context.field</c>. The full list is in <c>context.fields</c>, and
    /// a form that can show several should read that instead.
    /// </remarks>
    public string Field => Violations[0].Field;

    /// <summary>Field-level complaints, in the shape RFC 7807's <c>errors</c> member wants.</summary>
    public IReadOnlyDictionary<string, string[]> AsErrorMap() =>
        Violations
            .GroupBy(v => v.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(v => v.Message).ToArray(), StringComparer.Ordinal);

    private static string Describe(IReadOnlyList<FieldViolation>? violations) =>
        violations is null || violations.Count == 0
            ? "The request was refused."
            : string.Join(" ", violations.Select(v => v.Message));
}
