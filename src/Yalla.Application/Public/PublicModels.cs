using Yalla.Application.BranchSettings;
using Yalla.Application.Menus;
using Yalla.Domain.Enums;

namespace Yalla.Application.Public;

/// <summary>
/// One venue on the browse list, with its branches.
/// </summary>
/// <remarks>
/// Deliberately thin. This is what an anonymous request gets, and every field on it is something a
/// person standing in the doorway could see for themselves - so there is nothing here to leak.
/// </remarks>
/// <param name="VenueSlug">The public identity. Half of a printed link.</param>
/// <param name="Name">The venue's name.</param>
/// <param name="Type">1 Cafe, 2 Restaurant.</param>
/// <param name="Branches">Its active branches.</param>
public sealed record PublicVenueCard(
    string VenueSlug,
    string Name,
    VenueType Type,
    IReadOnlyList<PublicBranchCard> Branches);

/// <summary>One branch on the browse list.</summary>
/// <param name="BranchId">The id, for the menu and availability routes.</param>
/// <param name="BranchSlug">The other half of a printed link.</param>
/// <param name="Name">The branch's name.</param>
/// <param name="Address">Where it is.</param>
/// <param name="FreeTableCount">
/// How many bookable tables have nobody sitting at them right now - the same tables the branch
/// page's <c>tableCount</c> counts, so the two can never read "3 of 2 free". The one volatile
/// number here, and the reason the browse list is cached for seconds rather than minutes.
/// </param>
/// <param name="IsOpenNow">Whether it is inside an opening block at this moment, in its own zone.</param>
/// <param name="TimeZoneId">
/// The branch's IANA zone, e.g. <c>Asia/Yerevan</c>. A slot a diner picks after browsing is a
/// wall-clock time at the branch, and a phone set to another zone has to render it in this one
/// rather than guess - the diner app guessed <c>Asia/Yerevan</c>, because this list is the only
/// public read in front of its venue and booking screens and it did not say.
/// </param>
public sealed record PublicBranchCard(
    Guid BranchId,
    string BranchSlug,
    string Name,
    string Address,
    int FreeTableCount,
    bool IsOpenNow,
    string TimeZoneId);

/// <summary>
/// One branch's public page.
/// </summary>
/// <param name="VenueSlug">The venue half of the link.</param>
/// <param name="BranchSlug">The branch half.</param>
/// <param name="BranchId">The id the menu and availability routes take.</param>
/// <param name="VenueName">The venue's name.</param>
/// <param name="BranchName">The branch's name.</param>
/// <param name="VenueType">1 Cafe, 2 Restaurant.</param>
/// <param name="Address">Street address, as a person would read it.</param>
/// <param name="Latitude">For the map pin.</param>
/// <param name="Longitude">For the map pin.</param>
/// <param name="TimeZoneId">
/// The branch's IANA zone. Every time on this page is rendered in it - a tourist's phone is on the
/// wrong zone, and a tourist is a named primary user of this surface.
/// </param>
/// <param name="OpeningHours">The weekly hours.</param>
/// <param name="IsOpenNow">
/// Whether it is open at this moment, decided in the branch's own zone.
/// <para>
/// <b>This is the only "is it worth going" signal, and there is deliberately no second one.</b> An
/// earlier draft carried a <c>status</c> of Open/Closed beside it, which was computed from this
/// field and therefore said nothing new under a name that implied venue lifecycle. A branch that is
/// suspended, deleted or switched off never reaches this response at all - every query behind it
/// filters those out and the route answers 404 - so "shut tonight" is this field being false, and
/// "this venue is gone" is the 404. Two names for one fact is how a mapper picks the wrong one.
/// </para>
/// </param>
/// <param name="FreeTableCount">
/// Bookable tables with nobody at them right now, so it is always out of <paramref name="TableCount"/>.
/// A walk-in-only stool somebody is sitting at is still drawn taken on the plan: that is each
/// table's own <c>isFree</c>, which every active table has, bookable or not.
/// </param>
/// <param name="TableCount">How many bookable tables there are, so the count has a denominator.</param>
/// <param name="FloorPlan">
/// The room, in diner shape: the canvas, the areas and the tables with their geometry and whether
/// each is free. <b>No QR tokens and no staff state</b> - see <see cref="PublicFloorTable"/>.
/// </param>
/// <param name="PhoneE164">
/// The branch's contact number in E.164, or null when nobody has supplied one. The only way a
/// diner on this page can ask about a high chair or a wheelchair ramp.
/// </param>
/// <param name="AcceptsWebBookings">
/// Whether this branch takes bookings from this page. <b>False unless somebody switched it on</b> -
/// see <see cref="Yalla.Domain.Venues.Branch.AcceptsWebBookings"/>. When false the page shows the
/// room, the menu and the hours and offers no booking, which is a perfectly good page.
/// </param>
/// <param name="BookingWindowDays">
/// How many days ahead this branch takes bookings, which is the maximum of the date input.
/// </param>
/// <param name="Policy">
/// The reservation rules a diner needs, and <b>only</b> those - see
/// <see cref="PublicReservationPolicy"/> for what is deliberately withheld.
/// </param>
/// <param name="AsOfUtc">
/// When the server read the live half of this page. <paramref name="FreeTableCount"/>,
/// <paramref name="IsOpenNow"/> and each table's <c>isFree</c> are true as of this instant and no
/// later; the rest is stable. The page shows the staleness rather than implying there is none,
/// because this link is cached for seconds and shared for days.
/// </param>
public sealed record PublicBranchPage(
    string VenueSlug,
    string BranchSlug,
    Guid BranchId,
    string VenueName,
    string BranchName,
    VenueType VenueType,
    string Address,
    double Latitude,
    double Longitude,
    string TimeZoneId,
    IReadOnlyList<OpeningHoursView> OpeningHours,
    bool IsOpenNow,
    int FreeTableCount,
    int TableCount,
    PublicFloorPlan FloorPlan,
    string? PhoneE164,
    bool AcceptsWebBookings,
    int BookingWindowDays,
    PublicReservationPolicy Policy,
    DateTime AsOfUtc);

/// <summary>
/// The reservation rules a diner needs in order to book, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>A subset, chosen field by field, not a projection of the whole policy.</b> This route has no
/// authentication at all, so everything on it is published to anybody with a URL. The full
/// <c>ReservationPolicyView</c> carries commercial and operational settings - the service charge
/// the venue adds, the party size above which staff vet a booking, how close to a booking a
/// walk-in may still be seated - and none of those are a diner's business. A scraper reading
/// <c>walkInHoldbackMinutes</c> across the estate learns how every venue in the city runs its floor.
/// </para>
/// <para>
/// Adding a field here is a decision to publish it. That is why this is its own record rather than
/// <c>ReservationPolicyView</c> with an attribute or two - a new setting on the policy cannot leak
/// onto the public page by being added upstream, because this record physically cannot carry it.
/// </para>
/// </remarks>
/// <param name="TurnTimeMinutes">
/// How long the table is held. The diner is told this before booking because it is the answer to
/// "can we linger?", and finding out at the table is worse.
/// </param>
/// <param name="MinLeadMinutes">
/// How far ahead a booking must be made, which is what greys out the next available slot.
/// </param>
/// <param name="CancellationDeadlineMinutes">
/// How long before the start a diner may still cancel freely. On the page because a deadline
/// nobody was told about is a deadline that produces no-shows rather than cancellations.
/// </param>
public sealed record PublicReservationPolicy(
    int TurnTimeMinutes,
    int MinLeadMinutes,
    int CancellationDeadlineMinutes);

/// <summary>The room as a diner sees it: a canvas, areas, and tables.</summary>
public sealed record PublicFloorPlan(
    int FloorWidth,
    int FloorHeight,
    IReadOnlyList<FloorAreaView> Areas,
    IReadOnlyList<PublicFloorTable> Tables);

/// <summary>
/// One table on the public floor plan.
/// </summary>
/// <remarks>
/// <b>Not <c>FloorTableView</c>, and that is the whole point.</b> The admin shape carries the
/// table's <c>QrToken</c> - which is the credential that opens a tab - and its full
/// <c>TableStatus</c>, which says whether a table is out of service or being cleaned. Neither is
/// anybody's business from the pavement. What is left is what a person in the doorway can see: how
/// big it is, where it is, and whether somebody is sitting at it.
/// </remarks>
/// <param name="Label">What is printed on it.</param>
/// <param name="Seats">How many it seats.</param>
/// <param name="X">Left edge on the canvas.</param>
/// <param name="Y">Top edge on the canvas.</param>
/// <param name="Width">Width on the canvas.</param>
/// <param name="Height">Height on the canvas.</param>
/// <param name="RotationDegrees">Clockwise rotation.</param>
/// <param name="Shape">1 Rectangle, 2 Round.</param>
/// <param name="AreaName">Which area it is in, by name. Null for none.</param>
/// <param name="IsBookable">False for the bar stools that only ever take walk-ins.</param>
/// <param name="IsFree">Whether anybody is sitting at it right now.</param>
public sealed record PublicFloorTable(
    string Label,
    int Seats,
    int X,
    int Y,
    int Width,
    int Height,
    double RotationDegrees,
    TableShape Shape,
    string? AreaName,
    bool IsBookable,
    bool IsFree);

/// <summary>
/// What a link to this branch should look like when it is pasted into WhatsApp or Telegram.
/// </summary>
/// <remarks>
/// <para>
/// Served by the backend so the web page and any future renderer do not each invent their own. The
/// frontend turns these into Open Graph and Twitter card tags.
/// </para>
/// <para>
/// <b>The free-table count is deliberately not in here.</b> A card is scraped once and cached by
/// whichever chat app fetched it, for hours - so a live number would be frozen at whatever it was
/// when somebody first pasted the link, and "4 tables free" three hours stale is worse than no
/// number at all. The venue description does not move.
/// </para>
/// </remarks>
/// <param name="Title">Venue and branch, as one line.</param>
/// <param name="Description">What the venue is. Stable enough to cache.</param>
/// <param name="ImageUrl">The branch cover photo, or the venue's, or null.</param>
/// <param name="CanonicalPath">The path this branch lives at, for <c>og:url</c>.</param>
/// <param name="Locale">The locale tag to advertise.</param>
public sealed record PublicBranchMeta(
    string Title,
    string Description,
    string? ImageUrl,
    string CanonicalPath,
    string Locale);

/// <summary>
/// The anonymous, read-only view of the venue estate.
/// </summary>
/// <remarks>
/// <para>
/// Every diner today has to install an app before they can see anything, which is the wrong shape
/// for a product whose growth channel is a venue putting a link in its Instagram bio - and the wrong
/// shape for a tourist, who will not install a Yerevan-only app to find out whether a table is free.
/// </para>
/// <para>
/// <b>Strictly read-only about the venue, and blind to everything else.</b> No staff, no tabs, no
/// other diners, and no floor state beyond what somebody in the doorway can see. A suspended or
/// inactive branch is a 404 from every route here rather than an empty result: "this venue is
/// suspended" is not a fact a public page should publish about a customer.
/// </para>
/// </remarks>
public interface IPublicVenueQuery
{
    /// <summary>Active, non-suspended venues with their branches, for the browse case.</summary>
    Task<IReadOnlyList<PublicVenueCard>> GetVenuesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One branch, addressed by its slug pair.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// No such pairing, or the branch is inactive or its venue is suspended or deleted. The three
    /// are one answer on purpose.
    /// </exception>
    Task<PublicBranchPage> GetBranchAsync(
        string venueSlug,
        string branchSlug,
        CancellationToken cancellationToken = default);

    /// <summary>The branch's menu, complete items only, exactly as the diner app receives it.</summary>
    /// <exception cref="KeyNotFoundException">No such branch, or it is not open for business.</exception>
    Task<BranchMenuView> GetMenuAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>The card a chat app renders when the link is pasted.</summary>
    /// <exception cref="KeyNotFoundException">No such branch, or it is not open for business.</exception>
    Task<PublicBranchMeta> GetMetaAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms a branch may be addressed publicly at all, and returns it.
    /// </summary>
    /// <remarks>
    /// Used by the availability route, which reuses the existing read model rather than growing a
    /// second implementation - so the only thing it needs from here is the 404 rule.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No such branch, or it is not open for business.</exception>
    Task RequirePublicBranchAsync(Guid branchId, CancellationToken cancellationToken = default);
}
