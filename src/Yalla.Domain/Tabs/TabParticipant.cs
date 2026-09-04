using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Tabs;

/// <summary>
/// One person on a tab, with what they are allowed to do on it.
/// </summary>
/// <remarks>
/// A participant is identified by device as well as by account, because the common case is a
/// table of six where two people have the app and four scanned a QR code.
/// </remarks>
public sealed class TabParticipant : Entity
{
    public Guid TabId { get; private set; }

    public Tab Tab { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;

    /// <summary>The diner's account, when they have one. Identity is a later module, so no navigation.</summary>
    public Guid? UserId { get; private set; }

    /// <summary>The phone that joined, so an anonymous QR guest can be recognised again.</summary>
    public string DeviceId { get; private set; } = null!;

    public ParticipantRole Role { get; private set; }

    public ParticipantStatus Status { get; private set; }

    public DateTime JoinedAtUtc { get; private set; }

    public DateTime? ApprovedAtUtc { get; private set; }

    public DateTime? RemovedAtUtc { get; private set; }

    /// <summary>Whether this person may add items to the tab.</summary>
    public bool CanOrder { get; private set; }

    /// <summary>Whether this person may see the whole table's bill rather than only their own items.</summary>
    public bool CanSeeTableTotal { get; private set; }

    /// <summary>
    /// Whether this person may settle against the tab. Implies <see cref="CanSeeTableTotal"/> -
    /// see the class remarks on <see cref="SetPermissions"/>.
    /// </summary>
    public bool CanPay { get; private set; }

    private TabParticipant()
    {
    }

    public TabParticipant(
        Guid tabId,
        string displayName,
        string deviceId,
        ParticipantRole role,
        ParticipantStatus status,
        DateTime joinedAtUtc,
        Guid? userId = null,
        bool canOrder = true,
        bool canSeeTableTotal = true,
        bool canPay = false)
        : base(Guid.CreateVersion7())
    {
        TabId = Guard.NotEmpty(tabId, nameof(tabId));
        DisplayName = Guard.NotBlank(displayName, nameof(displayName), FieldLengths.DisplayName);
        DeviceId = Guard.NotBlank(deviceId, nameof(deviceId), FieldLengths.DeviceId);
        Role = Guard.Defined(role, nameof(role));
        Status = Guard.Defined(status, nameof(status));
        JoinedAtUtc = Guard.NotLocalTime(joinedAtUtc, nameof(joinedAtUtc));
        UserId = userId;
        ApprovedAtUtc = status == ParticipantStatus.Approved ? JoinedAtUtc : null;

        SetPermissions(canOrder, canSeeTableTotal, canPay);
    }

    /// <summary>
    /// Sets the three permissions together, because they are not independent: nobody pays toward
    /// a total they are not allowed to see.
    /// </summary>
    /// <remarks>
    /// <c>CanPay</c> implies <c>CanSeeTableTotal</c> is enforced here as a domain invariant rather
    /// than in the apps. Three clients consume this API and a rule that lives only in a UI is a
    /// rule that one of them will forget.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="canPay"/> is true while <paramref name="canSeeTableTotal"/> is false.
    /// </exception>
    public void SetPermissions(bool canOrder, bool canSeeTableTotal, bool canPay)
    {
        if (canPay && !canSeeTableTotal)
        {
            throw new ArgumentException(
                "A participant who may pay must also be allowed to see the table total.",
                nameof(canSeeTableTotal));
        }

        CanOrder = canOrder;
        CanSeeTableTotal = canSeeTableTotal;
        CanPay = canPay;
    }

    /// <summary>
    /// Renames this person on the tab, so the host sees "Ani" rather than "Guest 3".
    /// </summary>
    /// <remarks>
    /// This is a profile field on the participant, not an account. A walk-in who scanned the QR
    /// has no user row and never will; letting them put a name on the tab is the entire extent of
    /// their "profile", and it costs them nothing to skip.
    /// </remarks>
    public void SetDisplayName(string displayName) =>
        DisplayName = Guard.NotBlank(displayName, nameof(displayName), FieldLengths.DisplayName);

    public void Approve(DateTime approvedAtUtc)
    {
        if (Status == ParticipantStatus.Removed)
        {
            throw new DomainStateException("A removed participant cannot be approved.");
        }

        Status = ParticipantStatus.Approved;
        ApprovedAtUtc ??= Guard.NotLocalTime(approvedAtUtc, nameof(approvedAtUtc));
    }

    public void Remove(DateTime removedAtUtc)
    {
        Status = ParticipantStatus.Removed;
        RemovedAtUtc ??= Guard.NotLocalTime(removedAtUtc, nameof(removedAtUtc));
    }
}
