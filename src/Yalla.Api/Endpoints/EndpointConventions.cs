using Yalla.Api.Errors;
using Yalla.Api.Filters;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Shared endpoint conventions: the tag vocabulary and the problem-response declaration.
/// </summary>
public static class EndpointConventions
{
    /// <summary>The diner app - phones held by people at, or booking, a table.</summary>
    public const string DinerTag = "diner";

    /// <summary>The staff tablet on the floor.</summary>
    public const string StaffTag = "staff";

    /// <summary>The admin panel - owners and managers in a desktop browser.</summary>
    public const string AdminTag = "admin";

    /// <summary>The sign-in flows for all four identity types.</summary>
    public const string AuthTag = "auth";

    /// <summary>The people who run Yalla: onboarding and configuring venues.</summary>
    public const string PlatformTag = "platform";

    /// <summary>
    /// Declares a failure response carrying the API's problem document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the framework's <c>ProducesProblem</c>, which declares the base <c>ProblemDetails</c>
    /// type. Every failure here is a <see cref="UnifiedErrorEnvelope"/> - an RFC 7807 document
    /// with four extension members, including the <c>code</c> that clients actually branch on -
    /// and a generated client that only knew about the base type would be missing exactly the
    /// fields it needs.
    /// </para>
    /// <para>
    /// Renamed rather than overloaded on purpose: an overload one optional argument away from the
    /// framework's is an overload someone resolves to the wrong one.
    /// </para>
    /// </remarks>
    /// <param name="builder">The endpoint.</param>
    /// <param name="statusCode">The status this describes.</param>
    /// <param name="description">
    /// What this failure means for <i>this</i> endpoint. Worth writing for the outcomes a client
    /// has to handle - "someone else took that table" - rather than restating the status code.
    /// </param>
    public static RouteHandlerBuilder ProducesProblemDetails(
        this RouteHandlerBuilder builder,
        int statusCode,
        string description) =>
        builder
            .Produces<UnifiedErrorEnvelope>(statusCode, ErrorResponsesOperationFilter.ProblemMediaType)
            .WithMetadata(new ResponseDescriptionAttribute(statusCode, description));
}

/// <summary>
/// Carries a per-endpoint description for a status code, which <c>Produces</c> has nowhere to put.
/// </summary>
/// <seealso cref="ResponseDescriptionOperationFilter"/>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ResponseDescriptionAttribute(int statusCode, string description) : Attribute
{
    /// <summary>The status code being described.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The description to put on it.</summary>
    public string Description { get; } = description;
}
