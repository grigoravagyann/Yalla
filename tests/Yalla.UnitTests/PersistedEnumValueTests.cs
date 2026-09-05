using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.UnitTests;

/// <summary>
/// Every persisted enum member, pinned to the exact integer already on disk.
/// </summary>
/// <remarks>
/// <para>
/// Enums are stored as <c>int</c>. That makes a member's <i>number</i> part of the database schema,
/// and inserting or removing one in the middle silently rewrites the meaning of every existing row:
/// an <c>Occupied</c> table starts reading back as <c>OutOfService</c>, a <c>Completed</c> booking as
/// <c>Seated</c>. Nothing fails, nothing logs, and the floor plan is simply wrong.
/// </para>
/// <para>
/// So this test is deliberately tedious. It exists to fail loudly the moment somebody tidies an
/// enum, and the failure message names the member and both numbers. If you are here because it
/// failed: do not change the test to match the code. Put the member back on its old number and give
/// the new member a fresh one, leaving a gap where anything was retired. A gap costs nothing; a
/// shift costs the data.
/// </para>
/// </remarks>
public class PersistedEnumValueTests
{
    [Fact]
    public void Table_status_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)TableStatus.Free);
        Assert.Equal(2, (int)TableStatus.Held);
        // 3 was Reserved, retired in Prompt 2 and derived at read time now. Never reuse it.
        Assert.Equal(4, (int)TableStatus.Occupied);
        Assert.Equal(5, (int)TableStatus.OutOfService);

        AssertNothingOccupies<TableStatus>(3, "Reserved");
    }

    [Fact]
    public void Reservation_status_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)ReservationStatus.PendingApproval);
        Assert.Equal(2, (int)ReservationStatus.Confirmed);
        // 3 was Late, retired in Prompt 2 and derived from the clock now. Never reuse it.
        Assert.Equal(4, (int)ReservationStatus.Seated);
        Assert.Equal(5, (int)ReservationStatus.Completed);
        Assert.Equal(6, (int)ReservationStatus.CancelledByDiner);
        Assert.Equal(7, (int)ReservationStatus.CancelledByVenue);
        Assert.Equal(8, (int)ReservationStatus.NoShow);

        AssertNothingOccupies<ReservationStatus>(3, "Late");
    }

    /// <summary>
    /// Prompt 6 added <c>PlatformAdmin</c>. It took the vacant 0 rather than displacing anybody, so
    /// every existing staff row still means what it meant.
    /// </summary>
    [Fact]
    public void Staff_role_values_are_pinned_and_the_platform_tier_displaced_nobody()
    {
        Assert.Equal(0, (int)StaffRole.PlatformAdmin);
        Assert.Equal(1, (int)StaffRole.Owner);
        Assert.Equal(2, (int)StaffRole.Manager);
        Assert.Equal(3, (int)StaffRole.Waiter);
        Assert.Equal(4, (int)StaffRole.Kitchen);
    }

    /// <summary>
    /// Seniority is its own map, so the enum's storage numbers can never be mistaken for authority.
    /// </summary>
    [Fact]
    public void Seniority_is_declared_rather_than_inferred_from_the_stored_numbers()
    {
        Assert.True(StaffRoleRules.Outranks(StaffRole.PlatformAdmin, StaffRole.Owner));
        Assert.True(StaffRoleRules.Outranks(StaffRole.Owner, StaffRole.Manager));
        Assert.True(StaffRoleRules.Outranks(StaffRole.Manager, StaffRole.Waiter));
        Assert.False(StaffRoleRules.Outranks(StaffRole.Waiter, StaffRole.Manager));
        Assert.False(StaffRoleRules.Outranks(StaffRole.Owner, StaffRole.Owner));

        // Every role has a declared rank; none falls through to a default.
        foreach (var role in Enum.GetValues<StaffRole>())
        {
            _ = StaffRoleRules.RankOf(role);
        }
    }

    [Fact]
    public void Actor_type_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)ActorType.Diner);
        Assert.Equal(2, (int)ActorType.Staff);
        Assert.Equal(3, (int)ActorType.System);
    }

    [Fact]
    public void Principal_type_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)PrincipalType.TabParticipant);
        Assert.Equal(2, (int)PrincipalType.Diner);
        Assert.Equal(3, (int)PrincipalType.StaffDevice);
        Assert.Equal(4, (int)PrincipalType.StaffSession);
        Assert.Equal(5, (int)PrincipalType.VenueUser);
    }

    [Fact]
    public void Refresh_token_subject_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)RefreshTokenSubject.Diner);
        Assert.Equal(2, (int)RefreshTokenSubject.VenueUser);
    }

    [Fact]
    public void Venue_and_table_shape_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)VenueType.Cafe);
        Assert.Equal(2, (int)VenueType.Restaurant);

        Assert.Equal(1, (int)TableShape.Rectangle);
        Assert.Equal(2, (int)TableShape.Round);

        Assert.Equal(1, (int)SubscriptionTier.Free);
        Assert.Equal(2, (int)SubscriptionTier.Paid);
    }

    [Fact]
    public void Session_and_stay_hint_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)TableSessionSource.Reservation);
        Assert.Equal(2, (int)TableSessionSource.WalkIn);

        Assert.Equal(1, (int)StayHint.OneHour);
        Assert.Equal(2, (int)StayHint.TwoHours);
        Assert.Equal(3, (int)StayHint.ThreeHoursPlus);
    }

    [Fact]
    public void Tab_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)TabStatus.Open);
        Assert.Equal(2, (int)TabStatus.Closing);
        Assert.Equal(3, (int)TabStatus.Closed);
        Assert.Equal(4, (int)TabStatus.Abandoned);

        Assert.Equal(1, (int)SettlementMode.HostPaysEverything);
        Assert.Equal(2, (int)SettlementMode.EveryonePaysOwnItems);
        Assert.Equal(3, (int)SettlementMode.AnyonePaysAnyAmount);

        Assert.Equal(1, (int)ParticipantRole.Host);
        Assert.Equal(2, (int)ParticipantRole.Guest);

        Assert.Equal(1, (int)ParticipantStatus.PendingApproval);
        Assert.Equal(2, (int)ParticipantStatus.Approved);
        Assert.Equal(3, (int)ParticipantStatus.Removed);

        Assert.Equal(1, (int)TabOrderStatus.New);
        Assert.Equal(2, (int)TabOrderStatus.InKitchen);
        Assert.Equal(3, (int)TabOrderStatus.Ready);
        Assert.Equal(4, (int)TabOrderStatus.Served);
        Assert.Equal(5, (int)TabOrderStatus.Voided);
    }

    [Fact]
    public void Payment_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)PaymentMethod.Idram);
        Assert.Equal(2, (int)PaymentMethod.Telcell);
        Assert.Equal(3, (int)PaymentMethod.Card);
        Assert.Equal(4, (int)PaymentMethod.Cash);

        Assert.Equal(1, (int)PaymentStatus.Reserved);
        Assert.Equal(2, (int)PaymentStatus.Succeeded);
        Assert.Equal(3, (int)PaymentStatus.Failed);
        Assert.Equal(4, (int)PaymentStatus.Released);
    }

    [Fact]
    public void Menu_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(0, (int)SpiceLevel.NotSpicy);
        Assert.Equal(1, (int)SpiceLevel.Mild);
        Assert.Equal(2, (int)SpiceLevel.Medium);
        Assert.Equal(3, (int)SpiceLevel.Hot);
    }

    /// <summary>
    /// <c>DerivedTableState</c> is computed per request and never stored, so its numbers are a wire
    /// contract rather than a schema one - but three clients switch on them, so they are pinned too.
    /// </summary>
    [Fact]
    public void Derived_table_state_values_are_pinned_because_clients_switch_on_them()
    {
        Assert.Equal(1, (int)DerivedTableState.Free);
        Assert.Equal(2, (int)DerivedTableState.ReservedSoon);
        Assert.Equal(3, (int)DerivedTableState.Held);
        Assert.Equal(4, (int)DerivedTableState.Occupied);
        Assert.Equal(5, (int)DerivedTableState.OutOfService);
    }

    /// <summary>
    /// Every enum this test knows about is covered. Catches the case nobody thinks of: a brand-new
    /// persisted enum added without a line in here.
    /// </summary>
    [Fact]
    public void Every_domain_enum_is_covered_by_this_test()
    {
        var declared = typeof(TableStatus).Assembly
            .GetTypes()
            .Where(t => t.IsEnum && t.Namespace == "Yalla.Domain.Enums")
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        string[] covered =
        [
            nameof(ActorType), nameof(DerivedTableState), nameof(ParticipantRole), nameof(ParticipantStatus),
            nameof(PaymentMethod), nameof(PaymentStatus), nameof(PrincipalType), nameof(RefreshTokenSubject),
            nameof(ReservationStatus), nameof(SettlementMode), nameof(SpiceLevel), nameof(StaffRole),
            nameof(StayHint), nameof(SubscriptionTier), nameof(TabOrderStatus), nameof(TabStatus),
            nameof(TableSessionSource), nameof(TableShape), nameof(TableStatus), nameof(VenueType),
        ];

        Assert.Equal(covered.OrderBy(n => n), declared);
    }

    /// <summary>Asserts a retired number was left vacant rather than handed to somebody else.</summary>
    private static void AssertNothingOccupies<TEnum>(int retiredValue, string retiredName)
        where TEnum : struct, Enum
    {
        Assert.False(
            Enum.IsDefined(typeof(TEnum), retiredValue),
            $"{typeof(TEnum).Name} = {retiredValue} was {retiredName} and is permanently retired. "
            + "Rows written before it was removed still hold it, so giving it to another member "
            + "would change what those rows mean. Use the next free number instead.");
    }
}
