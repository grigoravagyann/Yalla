namespace Yalla.Domain.Staff;

/// <summary>
/// The PIN being set is already used by somebody else who signs in on the same tablet. Answers
/// 409 <c>pin-taken</c> with <c>context.field = "pin"</c>.
/// </summary>
/// <remarks>
/// <para>
/// A PIN names a person only among the people a tablet lists. Two people on one roster with the
/// same four digits means one tap away from signing in as the other - the audit log would then name
/// the wrong person - so the second is refused when it is set. "The same tablet" is: for somebody at
/// a branch, everyone active at that branch and everyone venue-wide; for somebody venue-wide, every
/// active person in the venue. Deactivated people do not count.
/// </para>
/// <para>
/// Decided at the moment a PIN is set (creation with one, or a reset). Duplicates already in the
/// data are left alone, and moving a person to another branch does not re-check theirs.
/// </para>
/// </remarks>
public sealed class PinTakenException : DomainStateException
{
    /// <summary>The sentence the console shows under the PIN input. Wire text.</summary>
    public const string Sentence = "Somebody who signs in on this tablet already uses that PIN. Choose another.";

    public PinTakenException()
        : base(Sentence)
    {
    }

    /// <summary>The request field to highlight. Always <c>pin</c>.</summary>
    public string Field => "pin";
}
