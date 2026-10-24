namespace Yalla.Domain.Identity;

/// <summary>
/// A phone cannot be paired to kitchen staff. The kitchen works on its tablet; there is no phone screen for it.
/// </summary>
/// <remarks>
/// Its own code (<c>kitchen-phone-not-allowed</c>, 409) so the console can say why rather than "conflict" - and
/// the console hides the button for kitchen staff in the first place. Raised when the code is issued, and again
/// at redemption for somebody moved to the kitchen in the ten minutes between.
/// </remarks>
public sealed class KitchenPhoneNotAllowedException : DomainStateException
{
    public const string Sentence =
        "Kitchen staff work on the kitchen tablet, so a phone cannot be paired to them.";

    public KitchenPhoneNotAllowedException(Guid staffMemberId)
        : base(Sentence) => StaffMemberId = staffMemberId;

    /// <summary>Who was refused.</summary>
    public Guid StaffMemberId { get; }
}
