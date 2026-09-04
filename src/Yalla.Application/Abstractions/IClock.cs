namespace Yalla.Application.Abstractions;

/// <summary>
/// The current instant, as an injected dependency rather than a static call.
/// </summary>
/// <remarks>
/// Almost every rule in this system is a question about time - is this booking late, has this
/// join token expired, has grace run out - so the clock has to be substitutable in tests from the
/// start. Implementations return UTC; nothing in the system reads local time except when
/// projecting a stored instant into a branch's <c>TimeZoneId</c> for display.
/// </remarks>
public interface IClock
{
    /// <summary>The current UTC instant.</summary>
    DateTime UtcNow { get; }
}
