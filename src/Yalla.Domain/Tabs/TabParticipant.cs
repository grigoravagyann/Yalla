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

    /// <summary>
    /// The diner's account, when they have one. Usually they do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tab participant is account-less by design: scanning a table and ordering must not require
    /// signing up for anything, and that stays true. This is recorded when the scanning phone
    /// <i>happens</i> to be carrying a diner token - somebody who books through the app and then
    /// scans a table.
    /// </para>
    /// <para>
    /// Prompt 9 gave it a second job. It is the only address a push has: a device id from the tab
    /// flow and a push token from the diner app are separate identifier spaces with nothing joining
    /// them, so "the host approved you" reaches exactly the participants who have one of these and
    /// nobody else. No navigation - identity is its own module.
    /// </para>
    /// </remarks>
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

    public bool IsHost => Role == ParticipantRole.Host;

    public bool IsApproved => Status == ParticipantStatus.Approved;

    public bool IsRemoved => Status == ParticipantStatus.Removed;

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
    /// The first scanner: approved on the spot, may pay, may see everything. There is nobody
    /// else on the tab to approve them.
    /// </summary>
    public static TabParticipant Host(
        Guid tabId,
        string displayName,
        string deviceId,
        DateTime joinedAtUtc,
        Guid? userId = null) =>
        new(
            tabId,
            displayName,
            deviceId,
            ParticipantRole.Host,
            ParticipantStatus.Approved,
            joinedAtUtc,
            userId,
            canOrder: true,
            canSeeTableTotal: true,
            canPay: true);

    /// <summary>
    /// Everyone after the first: pending until the host taps approve, with the tab's defaults.
    /// </summary>
    /// <remarks>
    /// <c>CanPay</c> starts false - the host is often treating, and letting a stranger who joined
    /// the wrong table put money toward it is a refund nobody wants to process.
    /// <c>CanSeeTableTotal</c> follows the tab's <c>HideTotalFromGuests</c>, which is the table
    /// default the host chose.
    /// </remarks>
    public static TabParticipant Guest(
        Guid tabId,
        string displayName,
        string deviceId,
        DateTime joinedAtUtc,
        bool hideTotalFromGuests,
        Guid? userId = null) =>
        new(
            tabId,
            displayName,
            deviceId,
            ParticipantRole.Guest,
            ParticipantStatus.PendingApproval,
            joinedAtUtc,
            userId,
            canOrder: true,
            canSeeTableTotal: !hideTotalFromGuests,
            canPay: false);

    /// <summary>
    /// Sets the three permissions together, because they are not independent: nobody pays toward
    /// a total they are not allowed to see.
    /// </summary>
    /// <remarks>
    /// <c>CanPay</c> implies <c>CanSeeTableTotal</c> is enforced here as a domain invariant rather
    /// than in the apps. Three clients consume this API and a rule that lives only in a UI is a
    /// rule that one of them will forget. The refusal is a refusal - the flags are not silently
    /// corrected, because a host who tapped "may pay" and got "may see the total" without being
    /// told has been surprised, and their next tap is made on the wrong assumption.
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

    /// <summary>
    /// The host turns a pending joiner away. Ends as <see cref="ParticipantStatus.Removed"/>,
    /// because "was never let on" and "was taken off" leave the same thing behind: a row that no
    /// longer acts, kept so anything that referenced it still resolves.
    /// </summary>
    public void Reject(DateTime rejectedAtUtc)
    {
        if (Status != ParticipantStatus.PendingApproval)
        {
            throw new DomainStateException(
                $"Only a pending participant can be rejected; this one is {Status}.");
        }

        Status = ParticipantStatus.Removed;
        RemovedAtUtc ??= Guard.NotLocalTime(rejectedAtUtc, nameof(rejectedAtUtc));
    }

    /// <summary>
    /// Takes an accidental joiner off the tab. A status change, never a delete: their order lines
    /// and any payment they made are financial records and must survive them leaving.
    /// </summary>
    public void Remove(DateTime removedAtUtc)
    {
        if (IsHost)
        {
            throw new DomainStateException(
                "The host cannot be removed from their own tab. Reassign the host first.");
        }

        Status = ParticipantStatus.Removed;
        RemovedAtUtc ??= Guard.NotLocalTime(removedAtUtc, nameof(removedAtUtc));
    }

    /// <summary>
    /// Becomes the host. Hosting means approving joiners, choosing the split and, often, paying -
    /// so the role brings sight of the total and the right to pay with it.
    /// </summary>
    public void BecomeHost()
    {
        if (Status != ParticipantStatus.Approved)
        {
            throw new DomainStateException(
                $"Only an approved participant can become the host; this one is {Status}.");
        }

        Role = ParticipantRole.Host;
        SetPermissions(CanOrder, canSeeTableTotal: true, canPay: true);
    }

    /// <summary>
    /// Steps down to guest when the host role is moved to somebody else. Their permissions are
    /// left as they were: the person who opened the tab is still at the table and still allowed
    /// what they were allowed.
    /// </summary>
    public void BecomeGuest() => Role = ParticipantRole.Guest;
}
