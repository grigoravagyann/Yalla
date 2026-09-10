using Yalla.Application.BranchSettings;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The pure rules of the platform task: the role invariant, the role hierarchy, the opening-hours
/// and floor-plan checks, the policy bounds. No database, no HTTP.
/// </summary>
public class PlatformAdminRulesTests
{
    // ------------------------------------------------------------ 1. the scope invariant

    [Fact]
    public void A_platform_admin_with_a_venue_id_is_invalid()
    {
        var construct = () => new StaffMember(
            Guid.CreateVersion7(), "Ani Admin", "+37400000001", StaffRole.PlatformAdmin, "hash");

        var refused = Assert.Throws<ArgumentException>(construct);
        Assert.Contains("platform admin", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StaffRole.Owner)]
    [InlineData(StaffRole.Manager)]
    [InlineData(StaffRole.Waiter)]
    [InlineData(StaffRole.Kitchen)]
    public void Every_other_role_requires_a_venue(StaffRole role)
    {
        var construct = () => new StaffMember(Guid.Empty, "Aram", "+37400000002", role, "hash");

        Assert.Throws<ArgumentException>(construct);
    }

    [Fact]
    public void A_platform_admin_is_built_with_no_venue_no_branch_and_a_password()
    {
        var admin = StaffMember.PlatformAdmin("Ani Admin", "+37400000001", "Ani@Yalla.app", "pw-hash");

        Assert.Null(admin.VenueId);
        Assert.Null(admin.BranchId);
        Assert.True(admin.IsPlatformAdmin);
        Assert.Equal("ani@yalla.app", admin.Email);
        Assert.True(admin.HasPasswordCredentials);
        Assert.Equal(StaffMember.NoPin, admin.PinHash);
    }

    [Fact]
    public void A_venue_staff_member_cannot_be_promoted_to_platform_admin_and_the_role_is_left_as_it_was()
    {
        var manager = new StaffMember(Guid.CreateVersion7(), "Nune", "+37400000003", StaffRole.Manager, "hash");

        Assert.Throws<ArgumentException>(() => manager.SetRole(StaffRole.PlatformAdmin));
        Assert.Equal(StaffRole.Manager, manager.Role);
    }

    [Fact]
    public void A_platform_admin_cannot_be_given_a_venue_role_or_a_branch()
    {
        var admin = StaffMember.PlatformAdmin("Ani Admin", "+37400000001", "ani@yalla.app", "pw-hash");

        Assert.Throws<ArgumentException>(() => admin.SetRole(StaffRole.Owner));
        Assert.Throws<ArgumentException>(() => admin.AssignToBranch(Guid.CreateVersion7()));
        Assert.Equal(StaffRole.PlatformAdmin, admin.Role);
    }

    // ------------------------------------------------------------ 16. the role hierarchy

    [Theory]
    [InlineData(StaffRole.Manager, StaffRole.Waiter, true)]
    [InlineData(StaffRole.Manager, StaffRole.Kitchen, true)]
    [InlineData(StaffRole.Manager, StaffRole.Manager, false)]
    [InlineData(StaffRole.Manager, StaffRole.Owner, false)]
    [InlineData(StaffRole.Owner, StaffRole.Manager, true)]
    [InlineData(StaffRole.Owner, StaffRole.Owner, true)]
    [InlineData(StaffRole.Owner, StaffRole.PlatformAdmin, false)]
    [InlineData(StaffRole.PlatformAdmin, StaffRole.Owner, true)]
    [InlineData(StaffRole.PlatformAdmin, StaffRole.PlatformAdmin, false)]
    [InlineData(StaffRole.Waiter, StaffRole.Kitchen, false)]
    public void Who_may_assign_which_role(StaffRole actor, StaffRole target, bool allowed) =>
        Assert.Equal(allowed, StaffRoleRules.MayAssign(actor, target));

    // ------------------------------------------------------------ reserved slugs

    /// <summary>
    /// The console's own first path segments cannot name a venue, because the web app boots the
    /// console for those and the public page for everything else - a venue called
    /// <c>reset-password</c> would sit unreachable under the address every reset link points at.
    /// </summary>
    [Theory]
    [InlineData("assets")]
    [InlineData("dev")]
    [InlineData("fonts")]
    [InlineData("platform")]
    [InlineData("reset-password")]
    [InlineData("sign-in")]
    [InlineData("staff")]
    [InlineData("venue")]
    public void A_slug_the_console_answers_to_cannot_name_a_venue(string reserved)
    {
        var refused = Assert.Throws<ArgumentException>(() => new Venue("Impostor", VenueType.Cafe, reserved));

        Assert.Equal("slug", refused.ParamName);
        Assert.Contains(reserved, refused.Message, StringComparison.Ordinal);

        // Case is normalised before the list is consulted, so the capitalised spelling is no way round it.
        Assert.Throws<ArgumentException>(() => new Venue("Impostor", VenueType.Cafe, reserved.ToUpperInvariant()));

        var existing = new Venue("Lumen", VenueType.Cafe, "lumen-coffee");
        Assert.Throws<ArgumentException>(() => existing.SetSlug(reserved));
        Assert.Equal("lumen-coffee", existing.Slug);
    }

    [Fact]
    public void An_ordinary_slug_is_accepted()
    {
        var venue = new Venue("Lumen", VenueType.Cafe, "lumen-coffee");

        Assert.Equal("lumen-coffee", venue.Slug);
    }

    // ------------------------------------------------------------ 9. opening hours

    [Fact]
    public void Overlapping_opening_hours_within_a_day_are_rejected_naming_the_block()
    {
        var blocks = new List<OpeningHoursBlock>
        {
            new(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(15, 0)),
            new(DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(23, 0)),
        };

        var refused = Assert.Throws<FieldValidationException>(() => OpeningHoursRules.Normalise(blocks));

        Assert.Contains("Monday", refused.Message);

        // The body is an array, so the field is indexed - which is what lets a form rendering a row
        // per block put the message on the offending row rather than at the top.
        var violation = Assert.Single(refused.Violations);

        Assert.Equal("[1].opensAt", violation.Field);
        Assert.Equal(new TimeOnly(14, 0), violation.Value);
        Assert.Equal(FieldBounds.Conflict, violation.Bound);
    }

    [Fact]
    public void Touching_blocks_are_allowed_and_closes_next_day_is_derived()
    {
        var blocks = new List<OpeningHoursBlock>
        {
            new(DayOfWeek.Friday, new TimeOnly(9, 0), new TimeOnly(15, 0)),
            new(DayOfWeek.Friday, new TimeOnly(15, 0), new TimeOnly(1, 0)),
            new(DayOfWeek.Saturday, new TimeOnly(10, 0), new TimeOnly(23, 0)),
        };

        var normalised = OpeningHoursRules.Normalise(blocks);

        Assert.Equal(3, normalised.Count);
        Assert.False(normalised[0].ClosesNextDay);
        Assert.True(normalised[1].ClosesNextDay);
        Assert.False(normalised[2].ClosesNextDay);
    }

    [Fact]
    public void A_block_running_past_midnight_overlaps_a_later_block_on_the_same_day()
    {
        var blocks = new List<OpeningHoursBlock>
        {
            new(DayOfWeek.Friday, new TimeOnly(18, 0), new TimeOnly(2, 0)),
            new(DayOfWeek.Friday, new TimeOnly(23, 0), new TimeOnly(23, 30)),
        };

        Assert.Throws<FieldValidationException>(() => OpeningHoursRules.Normalise(blocks));
    }

    /// <summary>
    /// <b>Test 8, for the hours form.</b> Three bad blocks come back as three violations.
    /// </summary>
    /// <remarks>
    /// Refusing the first and stopping made an owner fixing a week's hours submit once per mistake.
    /// </remarks>
    [Fact]
    public void Three_bad_opening_blocks_are_all_reported_each_against_its_own_row()
    {
        var blocks = new List<OpeningHoursBlock>
        {
            new(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(15, 0)),
            new(DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(23, 0)),   // overlaps the first
            new(DayOfWeek.Tuesday, new TimeOnly(12, 0), new TimeOnly(12, 0)),  // no length
            new(DayOfWeek.Friday, new TimeOnly(10, 0), new TimeOnly(18, 0)),
            new(DayOfWeek.Friday, new TimeOnly(17, 0), new TimeOnly(23, 0)),   // overlaps the fourth
        };

        var refused = Assert.Throws<FieldValidationException>(() => OpeningHoursRules.Normalise(blocks));

        Assert.Equal(3, refused.Violations.Count);

        Assert.Equal(
            ["[2].closesAt", "[1].opensAt", "[4].opensAt"],
            refused.Violations.Select(v => v.Field));

        // And the same complaints arrive keyed by field for anything reading RFC 7807's errors map.
        var errors = refused.AsErrorMap();

        Assert.Equal(3, errors.Count);
        Assert.All(errors.Values, messages => Assert.NotEmpty(messages));
    }

    // ------------------------------------------------------------ 10 and 11. the floor plan

    [Fact]
    public void A_plan_with_a_table_outside_the_canvas_is_rejected_naming_the_tables()
    {
        var tables = new List<FloorTableInput>
        {
            Table("1", x: 10, y: 10),
            Table("7", x: 950, y: 10, width: 100),   // runs off the right edge
            Table("9", x: 10, y: 680, height: 50),   // runs off the bottom
        };

        var result = FloorPlanRules.Validate(1000, 700, tables);

        Assert.False(result.IsValid);
        Assert.Equal(["7", "9"], result.TablesOutsideCanvas);
        Assert.Contains(result.Errors, e => e.Contains("7") && e.Contains("9"));
    }

    [Fact]
    public void A_plan_with_overlapping_tables_is_valid_with_a_warning()
    {
        var tables = new List<FloorTableInput>
        {
            Table("1", x: 100, y: 100),
            Table("2", x: 150, y: 150),   // sits on top of table 1
            Table("3", x: 500, y: 500),
        };

        var result = FloorPlanRules.Validate(1000, 700, tables);

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("1", warning);
        Assert.Contains("2", warning);
    }

    [Fact]
    public void Repeated_labels_are_an_error()
    {
        var tables = new List<FloorTableInput> { Table("7", x: 10, y: 10), Table("7", x: 300, y: 300) };

        var result = FloorPlanRules.Validate(1000, 700, tables);

        Assert.False(result.IsValid);
        Assert.Equal(["7"], result.DuplicateLabels);
    }

    // ------------------------------------------------------------ policy bounds

    /// <summary>
    /// <b>Test 7.</b> A bounds refusal carries the field, the bound it broke and the value sent.
    /// </summary>
    /// <remarks>
    /// It used to carry prose and nothing else - the <c>ParamName</c> was the English label "Turn
    /// time" - so the console kept a label-to-input lookup table keyed on server text while its own
    /// labels were localised. The moment either side was translated the table silently stopped
    /// matching and every message fell back to form-level.
    /// </remarks>
    [Theory]
    [InlineData(5)]
    [InlineData(12 * 60)]
    public void A_turn_time_of_five_minutes_or_twelve_hours_is_refused_naming_the_field(int turnTime)
    {
        var command = Policy(turnTime);

        var refused = Assert.Throws<FieldValidationException>(() => command.ToPolicy());

        Assert.Contains("Turn time", refused.Message);
        Assert.Contains(turnTime.ToString(), refused.Message);

        var violation = Assert.Single(refused.Violations);

        // The wire name, in the casing the OpenAPI schema uses - not a label.
        Assert.Equal("turnTimeMinutes", violation.Field);
        Assert.Equal(turnTime, violation.Value);
        Assert.Equal(ReservationPolicyLimits.MinTurnTimeMinutes, violation.Min);
        Assert.Equal(ReservationPolicyLimits.MaxTurnTimeMinutes, violation.Max);
        Assert.Equal(turnTime < ReservationPolicyLimits.MinTurnTimeMinutes ? FieldBounds.Min : FieldBounds.Max, violation.Bound);
    }

    /// <summary>
    /// <b>Test 8.</b> A request breaking three bounds reports three, not the first.
    /// </summary>
    /// <remarks>
    /// The policy constructor throws on the first bad number it meets, so this only works because
    /// the bounds are checked against the raw values before the policy is built. A form that
    /// surfaces one error at a time makes an owner submit six times.
    /// </remarks>
    [Fact]
    public void A_policy_breaking_three_bounds_reports_all_three()
    {
        var command = Policy(5) with
        {
            BookingWindowDays = 0,
            ServiceChargePercent = 150m,
        };

        var refused = Assert.Throws<FieldValidationException>(() => command.ToPolicy());

        Assert.Equal(3, refused.Violations.Count);

        Assert.Equal(
            ["turnTimeMinutes", "bookingWindowDays", "serviceChargePercent"],
            refused.Violations.Select(v => v.Field));

        // context.field is the first, for a form that can only highlight one input.
        Assert.Equal("turnTimeMinutes", refused.Field);

        // And every one of them carries what it broke and what was sent.
        Assert.All(refused.Violations, v =>
        {
            Assert.NotNull(v.Bound);
            Assert.NotNull(v.Value);
            Assert.False(string.IsNullOrWhiteSpace(v.Message));
        });

        Assert.Equal(150m, refused.Violations.Single(v => v.Field == "serviceChargePercent").Value);
        Assert.Equal(0, refused.Violations.Single(v => v.Field == "bookingWindowDays").Value);
    }

    [Fact]
    public void A_sane_policy_passes_the_bounds_unchanged()
    {
        var policy = Policy(90).ToPolicy();

        Assert.Equal(90, policy.TurnTimeMinutes);
    }

    private static ReservationPolicyCommand Policy(int turnTime) => new(
        TurnTimeMinutes: turnTime,
        BufferMinutes: 15,
        GraceMinutes: 15,
        LateNudgeAfterMinutes: 10,
        GraceExtensionMinutes: 10,
        MinLeadMinutes: 30,
        BookingWindowDays: 14,
        CancellationDeadlineMinutes: 120,
        AutoConfirm: true,
        ServiceChargePercent: 10m,
        PricesIncludeVat: true,
        MaxSeatOverhang: 2,
        ApprovalRequiredAbovePartySize: 8);

    private static FloorTableInput Table(string label, int x, int y, int width = 90, int height = 90) =>
        new(null, label, 4, x, y, width, height, 0d, TableShape.Round);
}
