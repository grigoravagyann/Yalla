using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Menus;

namespace Yalla.Domain.Venues;

/// <summary>
/// One physical location: a floor plan, opening hours, a menu, a reservation policy.
/// </summary>
/// <remarks>
/// The branch is the operational and commercial unit. A chain with four locations is four paying
/// customers, so every table, booking, tab and payment carries a <c>BranchId</c> from day one
/// rather than being retrofitted when the first multi-branch customer arrives - and the
/// <see cref="SubscriptionTier"/> lives here for the same reason.
/// </remarks>
public sealed class Branch : Entity
{
    private readonly List<OpeningHours> _openingHours = [];
    private readonly List<FloorArea> _floorAreas = [];
    private readonly List<DiningTable> _diningTables = [];
    private readonly List<MenuCategory> _menuCategories = [];

    public Guid VenueId { get; private set; }

    public Venue Venue { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>URL segment, unique within the venue.</summary>
    public string Slug { get; private set; } = null!;

    public string Address { get; private set; } = null!;

    public double Latitude { get; private set; }

    public double Longitude { get; private set; }

    /// <summary>
    /// IANA time zone identifier, e.g. <c>Asia/Yerevan</c>. Every stored instant is UTC; this is
    /// what turns a UTC instant back into the wall-clock time the diner and the waiter see, and
    /// what interprets the wall-clock <see cref="OpeningHours"/>.
    /// </summary>
    public string TimeZoneId { get; private set; } = null!;

    public bool IsActive { get; private set; }

    /// <summary>
    /// The picture on the venue card in the diner app. Optional, and the same entity a menu item
    /// uses - a cover and a dish photo differ in what they are of, not in how they are stored.
    /// </summary>
    /// <remarks>
    /// The diner venue and branch cards faked this until Prompt 9. Nullable because a venue is
    /// perfectly usable before somebody has taken a photograph of the room, which is not true of a
    /// menu item: a dish with no picture is the thing the diner then asks a waiter about.
    /// </remarks>
    /// <summary>
    /// Whether diners are pushed when their order is ready. <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// Noisy in a cafe where a waiter carries the plate ten feet, and genuinely useful in a canteen
    /// where the diner collects it. Defaulting it on would train a whole city to switch our
    /// notifications off, and the ones that matter - the reminder and the nudge - would go with them.
    /// </remarks>
    public bool NotifyOnOrderReady { get; private set; }

    /// <summary>Turns the order-ready push on or off for this branch.</summary>
    public void SetNotifyOnOrderReady(bool notify) => NotifyOnOrderReady = notify;

    public Guid? CoverPhotoId { get; private set; }

    public Media.Photo? CoverPhoto { get; private set; }

    /// <summary>Sets or clears the venue card's picture.</summary>
    public void SetCoverPhoto(Guid? photoId) => CoverPhotoId = photoId;

    /// <summary>Width of the floor-plan canvas the tables are positioned on, in design units.</summary>
    public int FloorWidth { get; private set; }

    /// <summary>Height of the floor-plan canvas the tables are positioned on, in design units.</summary>
    public int FloorHeight { get; private set; }

    /// <summary>
    /// What this branch pays for. <see cref="Enums.SubscriptionTier.Free"/> gets the floor plan and
    /// reservations; <see cref="Enums.SubscriptionTier.Paid"/> adds tabs, ordering and payments.
    /// A flag that gates features - billing is out of scope.
    /// </summary>
    public SubscriptionTier SubscriptionTier { get; private set; }

    /// <summary>Whether tabs, ordering and payments are switched on here.</summary>
    public bool IsPaid => SubscriptionTier == SubscriptionTier.Paid;

    /// <summary>Owned value: persisted as extra columns on this row, never as its own table.</summary>
    public ReservationPolicy ReservationPolicy { get; private set; } = null!;

    /// <summary>
    /// When somebody last saved the reservation policy for this branch, or null if nobody ever has.
    /// </summary>
    /// <remarks>
    /// A branch ships with <see cref="ReservationPolicy.DefaultFor"/>, so "has a policy" is true
    /// from the moment it is created and answers nothing. What the onboarding checklist needs to
    /// know is whether a human has <i>looked</i> at the defaults - a 90-minute cover and a 14-day
    /// window are a guess about a venue nobody has visited - and that is a different fact, which
    /// nothing else on the row records.
    /// </remarks>
    public DateTime? ReservationPolicyReviewedAtUtc { get; private set; }

    public IReadOnlyCollection<OpeningHours> OpeningHours => _openingHours;

    public IReadOnlyCollection<FloorArea> FloorAreas => _floorAreas;

    public IReadOnlyCollection<DiningTable> DiningTables => _diningTables;

    public IReadOnlyCollection<MenuCategory> MenuCategories => _menuCategories;

    private Branch()
    {
    }

    public Branch(
        Venue venue,
        string name,
        string slug,
        string address,
        double latitude,
        double longitude,
        string timeZoneId,
        int floorWidth,
        int floorHeight,
        ReservationPolicy? reservationPolicy = null,
        SubscriptionTier subscriptionTier = SubscriptionTier.Free)
        : base(Guid.CreateVersion7())
    {
        ArgumentNullException.ThrowIfNull(venue);

        VenueId = venue.Id;
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
        Slug = SlugText.Normalise(slug, nameof(slug));
        Address = Guard.NotBlank(address, nameof(address), FieldLengths.Address);
        Latitude = InRange(latitude, -90d, 90d, nameof(latitude));
        Longitude = InRange(longitude, -180d, 180d, nameof(longitude));
        TimeZoneId = NormaliseTimeZoneId(timeZoneId);
        FloorWidth = Guard.Positive(floorWidth, nameof(floorWidth));
        FloorHeight = Guard.Positive(floorHeight, nameof(floorHeight));
        ReservationPolicy = reservationPolicy ?? ReservationPolicy.DefaultFor(venue.Type);
        SubscriptionTier = Guard.Defined(subscriptionTier, nameof(subscriptionTier));
        IsActive = true;
    }

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);

    public void SetActive(bool isActive) => IsActive = isActive;

    public void SetSubscriptionTier(SubscriptionTier tier) =>
        SubscriptionTier = Guard.Defined(tier, nameof(tier));

    /// <summary>Moves the branch: a new address and coordinates together, since one without the other is wrong.</summary>
    public void Relocate(string address, double latitude, double longitude)
    {
        Address = Guard.NotBlank(address, nameof(address), FieldLengths.Address);
        Latitude = InRange(latitude, -90d, 90d, nameof(latitude));
        Longitude = InRange(longitude, -180d, 180d, nameof(longitude));
    }

    public void SetTimeZone(string timeZoneId) => TimeZoneId = NormaliseTimeZoneId(timeZoneId);

    /// <summary>Replaces the whole policy. The admin panel edits it as one form.</summary>
    /// <remarks>
    /// Saving the form is what counts as reviewing it, which is why the timestamp is stamped here
    /// rather than by a separate "I have read this" action nobody would click.
    /// </remarks>
    public void UpdateReservationPolicy(ReservationPolicy policy, DateTime reviewedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);

        ReservationPolicy = policy;
        ReservationPolicyReviewedAtUtc = Guard.NotLocalTime(reviewedAtUtc, nameof(reviewedAtUtc));
    }

    public void ResizeFloor(int floorWidth, int floorHeight)
    {
        FloorWidth = Guard.Positive(floorWidth, nameof(floorWidth));
        FloorHeight = Guard.Positive(floorHeight, nameof(floorHeight));
    }

    private static string NormaliseTimeZoneId(string? timeZoneId)
    {
        var value = Guard.NotBlank(timeZoneId, nameof(timeZoneId), FieldLengths.TimeZoneId);

        // An IANA identifier never contains whitespace. Resolving it against the host's tz
        // database is deliberately left out of the domain: it is platform-dependent, and the
        // domain must stay free of environment concerns.
        return value.Any(char.IsWhiteSpace)
            ? throw new ArgumentException("An IANA time zone identifier must not contain whitespace.", nameof(timeZoneId))
            : value;
    }

    private static double InRange(double value, double min, double max, string paramName) =>
        double.IsNaN(value) || value < min || value > max
            ? throw new ArgumentOutOfRangeException(paramName, value, $"Value must be between {min} and {max}.")
            : value;
}
