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
    public void Telegram_verification_status_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)TelegramVerificationStatus.Pending);
        Assert.Equal(2, (int)TelegramVerificationStatus.Verified);
        Assert.Equal(3, (int)TelegramVerificationStatus.Consumed);
        Assert.Equal(4, (int)TelegramVerificationStatus.Expired);
    }

    [Fact]
    public void Staff_role_values_are_pinned_and_the_platform_tier_displaced_nobody()
    {
        Assert.Equal(0, (int)StaffRole.Unknown);
        Assert.Equal(1, (int)StaffRole.Owner);
        Assert.Equal(2, (int)StaffRole.Manager);
        Assert.Equal(3, (int)StaffRole.Waiter);
        Assert.Equal(4, (int)StaffRole.Kitchen);
        Assert.Equal(5, (int)StaffRole.PlatformAdmin);

        // Added after the platform tier took 5. Manager keeps 2 and now means a branch manager: the
        // rows that meant "venue-wide manager" were moved to 6 by the migration, not renumbered.
        Assert.Equal(6, (int)StaffRole.GeneralManager);
    }

    // ------------------------------------------------------------ 11 and 12. zero grants nothing

    /// <summary>
    /// <b>Test 11.</b> The value you get by forgetting is nobody, and asking its rank throws.
    /// </summary>
    /// <remarks>
    /// <c>PlatformAdmin</c> was 0, so an unset field, a deserialisation default, or an insert path
    /// that forgot to set a role produced a platform administrator. Not a hypothetical: it is what
    /// <c>default(StaffRole)</c> evaluates to in every one of those cases, and none of them looks
    /// like a security decision at the call site.
    /// </remarks>
    [Fact]
    public void The_default_staff_role_is_nobody_and_has_no_rank()
    {
        Assert.Equal(StaffRole.Unknown, default(StaffRole));

        // Absent from the seniority map on purpose. A role nobody set is a bug to surface, not a
        // permission level to resolve.
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => StaffRoleRules.RankOf(StaffRole.Unknown));

        Assert.Contains("no place in the seniority map", thrown.Message);

        // And it outranks nothing, including itself, because asking is already an error.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StaffRoleRules.Outranks(StaffRole.Unknown, StaffRole.Kitchen));
    }

    /// <summary>
    /// <b>Test 12.</b> No persisted enum may put a privileged member on zero.
    /// </summary>
    /// <remarks>
    /// The general guard, so the next enum does not repeat the mistake this one made. Zero is what
    /// an unset column, a missing JSON property and a <c>default(T)</c> all produce, so whatever
    /// sits there is the value the system hands out by accident - and that must never be the value
    /// that can do the most.
    /// </remarks>
    [Fact]
    public void No_persisted_enum_gives_its_zero_value_to_a_privileged_member()
    {
        // Names that mean "may do more than an ordinary caller". Matched on the name because the
        // point is to catch a member somebody adds later without thinking about zero.
        string[] privileged =
        [
            "PlatformAdmin", "Admin", "Owner", "Manager", "SuperUser", "Root", "Host",
        ];

        var offenders = typeof(StaffRole).Assembly
            .GetTypes()
            .Where(t => t.IsEnum && t.Namespace == "Yalla.Domain.Enums")
            .Where(t => Enum.IsDefined(t, 0))
            .Select(t => new { Enum = t.Name, Zero = Enum.GetName(t, 0)! })
            .Where(x => privileged.Contains(x.Zero, StringComparer.OrdinalIgnoreCase))
            .Select(x => $"{x.Enum}.{x.Zero}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These enums hand out a privileged member as their default: "
            + string.Join(", ", offenders)
            + ". Zero is what an unset column and a deserialisation default both produce, so it must "
            + "be the value that can do the least. Give it an Unknown member and move the privileged "
            + "one to the next free number - while there are no live rows, which is the only time it "
            + "is free.");
    }

    /// <summary>
    /// Seniority is its own map, so the enum's storage numbers can never be mistaken for authority.
    /// </summary>
    [Fact]
    public void Seniority_is_declared_rather_than_inferred_from_the_stored_numbers()
    {
        Assert.True(StaffRoleRules.Outranks(StaffRole.PlatformAdmin, StaffRole.Owner));
        Assert.True(StaffRoleRules.Outranks(StaffRole.Owner, StaffRole.GeneralManager));
        Assert.True(StaffRoleRules.Outranks(StaffRole.GeneralManager, StaffRole.Manager));
        Assert.True(StaffRoleRules.Outranks(StaffRole.Owner, StaffRole.Manager));
        Assert.True(StaffRoleRules.Outranks(StaffRole.Manager, StaffRole.Waiter));
        Assert.False(StaffRoleRules.Outranks(StaffRole.GeneralManager, StaffRole.GeneralManager));

        // GeneralManager is 6 - the highest stored number of any venue role - and outranks Manager
        // at 2. Another reason the order is declared rather than read off the enum.
        Assert.True((int)StaffRole.GeneralManager > (int)StaffRole.Kitchen);
        Assert.False(StaffRoleRules.Outranks(StaffRole.Waiter, StaffRole.Manager));
        Assert.False(StaffRoleRules.Outranks(StaffRole.Owner, StaffRole.Owner));

        // PlatformAdmin is 5 and still outranks Owner at 1, which is the whole reason seniority is
        // a declared map: the storage numbers now run the other way.
        Assert.True((int)StaffRole.PlatformAdmin > (int)StaffRole.Owner);

        // Every real role has a declared rank; none falls through to a default. Unknown is the one
        // deliberate omission and has its own test.
        foreach (var role in Enum.GetValues<StaffRole>().Where(r => r != StaffRole.Unknown))
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
    public void Venue_type_and_branch_feature_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)VenueType.Cafe);
        Assert.Equal(2, (int)VenueType.Restaurant);
        Assert.Equal(3, (int)VenueType.Bar);

        // The number is also the bit in Branches.AllowedFeatures (1 << (value - 1)): renumbering moves a stored set.
        Assert.Equal(1, (int)BranchFeature.AppBookings);
        Assert.Equal(2, (int)BranchFeature.WebBookings);
        Assert.Equal(3, (int)BranchFeature.PreOrders);
        Assert.Equal(4, (int)BranchFeature.Takeaway);
        Assert.Equal(5, (int)BranchFeature.TableService);
        Assert.Equal(6, (int)BranchFeature.GuestMessages);
        Assert.Equal(7, (int)BranchFeature.Reviews);
        Assert.Equal(8, (int)BranchFeature.StaffPhone);
        Assert.Equal(9, (int)BranchFeature.KitchenDisplay);
        Assert.Equal(10, (int)BranchFeature.Reports);
        Assert.Equal(10, Enum.GetValues<BranchFeature>().Length);

        // Stored in Branches.ReservationPolicy_BookingMode; the migration gave every existing branch 1.
        Assert.Equal(1, (int)BookingMode.ChooseTable);
        Assert.Equal(2, (int)BookingMode.TimeOnly);
        Assert.Equal(2, Enum.GetValues<BookingMode>().Length);
    }

    [Fact]
    public void Session_and_stay_hint_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)TableSessionSource.Reservation);
        Assert.Equal(2, (int)TableSessionSource.WalkIn);

        Assert.Equal(1, (int)StayHint.OneHour);
        Assert.Equal(2, (int)StayHint.TwoHours);
        Assert.Equal(3, (int)StayHint.ThreeHoursPlus);

        // Zero is a real member here, unlike every other enum in this file. Every booking that
        // predates the column reads back as Unknown, and the database default is 0 - so the value
        // has to mean "not stated" rather than be a sentinel nothing holds.
        Assert.Equal(0, (int)ReservationChannel.Unknown);
        Assert.Equal(1, (int)ReservationChannel.App);
        Assert.Equal(2, (int)ReservationChannel.Web);
        Assert.Equal(3, (int)ReservationChannel.Staff);
    }

    /// <summary>
    /// Why a booking waited and how a decline came about. Both were on the wire as numbers before
    /// they were columns - the trigger as <c>awaitingApprovalBecause</c> - so the numbers the apps
    /// already switch on are the ones stored; short notice took the next free one.
    /// </summary>
    [Fact]
    public void Approval_trigger_and_decline_reason_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)ApprovalTrigger.BranchApprovesEveryBooking);
        Assert.Equal(2, (int)ApprovalTrigger.LargeParty);
        Assert.Equal(3, (int)ApprovalTrigger.NoShowHistory);
        Assert.Equal(4, (int)ApprovalTrigger.ShortNotice);

        // Booking times: the guest before is asked whether they will have left in time.
        Assert.Equal(5, (int)ApprovalTrigger.EarlierGuestConsent);

        // Booking without a proved number (2026-10-20).
        Assert.Equal(6, (int)ApprovalTrigger.UnverifiedPhone);

        Assert.Equal(1, (int)DeclineReason.StaffDeclined);
        Assert.Equal(2, (int)DeclineReason.NoAnswer);
        Assert.Equal(3, (int)DeclineReason.EarlierGuestDeclined);
        Assert.Equal(4, (int)DeclineReason.EarlierGuestNoAnswer);
    }

    [Fact]
    public void Table_share_request_status_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)TableShareRequestStatus.Pending);
        Assert.Equal(2, (int)TableShareRequestStatus.Accepted);
        Assert.Equal(3, (int)TableShareRequestStatus.Declined);
        Assert.Equal(4, (int)TableShareRequestStatus.Expired);
        Assert.Equal(5, (int)TableShareRequestStatus.Withdrawn);
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
            nameof(ActivityActorKind), nameof(ActivityCategory), nameof(ActorType), nameof(AdjustmentKind), nameof(ApprovalTrigger), nameof(BillScope), nameof(BookingMode), nameof(BranchFeature), nameof(DeclineReason),
            nameof(DerivedTableState), nameof(ParticipantRole),
            nameof(ParticipantStatus), nameof(PaymentMethod), nameof(PaymentStatus), nameof(PrincipalType),
            nameof(RefreshTokenSubject), nameof(ReservationChannel), nameof(ReservationStatus),
            nameof(ServiceRequestPreset),
            nameof(SettlementMode), nameof(SpiceLevel), nameof(StaffDeviceBrowser), nameof(StaffDeviceKind), nameof(StaffDevicePlatform), nameof(StaffPushKinds), nameof(StaffRole), nameof(StayHint),
            nameof(TabAdmission), nameof(TabAdmissionReason), nameof(TabEventType),
            nameof(TabOrderStatus), nameof(TabStatus), nameof(TableChangeSignal),
            nameof(TableSessionSource), nameof(TableShareRequestStatus), nameof(TableStatus),
            nameof(TelegramVerificationStatus),
            nameof(VenueType),
        ];

        Assert.Equal(covered.OrderBy(n => n), declared);
    }

    // ------------------------------------------------------------ Prompt 8: ordering and billing

    [Fact]
    public void Adjustment_kind_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)AdjustmentKind.Discount);
        Assert.Equal(2, (int)AdjustmentKind.Comp);
    }

    [Fact]
    public void Service_request_preset_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)ServiceRequestPreset.Napkins);
        Assert.Equal(2, (int)ServiceRequestPreset.Water);
        Assert.Equal(3, (int)ServiceRequestPreset.TheBill);
        Assert.Equal(4, (int)ServiceRequestPreset.Other);
    }

    /// <summary>
    /// Stored on a request for the bill, and the number <c>CK_ServiceRequests_Bill</c> reads the
    /// preset by - so <c>TheBill</c> moving off 3 would break the constraint as well as the rows.
    /// </summary>
    [Fact]
    public void Bill_scope_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)BillScope.WholeBill);
        Assert.Equal(2, (int)BillScope.MyShare);
    }

    /// <summary>
    /// The event stream's types. These reach clients that are catching up, so a renumbering would
    /// make an old app build misread a tab rather than merely fail on it.
    /// </summary>
    [Fact]
    public void Tab_event_type_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)TabEventType.TabOpened);
        Assert.Equal(2, (int)TabEventType.ParticipantJoined);
        Assert.Equal(3, (int)TabEventType.ParticipantApproved);
        Assert.Equal(4, (int)TabEventType.ParticipantRejected);
        Assert.Equal(5, (int)TabEventType.ParticipantRemoved);
        Assert.Equal(6, (int)TabEventType.ParticipantPermissionsChanged);
        Assert.Equal(7, (int)TabEventType.ParticipantRenamed);
        Assert.Equal(8, (int)TabEventType.HostReassigned);
        Assert.Equal(9, (int)TabEventType.SettlementModeChanged);
        Assert.Equal(10, (int)TabEventType.OrderPlaced);
        Assert.Equal(11, (int)TabEventType.OrderStatusChanged);
        Assert.Equal(12, (int)TabEventType.LineVoided);
        Assert.Equal(13, (int)TabEventType.AdjustmentAdded);
        Assert.Equal(14, (int)TabEventType.AdjustmentVoided);
        Assert.Equal(15, (int)TabEventType.PaymentRecorded);
        Assert.Equal(16, (int)TabEventType.ServiceRequested);
        Assert.Equal(17, (int)TabEventType.ServiceRequestAcknowledged);
        Assert.Equal(18, (int)TabEventType.TabClosing);
        Assert.Equal(19, (int)TabEventType.TabClosed);
        Assert.Equal(20, (int)TabEventType.TabAbandoned);
        Assert.Equal(21, (int)TabEventType.TabAdmitted);
        Assert.Equal(22, (int)TabEventType.TabAdmissionRejected);
    }

    /// <summary>
    /// Admission (2026-10-20): stored on every tab, on the wire as <c>admission.status</c> and
    /// <c>admittedBecause</c>, and on the branch stream as <c>signal</c>. Existing rows were written as 2.
    /// </summary>
    [Fact]
    public void Tab_admission_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)TabAdmission.AwaitingAdmission);
        Assert.Equal(2, (int)TabAdmission.Admitted);
        Assert.Equal(3, (int)TabAdmission.Rejected);

        Assert.Equal(1, (int)TabAdmissionReason.Waiter);
        Assert.Equal(2, (int)TabAdmissionReason.VerifiedHost);
        Assert.Equal(3, (int)TabAdmissionReason.OpenedByStaff);
        Assert.Equal(4, (int)TabAdmissionReason.Booker);
        Assert.Equal(5, (int)TabAdmissionReason.NotRequired);

        Assert.Equal(1, (int)TableChangeSignal.TabAwaitingAdmission);
        Assert.Equal(2, (int)TableChangeSignal.TabAdmitted);
        Assert.Equal(3, (int)TableChangeSignal.TabAdmissionRejected);

        // The guest record (2026-10-20): a block's cancellation, and any booking event, on the change stream.
        Assert.Equal(4, (int)TableChangeSignal.BookingCancelledByBlock);
        Assert.Equal(5, (int)TableChangeSignal.BookingChanged);

        // Pre-orders (2026-10-21): any pre-order change, and the guest's "on my way".
        Assert.Equal(6, (int)TableChangeSignal.PreOrderChanged);

        // Activity history (2026-10-24): a table handed to another waiter.
        Assert.Equal(7, (int)TableChangeSignal.TableTransferred);

        // Takeaway (2026-10-26): any takeaway order's move, on a row with no table.
        Assert.Equal(8, (int)TableChangeSignal.TakeawayChanged);

        // A written-off tab (2026-10-27): its calls and orders left the floor.
        Assert.Equal(9, (int)TableChangeSignal.TabWrittenOff);
    }

    /// <summary>The activity log's two stored enums (2026-10-24). On the wire they are words; on disk, these numbers.</summary>
    [Fact]
    public void Activity_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)ActivityCategory.Booking);
        Assert.Equal(2, (int)ActivityCategory.Table);
        Assert.Equal(3, (int)ActivityCategory.Order);
        Assert.Equal(4, (int)ActivityCategory.Payment);
        Assert.Equal(5, (int)ActivityCategory.Takeaway);
        Assert.Equal(6, (int)ActivityCategory.Message);
        Assert.Equal(7, (int)ActivityCategory.Settings);
        Assert.Equal(8, (int)ActivityCategory.Staff);
        Assert.Equal(9, (int)ActivityCategory.Device);

        Assert.Equal(1, (int)ActivityActorKind.Staff);
        Assert.Equal(2, (int)ActivityActorKind.Diner);
        Assert.Equal(3, (int)ActivityActorKind.System);
        Assert.Equal(4, (int)ActivityActorKind.PlatformAdmin);
    }

    /// <summary>
    /// The pre-order's wire enums. <c>PreOrderState</c> is derived rather than stored, and <c>PreOrderItemRefusal</c>
    /// only ever answered - but a client keys its copy on both numbers, so they are pinned like a stored one.
    /// <c>PreOrders.KitchenStatus</c> is a <c>TabOrderStatus</c>, pinned above.
    /// </summary>
    [Fact]
    public void Pre_order_values_are_pinned_to_what_clients_read()
    {
        Assert.Equal(1, (int)Yalla.Domain.Occupancy.PreOrderState.Editable);
        Assert.Equal(2, (int)Yalla.Domain.Occupancy.PreOrderState.Locked);
        Assert.Equal(3, (int)Yalla.Domain.Occupancy.PreOrderState.Sent);
        Assert.Equal(4, (int)Yalla.Domain.Occupancy.PreOrderState.NotComing);
        Assert.Equal(5, (int)Yalla.Domain.Occupancy.PreOrderState.Cancelled);

        Assert.Equal(1, (int)Yalla.Domain.Occupancy.PreOrderItemRefusal.NotOnMenu);
        Assert.Equal(2, (int)Yalla.Domain.Occupancy.PreOrderItemRefusal.SoldOut);
        Assert.Equal(3, (int)Yalla.Domain.Occupancy.PreOrderItemRefusal.NotForPreOrder);
    }

    /// <summary>Guest messages: who wrote, what kind, and the guest's answer - stored as numbers on every message.</summary>
    [Fact]
    public void Guest_message_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)Yalla.Domain.Messages.MessageAuthor.Guest);
        Assert.Equal(2, (int)Yalla.Domain.Messages.MessageAuthor.Venue);
        Assert.Equal(3, (int)Yalla.Domain.Messages.MessageAuthor.System);

        Assert.Equal(1, (int)Yalla.Domain.Messages.MessageKind.Text);
        Assert.Equal(2, (int)Yalla.Domain.Messages.MessageKind.AskComing);
        Assert.Equal(3, (int)Yalla.Domain.Messages.MessageKind.Answer);
        Assert.Equal(4, (int)Yalla.Domain.Messages.MessageKind.Status);

        Assert.Equal(1, (int)Yalla.Domain.Messages.GuestAnswer.Coming);
        Assert.Equal(2, (int)Yalla.Domain.Messages.GuestAnswer.Late);
        Assert.Equal(3, (int)Yalla.Domain.Messages.GuestAnswer.NotComing);
    }

    /// <summary>Staff devices: what kind each row is, and the push kinds a device asked for, stored as bits.</summary>
    [Fact]
    public void Staff_device_values_are_pinned_to_what_is_stored()
    {
        Assert.Equal(1, (int)StaffDeviceKind.Tablet);
        Assert.Equal(2, (int)StaffDeviceKind.Phone);

        Assert.Equal(1, (int)StaffDevicePlatform.Other);
        Assert.Equal(2, (int)StaffDevicePlatform.IPhone);
        Assert.Equal(3, (int)StaffDevicePlatform.IPad);
        Assert.Equal(4, (int)StaffDevicePlatform.Android);
        Assert.Equal(5, (int)StaffDevicePlatform.Windows);
        Assert.Equal(6, (int)StaffDevicePlatform.Mac);
        Assert.Equal(7, (int)StaffDevicePlatform.Linux);

        Assert.Equal(1, (int)StaffDeviceBrowser.Safari);
        Assert.Equal(2, (int)StaffDeviceBrowser.Chrome);
        Assert.Equal(3, (int)StaffDeviceBrowser.Edge);
        Assert.Equal(4, (int)StaffDeviceBrowser.Firefox);
        Assert.Equal(5, (int)StaffDeviceBrowser.Opera);
        Assert.Equal(6, (int)StaffDeviceBrowser.SamsungInternet);

        Assert.Equal(1, (int)StaffPushKinds.Approvals);
        Assert.Equal(2, (int)StaffPushKinds.GuestMessages);
        Assert.Equal(4, (int)StaffPushKinds.TableCalls);
        Assert.Equal(8, (int)StaffPushKinds.Takeaway);
        Assert.Equal(16, (int)StaffPushKinds.KitchenReady);
        Assert.Equal(32, (int)StaffPushKinds.BookingToCall);
        Assert.Equal(47, (int)StaffPushKinds.Defaults);
        Assert.Equal(63, (int)StaffPushKinds.All);
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
