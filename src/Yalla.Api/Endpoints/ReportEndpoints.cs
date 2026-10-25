using System.Globalization;
using Yalla.Api.Authorization;
using Yalla.Application.Reports;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The five report groups. <c>ManagerOrAbove</c> within scope.
/// </summary>
/// <remarks>
/// <para>
/// Everything these read was already being logged and none of it was queryable: <c>TableSession</c>
/// has every occupancy with its source, party size and duration; <c>Reservation</c> has every
/// booking and its outcome; the tab tables have the money and the items. This is the answer to
/// "what am I paying for" in month three.
/// </para>
/// <para>
/// <b>Every range is in the branch's local dates.</b> A report for "yesterday" computed on UTC
/// boundaries is wrong by four hours in Yerevan - every day, and in the direction that moves the
/// late sittings into the wrong day.
/// </para>
/// <para>
/// <b>Every numeric report compares against the previous equivalent period.</b> "Covers last night"
/// means nothing without "and the Friday before".
/// </para>
/// </remarks>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}/reports")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/occupancy", OccupancyAsync)
            .WithName("getOccupancyReport")
            .WithSummary("How full the room was, and how long people actually stayed")
            .WithDescription(
                "Tables occupied by hour of day and by weekday, seats filled against capacity, and "
                + "walk-ins against bookings.\n\n"
                + "**`turnTime` is a distribution, not an average, and it is the most useful number "
                + "in the whole set.** A cafe whose policy says 120 minutes and whose real median is "
                + "165 is refusing a 20:00 sitting because it believes the 18:00 one ends at 20:00 - "
                + "and half the time it does not. It is losing bookings it has no other way to see. "
                + "An average hides exactly that: a fast lunch and a slow dinner average to something "
                + "plausible and describe neither service. Read `medianMinutes`, `p90Minutes` and "
                + "`overPolicyFraction` together.\n\n"
                + "A sitting is counted in **every hour it spans**, not only the one it started in: "
                + "\"how busy is the room at eight\" is a question about occupancy, and counting "
                + "arrivals makes a restaurant look empty at its busiest hour.\n\n"
                + "`turnTime.buckets` are half-hour bars, `upToMinutes` 60 to 240 (each up to and including) "
                + "and an open-ended last one. `walkInGuests` and `reservationGuests` split the people, not the "
                + "sittings; `turnsPerTable` is sittings per active table per day (`activeTables`), two decimals, "
                + "both periods over today's tables.")
            .Produces<OccupancyReport>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, RangeTooLong);

        group.MapGet("/reservations", ReservationsAsync)
            .WithName("getReservationReport")
            .WithSummary("Bookings, and what became of them")
            .WithDescription(
                "Booked, seated, cancelled and no-show with their rates, late cancellations against "
                + "the branch's own deadline, and how far ahead people book.\n\n"
                + "`leadTime` is what `bookingWindowDays` should be set from - a venue taking "
                + "bookings 30 days out when nobody books more than 5 days ahead is holding tables "
                + "against demand that does not exist. Its buckets are `upToHours` 2, 6, 24, 72, 168 and an "
                + "open-ended last one, each **strictly less than** - a booking made exactly two hours ahead "
                + "is in the 6.\n\n"
                + "**`webBookingsWithoutAnApp` is the number that decides a commercial question.** "
                + "Somebody who booked from the public page and has no registered device cannot be "
                + "reached by the reminder, the late nudge or one-tap cancel - the entire no-show "
                + "story. If that number is large, an SMS or Telegram channel is worth paying for; "
                + "if it is small, the install prompt on the confirmation screen is enough. See "
                + "`docs/reports.md`.")
            .Produces<ReservationReport>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, RangeTooLong);

        group.MapGet("/revenue", RevenueAsync)
            .WithName("getRevenueReport")
            .WithSummary("What was taken, by day and by hour")
            .WithDescription(
                "Total, average tab and **average per head** - a different question from average "
                + "tab, and the one a menu is priced against. Service charge collected, cash against "
                + "in-app, and comps and discounts as their own line **with who authorised them**.\n\n"
                + "That last part is why adjustments are manager-only: an owner asking \"what did we "
                + "give away last month, and who decided\" is asking both halves.\n\n"
                + "Revenue is counted on the tab **closing**, so a sitting that starts at 23:30 and "
                + "settles at 00:40 belongs to the night it was taken - which is the day the venue "
                + "counted its drawer.\n\n"
                + "`byDay` has one entry for every local date of the range, zero days included, and "
                + "`previousByDay` the same for the previous equivalent period, so the two pair by index. "
                + "`tabs` is the closed tabs with their comparison.\n\n"
                + "**Takeaway.** Orders collected at the counter are revenue too, counted when collected: `totalAmd` "
                + "is `hallAmd` (the closed tabs, `tabs` of them) plus `takeawayAmd` (`takeawayOrders` of them); "
                + "`byDay` carries each day's `takeawayAmd` and `takeawayOrders` inside its `revenueAmd`, and `byHour` "
                + "puts an order in the hour it was collected. `cashAmd` includes the counter's cash and `cardAmd` its "
                + "card payments, so cash + card + in-app is the total. `takeawayNotCollected` counts the orders "
                + "marked not collected in the period. Average tab, per head and service charge stay the hall's. The "
                + "CSV's day rows say `hallAmd` and `takeawayAmd` apart.")
            .Produces<RevenueReport>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, RangeTooLong);

        group.MapGet("/menu", MenuAsync)
            .WithName("getMenuReport")
            .WithSummary("What sold, what did not, and what got sent back")
            .WithDescription(
                "Top items by count and by revenue - frequently two different lists - plus voids by "
                + "item and reason, because a dish voided often is either mis-described or badly "
                + "made.\n\n"
                + "**`neverOrdered` is the one report that changes behaviour.** A dish nobody orders "
                + "is inventory that spoils and menu space that could sell something else, and no "
                + "venue knows which those are: they are, by definition, the ones nobody mentions.")
            .Produces<MenuReport>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, RangeTooLong);

        group.MapGet("/staff", StaffAsync)
            .WithName("getStaffReport")
            .WithSummary("Orders entered and tables turned, per branch")
            .WithDescription(
                "Aggregated across the branch, and **deliberately not per waiter**.\n\n"
                + "The audit log has the per-person data and an owner will ask for it. A ranked list "
                + "of employees that renders itself every morning is a different product from a "
                + "report somebody requests - it is a management decision with consequences, made on "
                + "the venue's behalf by software. If a venue wants per-person numbers they can be "
                + "given a report. See `docs/reports.md`.")
            .Produces<StaffReport>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, RangeTooLong);

        return app;
    }

    private const string RangeTooLong =
        "The range is longer than a report will run; `context.max` says the limit.";

    // ---------------------------------------------------------------- handlers

    private static async Task<IResult> OccupancyAsync(
        Guid branchId, DateOnly from, DateOnly to,
        IReportQuery reports, CancellationToken ct,
        bool rollUpVenue = false, string? format = null)
    {
        var report = await reports.GetOccupancyAsync(Request(branchId, from, to, rollUpVenue), ct);

        return IsCsv(format)
            ? Csv(
                $"occupancy-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}",
                ["localHour", "sessionsInProgress"],
                report.ByHour.Select(h => new[] { h.Hour.ToString(Invariant), h.Sessions.ToString(Invariant) }))
            : Results.Ok(report);
    }

    private static async Task<IResult> ReservationsAsync(
        Guid branchId, DateOnly from, DateOnly to,
        IReportQuery reports, CancellationToken ct,
        bool rollUpVenue = false, string? format = null)
    {
        var report = await reports.GetReservationsAsync(Request(branchId, from, to, rollUpVenue), ct);

        return IsCsv(format)
            ? Csv(
                $"reservations-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}",
                ["measure", "value", "previous"],
                new[]
                {
                    Row("booked", report.Booked),
                    Row("seated", report.Seated),
                    Row("cancelled", report.Cancelled),
                    Row("noShow", report.NoShow),
                    Row("lateCancellations", report.LateCancellations),
                    Row("webBookingsWithoutAnApp", report.WebBookingsWithoutAnApp),
                    Row("systemCancelled", report.SystemCancelled ?? new Compared(0, 0)),
                })
            : Results.Ok(report);
    }

    private static async Task<IResult> RevenueAsync(
        Guid branchId, DateOnly from, DateOnly to,
        IReportQuery reports, CancellationToken ct,
        bool rollUpVenue = false, string? format = null)
    {
        var report = await reports.GetRevenueAsync(Request(branchId, from, to, rollUpVenue), ct);

        // Day by day, in the branch's own local dates and whole dram - the shape somebody pastes
        // into a spreadsheet beside their own takings. The day's total, then the hall's and the
        // takeaway counter's halves of it, each in a column of its own.
        return IsCsv(format)
            ? Csv(
                $"revenue-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}",
                ["localDate", "revenueAmd", "tabs", "hallAmd", "takeawayAmd", "takeawayOrders"],
                report.ByDay.Select(d => new[]
                {
                    d.LocalDate.ToString("yyyy-MM-dd", Invariant),
                    d.RevenueAmd.ToString(Invariant),
                    d.Tabs.ToString(Invariant),
                    (d.RevenueAmd - d.TakeawayAmd).ToString(Invariant),
                    d.TakeawayAmd.ToString(Invariant),
                    d.TakeawayOrders.ToString(Invariant),
                }))
            : Results.Ok(report);
    }

    private static async Task<IResult> MenuAsync(
        Guid branchId, DateOnly from, DateOnly to,
        IReportQuery reports, CancellationToken ct,
        bool rollUpVenue = false, string? format = null)
    {
        var report = await reports.GetMenuAsync(Request(branchId, from, to, rollUpVenue), ct);

        return IsCsv(format)
            ? Csv(
                $"menu-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}",
                ["menuItemId", "name", "category", "quantity", "revenueAmd"],
                report.TopByRevenue.Select(i => new[]
                {
                    i.MenuItemId.ToString(),
                    i.Name,
                    i.CategoryName,
                    i.Quantity.ToString(Invariant),
                    i.RevenueAmd.ToString(Invariant),
                }))
            : Results.Ok(report);
    }

    private static async Task<IResult> StaffAsync(
        Guid branchId, DateOnly from, DateOnly to,
        IReportQuery reports, CancellationToken ct,
        bool rollUpVenue = false, string? format = null)
    {
        var report = await reports.GetStaffAsync(Request(branchId, from, to, rollUpVenue), ct);

        return IsCsv(format)
            ? Csv(
                $"staff-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}",
                ["measure", "value", "previous"],
                new[] { Row("ordersEntered", report.OrdersEntered), Row("tablesTurned", report.TablesTurned) })
            : Results.Ok(report);
    }

    // ---------------------------------------------------------------- CSV

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static ReportRequest Request(Guid branchId, DateOnly from, DateOnly to, bool rollUpVenue) =>
        new(branchId, from, to, rollUpVenue);

    private static bool IsCsv(string? format) =>
        string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);

    private static string[] Row(string measure, Compared value) =>
    [
        measure,
        value.Value.ToString(Invariant),

        // Empty rather than zero when there is no prior period. A spreadsheet reading "0" would
        // compute a 100% fall out of a venue's first week.
        value.Previous?.ToString(Invariant) ?? string.Empty,
    ];

    /// <summary>
    /// A CSV file, with the same numbers as the JSON - whole dram and local dates, so the file matches what the venue
    /// counts and what the screen said. Written by <see cref="CsvFile"/>, the one writer every export shares.
    /// </summary>
    private static IResult Csv(string fileName, IReadOnlyList<string> header, IEnumerable<string[]> rows) =>
        CsvFile.Result(fileName, header, rows);
}
