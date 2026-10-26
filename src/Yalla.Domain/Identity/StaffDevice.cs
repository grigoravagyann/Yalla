using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Identity;

/// <summary>
/// A tablet enrolled to one branch, or one person's phone paired to them there, holding a long-lived
/// device token.
/// </summary>
/// <remarks>
/// <para>
/// The device is not a person and holds no permissions of its own: it can do exactly one thing,
/// which is offer a PIN and be given a staff session in return. That separation is what lets the
/// tablet stay signed in for months while the person acting on it changes every few hours.
/// </para>
/// <para>
/// <see cref="RevokedAtUtc"/> is the reason this is a table rather than a claim in a token. A
/// tablet left in a taxi has to stop working from the admin panel, immediately, without waiting
/// for a token to expire - so every request carrying a device or session token checks this row.
/// </para>
/// </remarks>
public sealed class StaffDevice : Entity
{
    public Guid VenueId { get; private set; }

    /// <summary>
    /// The one branch this tablet belongs to. A device token is branch-scoped by construction,
    /// which is half of why a waiter at branch A cannot act on branch B.
    /// </summary>
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    /// <summary>What a manager calls it in the admin panel: "Bar tablet", "Terrace".</summary>
    public string Name { get; private set; } = null!;

    /// <summary>
    /// The identifier the client generated for itself and keeps in its own storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A browser is a device here, and the browser is the thing that has to recognise itself. It
    /// mints this once, stores it, and sends it with every enrolment, so a PWA can tell "I am
    /// already the terrace tablet" from "I am a laptop that has never been enrolled" without a
    /// server round trip - and a manager looking at the device list sees one row per real device
    /// rather than one per time somebody reloaded the page.
    /// </para>
    /// <para>
    /// Not the primary key. A client-chosen primary key lets a caller name a row that already
    /// exists; this is a claim about the caller, checked against a unique index scoped to the
    /// branch and filtered to live devices, so a revoked tablet's identifier can be enrolled again
    /// after the browser is wiped and re-set-up.
    /// </para>
    /// </remarks>
    public string ClientDeviceId { get; private set; } = null!;

    /// <summary>Last time a token from this device was seen, so an unused tablet is obvious.</summary>
    public DateTime? LastSeenAtUtc { get; private set; }

    /// <summary>Set when a manager revoked the tablet. Never cleared - enrol a new device instead.</summary>
    public DateTime? RevokedAtUtc { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    /// <summary>A shared tablet, or one person's phone. Existing rows are tablets.</summary>
    public StaffDeviceKind Kind { get; private set; } = StaffDeviceKind.Tablet;

    /// <summary>
    /// The one person a phone belongs to: only their PIN opens it, and its roster lists only them. Always set
    /// on a phone and never on a tablet - the invariant <see cref="Phone"/> and the table's check constraint
    /// both hold.
    /// </summary>
    public Guid? BoundStaffMemberId { get; private set; }

    public StaffMember? BoundStaffMember { get; private set; }

    /// <summary>
    /// A phone's platform, as it said when it paired - the parts the console words its device column from, in the
    /// reader's language. Null on a tablet, and on a phone paired before 2026-10-26 or by an app that did not say:
    /// <see cref="Name"/> is then all there is.
    /// </summary>
    public StaffDevicePlatform? Platform { get; private set; }

    /// <summary>A phone's browser, as it said when it paired. Null on a tablet, or when it did not say or know.</summary>
    public StaffDeviceBrowser? Browser { get; private set; }

    /// <summary>
    /// Which staff pushes this device asked for. Null until somebody chooses, and then
    /// <see cref="StaffPushKinds.Defaults"/> applies - see <see cref="EffectivePushKinds"/>.
    /// </summary>
    public StaffPushKinds? PushKinds { get; private set; }

    /// <summary>
    /// Whether a phone's pushes wait for its person's shift. Null until chosen, and then the role decides:
    /// on for a waiter, off for a manager and above (<see cref="OnlyDuringShiftByDefault"/>). Never read
    /// for a tablet, which has no person and so no shift.
    /// </summary>
    public bool? PushOnlyDuringShift { get; private set; }

    /// <summary>
    /// The language this device's pushes are written in (<c>hy</c>, <c>ru</c> or <c>en</c>), as the app last
    /// said. Null: Armenian, the market's language.
    /// </summary>
    public string? Locale { get; private set; }

    /// <summary>What this device gets pushed: its own choice, or the defaults.</summary>
    public StaffPushKinds EffectivePushKinds => PushKinds ?? StaffPushKinds.Defaults;

    /// <summary>
    /// The default for "only during my shift": a waiter's pocket should be quiet off shift, while a manager
    /// wants to know whenever something needs them.
    /// </summary>
    public static bool OnlyDuringShiftByDefault(StaffRole role) => role is StaffRole.Waiter or StaffRole.Kitchen;

    private StaffDevice()
    {
    }

    public StaffDevice(
        Guid venueId,
        Guid branchId,
        string name,
        string clientDeviceId,
        DateTime enrolledAtUtc)
        : base(Guid.CreateVersion7())
    {
        VenueId = Guard.NotEmpty(venueId, nameof(venueId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.DeviceName);
        ClientDeviceId = Guard.NotBlank(clientDeviceId, nameof(clientDeviceId), FieldLengths.DeviceId);
        StampCreatedAt(enrolledAtUtc);
        LastSeenAtUtc = enrolledAtUtc;
    }

    /// <summary>
    /// One person's own phone at a branch. Paired by a code a manager issued for them and redeemed with their PIN.
    /// </summary>
    public static StaffDevice Phone(
        Guid venueId,
        Guid branchId,
        string name,
        string clientDeviceId,
        Guid boundStaffMemberId,
        DateTime pairedAtUtc) =>
        new(venueId, branchId, name, clientDeviceId, pairedAtUtc)
        {
            Kind = StaffDeviceKind.Phone,
            BoundStaffMemberId = Guard.NotEmpty(boundStaffMemberId, nameof(boundStaffMemberId)),
        };

    /// <summary>
    /// What a phone runs on, as it said when it paired (again): replaced whole, so a browser that no longer says
    /// clears what an earlier one did. Ignored on a tablet, whose name is the manager's.
    /// </summary>
    public void Describe(StaffDevicePlatform? platform, StaffDeviceBrowser? browser)
    {
        if (Kind != StaffDeviceKind.Phone)
        {
            return;
        }

        Platform = platform is { } p && Enum.IsDefined(p) ? p : null;
        Browser = browser is { } b && Enum.IsDefined(b) ? b : null;
    }

    /// <summary>Whether this is a phone bound to somebody other than <paramref name="staffMemberId"/>.</summary>
    public bool IsBoundToSomebodyOtherThan(Guid staffMemberId) =>
        Kind == StaffDeviceKind.Phone && BoundStaffMemberId != staffMemberId;

    /// <summary>
    /// Records what this device wants pushed. <paramref name="onlyDuringShift"/> is ignored on a tablet,
    /// which has no person to be on shift.
    /// </summary>
    public void SetPushPreferences(StaffPushKinds kinds, bool onlyDuringShift)
    {
        PushKinds = kinds & StaffPushKinds.All;
        PushOnlyDuringShift = Kind == StaffDeviceKind.Phone ? onlyDuringShift : null;
    }

    /// <summary>The language for this device's pushes. Null or blank clears it.</summary>
    public void SetLocale(string? locale) =>
        Locale = string.IsNullOrWhiteSpace(locale)
            ? null
            : Guard.NotBlank(locale.Trim(), nameof(locale), FieldLengths.LocaleCode);

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), FieldLengths.DeviceName);

    /// <summary>
    /// Records that the tablet was seen. Callers throttle this - a write per request would put
    /// the busiest table in the database on the read path.
    /// </summary>
    public void Touch(DateTime atUtc) => LastSeenAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

    public void Revoke(DateTime revokedAtUtc) =>
        RevokedAtUtc ??= Guard.NotLocalTime(revokedAtUtc, nameof(revokedAtUtc));
}
