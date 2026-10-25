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
    private readonly List<SpecialOpeningDay> _specialOpeningDays = [];
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

    /// <summary>
    /// Whether guests may pre-order dishes on their bookings here. Off by default: a venue that has not looked at the
    /// feature must not start receiving kitchen tickets for dishes it never agreed to prepare ahead.
    /// </summary>
    /// <remarks>
    /// On the branch rather than in <see cref="ReservationPolicy"/>, deliberately: the policy is replaced whole by
    /// every save of the reservation form (and by tests and seeders that build one), and a switch living there would be
    /// silently reset by an edit that never showed it.
    /// </remarks>
    public bool AcceptsPreOrders { get; private set; }

    /// <summary>How long before a booking the waiter's pre-order card appears. Default 30; 5 to 120.</summary>
    public int PreOrderMinutesBefore { get; private set; } = Occupancy.PreOrderRules.DefaultMinutesBefore;

    /// <summary>
    /// Switches pre-orders on or off and sets when the waiter sees them, raising <see cref="Occupancy.PreOrderSettingsSaved"/>.
    /// Bounds are the caller's to check.
    /// </summary>
    public void SetPreOrders(bool accepts, int minutesBefore, DateTime nowUtc)
    {
        if (minutesBefore is < Occupancy.PreOrderRules.MinMinutesBefore or > Occupancy.PreOrderRules.MaxMinutesBefore)
        {
            throw new ArgumentOutOfRangeException(nameof(minutesBefore), minutesBefore, "Between 5 and 120 minutes.");
        }

        AcceptsPreOrders = accepts;
        PreOrderMinutesBefore = minutesBefore;

        // Saved, whether or not anything changed: the dishes excluded are the menu's rows, not this one.
        Raise(new Occupancy.PreOrderSettingsSaved(this, Guard.NotLocalTime(nowUtc, nameof(nowUtc))));
    }

    /// <summary>
    /// Whether guests may order from the app to take away. Off by default, for the pre-order switch's reason: a venue
    /// that has not looked at the feature must not start receiving orders it never agreed to cook.
    /// </summary>
    /// <remarks>On the branch rather than in <see cref="ReservationPolicy"/>, for the reason <see cref="AcceptsPreOrders"/> is.</remarks>
    public bool TakeawayEnabled { get; private set; }

    /// <summary>
    /// Orders are accepted without anybody tapping "Accept", ready at the time the guest asked for. Off by default: staff
    /// accept on the tablet, and an order nobody accepts within five minutes is declined.
    /// </summary>
    public bool TakeawayAutoAccept { get; private set; }

    /// <summary>The earliest an "as soon as possible" order is ready: 5 to 90 minutes, in fives. Default 20.</summary>
    public int TakeawayPrepMinutes { get; private set; } = Takeaway.TakeawayRules.DefaultPrepMinutes;

    /// <summary>Ordering stops this long before closing: 0 to 120 minutes, in fifteens. Default 30.</summary>
    public int TakeawayLastOrderMinutes { get; private set; } = Takeaway.TakeawayRules.DefaultLastOrderMinutes;

    /// <summary>How many takeaway orders may be sent or accepted at once: 1 to 30. Default 5.</summary>
    public int TakeawayMaxInProgress { get; private set; } = Takeaway.TakeawayRules.DefaultMaxInProgress;

    /// <summary>Saves the takeaway settings together. Out of bounds or off a step is refused, every field named.</summary>
    /// <exception cref="FieldValidationException">A setting out of its bounds or off its step.</exception>
    public void SetTakeaway(bool enabled, bool autoAccept, int prepMinutes, int lastOrderMinutes, int maxInProgress)
    {
        if (Takeaway.TakeawayRules.CheckSettings(prepMinutes, lastOrderMinutes, maxInProgress) is { Count: > 0 } violations)
        {
            throw new FieldValidationException(violations);
        }

        TakeawayEnabled = enabled;
        TakeawayAutoAccept = autoAccept;
        TakeawayPrepMinutes = prepMinutes;
        TakeawayLastOrderMinutes = lastOrderMinutes;
        TakeawayMaxInProgress = maxInProgress;
    }

    /// <summary>
    /// Whether guests may write to the venue in a booking's or an order's thread. On by default; off, the answer buttons
    /// ("I'm coming", "running late", "not coming") still work and only typed messages are refused.
    /// </summary>
    /// <remarks>On the branch rather than in <see cref="ReservationPolicy"/>, for the reason <see cref="AcceptsPreOrders"/> is.</remarks>
    public bool GuestsCanMessage { get; private set; } = true;

    /// <summary>Whether waiters may write to guests. On by default; managers, the general manager and the owner always may.</summary>
    public bool WaitersCanMessage { get; private set; } = true;

    /// <summary>
    /// The venue's quick replies, as a JSON array of strings, or null for the app's default set. At most eight, each 1 to
    /// 120 characters.
    /// </summary>
    public string? MessageTemplatesJson { get; private set; }

    /// <summary>A guest message unanswered this long rings on the tablet: 1 to 15 minutes. Default 3.</summary>
    public int MessageRemindAfterMinutes { get; private set; } = Messages.ConversationRules.DefaultRemindAfterMinutes;

    /// <summary>The venue's quick replies, or null for the app's default set.</summary>
    public IReadOnlyList<string>? MessageTemplates => TemplatesFrom(MessageTemplatesJson);

    /// <summary>The quick replies a stored <see cref="MessageTemplatesJson"/> holds, or null for the app's default set.</summary>
    public static IReadOnlyList<string>? TemplatesFrom(string? templatesJson) =>
        templatesJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<List<string>>(templatesJson);

    /// <summary>Saves the message settings together. Out of bounds is refused, every field named.</summary>
    /// <param name="guestsCanMessage">Guests may write.</param>
    /// <param name="waitersCanMessage">Waiters may write.</param>
    /// <param name="templates">The quick replies, trimmed and kept in order; null for the app's default set.</param>
    /// <param name="remindAfterMinutes">1 to 15.</param>
    /// <exception cref="FieldValidationException">Too many templates, one blank or too long, or the reminder out of bounds.</exception>
    public void SetMessages(bool guestsCanMessage, bool waitersCanMessage, IReadOnlyList<string?>? templates, int remindAfterMinutes)
    {
        if (Messages.ConversationRules.CheckSettings(templates, remindAfterMinutes) is { Count: > 0 } violations)
        {
            throw new FieldValidationException(violations);
        }

        GuestsCanMessage = guestsCanMessage;
        WaitersCanMessage = waitersCanMessage;
        MessageTemplatesJson = templates is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(templates.Select(t => t!.Trim()).ToList(), TemplatesJson);
        MessageRemindAfterMinutes = remindAfterMinutes;
    }

    /// <summary>Letters in any script kept as they are, so the column holds the Armenian a manager typed.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions TemplatesJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The branch's published contact number in E.164, or null when nobody has supplied one.
    /// </summary>
    /// <remarks>
    /// Nullable because a venue is perfectly usable before anyone has typed its number in, and a
    /// branch created through the platform API has no way to supply one. It is on the public page
    /// because a diner who wants to ask about a high chair, a wheelchair ramp or a dog has no other
    /// way to reach the venue - the page has no messaging and is not going to grow one.
    /// </remarks>
    public string? PhoneE164 { get; private set; }

    /// <summary>
    /// Whether this branch takes bookings from the public page. <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default false is a deliberate refusal to opt a venue in to something it never agreed to. A
    /// branch that has never been asked has not said it will accept bookings from strangers on the
    /// internet, and the first pilot venues are onboarded by hand anyway - so the cost of the
    /// default being wrong is one switch somebody flips during onboarding, against a venue
    /// discovering it has been taking web bookings nobody told it about.
    /// </para>
    /// <para>
    /// It is on the readiness checklist for the same reason: a line somebody ticks is a decision,
    /// and a default nobody sees is not.
    /// </para>
    /// <para>
    /// When it is false the public page still shows the room, the menu and the hours and simply
    /// offers no booking - which is a perfectly good page, and the one most venues will start with.
    /// </para>
    /// </remarks>
    public bool AcceptsWebBookings { get; private set; }

    /// <summary>Sets or clears the published contact number. Blank clears it.</summary>
    /// <exception cref="ArgumentException">The value is not a valid E.164 number.</exception>
    public void SetPhoneE164(string? phoneE164) =>
        PhoneE164 = string.IsNullOrWhiteSpace(phoneE164)
            ? null
            : PhoneNumber.Normalise(phoneE164, nameof(phoneE164));

    /// <summary>Turns public-page booking on or off for this branch.</summary>
    public void SetAcceptsWebBookings(bool accepts) => AcceptsWebBookings = accepts;

    /// <summary>The cuisine line on the browse card, e.g. "Armenian &amp; Mediterranean". Free text.</summary>
    public string? Cuisine { get; private set; }

    /// <summary>A paragraph about the place, for the details screen.</summary>
    public string? About { get; private set; }

    /// <summary>1 (cheap) to 4 (expensive), or null when the venue has not said.</summary>
    public int? PriceLevel { get; private set; }

    /// <summary>An http(s) address, or null.</summary>
    public string? WebsiteUrl { get; private set; }

    /// <summary>Comma-joined keys from <see cref="BranchListingRules.AmenityKeys"/>. See <see cref="Amenities"/>.</summary>
    public string? AmenityKeys { get; private set; }

    /// <summary>The amenity keys, in the order the venue gave them.</summary>
    public IReadOnlyList<string> Amenities =>
        string.IsNullOrEmpty(AmenityKeys) ? [] : AmenityKeys.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The part of town the branch is in - "Kentron", "Arabkir" - or null when the venue has not said.
    /// </summary>
    /// <remarks>
    /// What the app prints beside the venue type and the cuisine, and what a diner who knows the city
    /// searches by. Not derived from <see cref="Address"/>: a street address names its district in no
    /// fixed place, and guessing one would print the wrong neighbourhood with confidence. A listing
    /// field like the cuisine line, so a manager may set it - it moves nothing a diner walks to.
    /// </remarks>
    public string? District { get; private set; }

    /// <summary>Replaces every listing field at once. The console edits them as one form.</summary>
    /// <exception cref="FieldValidationException">Every field that broke a rule.</exception>
    public void UpdateListing(
        string? cuisine,
        string? about,
        int? priceLevel,
        string? websiteUrl,
        IEnumerable<string>? amenities,
        string? district)
    {
        var listing = BranchListingRules.Normalise(cuisine, about, priceLevel, websiteUrl, amenities, district);

        Cuisine = listing.Cuisine;
        About = listing.About;
        PriceLevel = listing.PriceLevel;
        WebsiteUrl = listing.WebsiteUrl;
        AmenityKeys = listing.Amenities.Count == 0 ? null : string.Join(',', listing.Amenities);
        District = listing.District;
    }

    public Guid? CoverPhotoId { get; private set; }

    public Media.Photo? CoverPhoto { get; private set; }

    /// <summary>Sets or clears the venue card's picture.</summary>
    public void SetCoverPhoto(Guid? photoId) => CoverPhotoId = photoId;

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

    /// <summary>Dates with their own hours, overriding the week. Read through <see cref="OpeningCalendar"/>.</summary>
    public IReadOnlyCollection<SpecialOpeningDay> SpecialOpeningDays => _specialOpeningDays;

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

    /// <summary>
    /// Which saved revision of the floor plan this is. An optimistic concurrency token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two managers editing the plan in two tabs both start from the same revision. Replacing the
    /// plan is replace-whole, so without this the second save silently throws away the first - the
    /// tables somebody just drew are gone and nobody is told. The editor sends the revision it
    /// loaded; a save against an older one is refused as <c>floor-plan-changed</c>.
    /// </para>
    /// <para>
    /// <b>Only a floor-plan save moves it.</b> Rooms, their photos and the pins on them are all saved
    /// by that one call, so there is no second writer to keep in step with it.
    /// </para>
    /// </remarks>
    public int FloorPlanVersion { get; private set; }

    /// <summary>Marks a new revision of the floor plan. Called by the floor-plan replace, and only there.</summary>
    public void BumpFloorPlanVersion() => FloorPlanVersion++;

    /// <summary>
    /// Which saved revision of the special opening days this is. The console sends the one it loaded; a save
    /// against an older one is refused as <c>concurrent-update</c>, because the list is replaced whole and the
    /// second of two editors would otherwise undo the first one's holiday without anybody being told.
    /// </summary>
    public int SpecialDaysVersion { get; private set; }

    /// <summary>Marks a new revision of the special days. Called by their replace, and only there.</summary>
    public void BumpSpecialDaysVersion() => SpecialDaysVersion++;

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
