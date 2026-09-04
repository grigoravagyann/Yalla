namespace Yalla.Domain;

/// <summary>
/// A deliberate refusal by the domain: the request was understood and is legal in shape, but the
/// entity is not in a state that permits it - "this tab is already closed", "the settlement mode
/// is locked", "a removed participant cannot be approved".
/// </summary>
/// <remarks>
/// <para>
/// This type exists to be distinguishable from a plain <see cref="InvalidOperationException"/>,
/// which the domain used to throw for these. That was indistinguishable from the same exception
/// raised by a <b>bug</b>: an unresolved service, <c>First()</c> on an empty sequence, "sequence
/// contains more than one element", EF's transient-failure wrapper. The API mapper turned every
/// one of them into a 409 that told the user they had a conflict, echoed the internal message
/// back to them, and logged none of it as an error - so real faults were invisible in the log and
/// looked exactly like ordinary contention.
/// </para>
/// <para>
/// Deriving from <see cref="InvalidOperationException"/> keeps existing <c>catch</c> blocks and
/// tests working; the point is only that the mapper can now tell the deliberate refusals apart
/// from the accidents. Anything the domain throws as this type has a message written to be read
/// by a person and is safe to return in a response body.
/// </para>
/// </remarks>
public class DomainStateException : InvalidOperationException
{
    public DomainStateException(string message)
        : base(message)
    {
    }

    public DomainStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
