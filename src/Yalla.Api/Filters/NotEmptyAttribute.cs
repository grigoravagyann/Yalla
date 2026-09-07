using System.ComponentModel.DataAnnotations;

namespace Yalla.Api.Filters;

/// <summary>
/// Rejects the all-zeros <see cref="Guid"/> — which is what a missing one binds to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The half of "required" that <see cref="RequiredAttribute"/> cannot express.</b> A missing
/// non-nullable <c>Guid</c> does not arrive as null; model binding gives it <see cref="Guid.Empty"/>,
/// which <see cref="RequiredAttribute"/> accepts because it only rejects null. So a request that
/// omitted <c>branchId</c> used to reach the lookup and come back as
/// <i>404, Branch 00000000-0000-0000-0000-000000000000 was not found</i> — an answer about a branch
/// nobody asked for, quoting an internal sentinel at a diner.
/// </para>
/// <para>
/// <b>Why this rather than making the parameter <c>Guid?</c>.</b> Both would work, and nullability
/// is the only option for a <c>DateOnly</c> or a <c>TimeOnly</c>, where the default is a value a
/// caller could legitimately mean — midnight is a real booking time. <see cref="Guid.Empty"/> is
/// not a real anything, so the fix here needs no change to the request contract at all: the wire
/// shape, the OpenAPI schema and every generated client stay exactly as they are, and only the
/// refusal improves. On a contract several clients are mapping against, that is worth the extra
/// attribute.
/// </para>
/// <para>
/// It composes with the rest: <c>RequestValidationFilter</c> collects it alongside every other
/// violation, so a body missing both <c>branchId</c> and <c>date</c> names both at once rather than
/// sending somebody round twice.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false)]
internal sealed class NotEmptyAttribute : ValidationAttribute
{
    public NotEmptyAttribute()
        : base("The {0} field is required.")
    {
    }

    /// <remarks>
    /// Null passes. Absence is <see cref="RequiredAttribute"/>'s job, and a field that is optional
    /// but must not be all-zeros when supplied is a real shape - so the two compose rather than
    /// duplicating each other.
    /// </remarks>
    public override bool IsValid(object? value) => value switch
    {
        null => true,
        Guid id => id != Guid.Empty,

        // Anything else is not a Guid and is not this attribute's business.
        _ => true,
    };
}
