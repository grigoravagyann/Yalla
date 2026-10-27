using Microsoft.AspNetCore.Http;
using Yalla.Api.Errors;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The features the platform allows per branch: the presets, the bits, the checks on a set, the effective set a diner
/// sees, and the refusal's code. Replaced the Free/Paid tier on 2026-10-27.
/// </summary>
public class BranchFeaturesTests
{
    private static Branch NewBranch(IEnumerable<BranchFeature>? features = null) =>
        new(new Venue("Lava", VenueType.Restaurant, "lava"), "Kentron", "kentron", "Abovyan 12", 40.18, 44.51, "Asia/Yerevan",
            allowedFeatures: features);

    [Fact]
    public void Basic_is_everything_but_table_service_and_Full_is_all_ten()
    {
        Assert.Equal(10, BranchFeatures.All.Count);
        Assert.Equal(BranchFeatures.All, BranchFeatures.Full);
        Assert.Equal(BranchFeatures.All.Where(f => f != BranchFeature.TableService), BranchFeatures.Basic);
        Assert.Equal(BranchFeatures.BasicBits, BranchFeatures.BitsOf(BranchFeatures.Basic));
        Assert.Equal(BranchFeatures.FullBits, BranchFeatures.BitsOf(BranchFeatures.Full));
    }

    [Fact]
    public void Each_feature_owns_the_bit_its_number_names_and_the_bits_read_back_in_order()
    {
        Assert.Equal(1, BranchFeatures.Bit(BranchFeature.AppBookings));
        Assert.Equal(16, BranchFeatures.Bit(BranchFeature.TableService));
        Assert.Equal(512, BranchFeatures.Bit(BranchFeature.Reports));

        Assert.Equal(
            [BranchFeature.WebBookings, BranchFeature.TableService],
            BranchFeatures.FromBits(BranchFeatures.BitsOf([BranchFeature.TableService, BranchFeature.WebBookings, BranchFeature.TableService])));

        // A bit nothing owns is ignored rather than invented into a feature.
        Assert.Equal(BranchFeatures.Full, BranchFeatures.FromBits(-1));
        Assert.Empty(BranchFeatures.FromBits(0));
    }

    [Fact]
    public void A_new_branch_starts_Basic_and_can_be_built_with_any_valid_set()
    {
        var basic = NewBranch();
        Assert.Equal(BranchFeatures.Basic, basic.AllowedFeatures);
        Assert.False(basic.Allows(BranchFeature.TableService));
        Assert.True(basic.Allows(BranchFeature.Takeaway));

        Assert.Equal(BranchFeatures.Full, NewBranch(BranchFeatures.Full).AllowedFeatures);
        Assert.Empty(NewBranch([]).AllowedFeatures);
    }

    [Fact]
    public void Pre_orders_need_a_booking_channel_and_an_unknown_value_is_refused_on_features()
    {
        var branch = NewBranch();

        var orphan = Assert.Throws<FieldValidationException>(
            () => branch.SetAllowedFeatures([BranchFeature.PreOrders, BranchFeature.Takeaway], incompleteMenuItemCount: 0));
        Assert.Equal("features", Assert.Single(orphan.Violations).Field);

        var unknown = Assert.Throws<FieldValidationException>(
            () => branch.SetAllowedFeatures([(BranchFeature)11], incompleteMenuItemCount: 0));
        Assert.Equal("features", Assert.Single(unknown.Violations).Field);

        Assert.Throws<FieldValidationException>(() => BranchFeatures.Validate(null));

        // Either channel will do.
        Assert.True(branch.SetAllowedFeatures([BranchFeature.PreOrders, BranchFeature.WebBookings], incompleteMenuItemCount: 0));
        Assert.True(branch.SetAllowedFeatures([BranchFeature.PreOrders, BranchFeature.AppBookings], incompleteMenuItemCount: 0));

        // Nothing moved on a refusal, and the same set again is no change.
        Assert.Equal([BranchFeature.AppBookings, BranchFeature.PreOrders], branch.AllowedFeatures);
        Assert.False(branch.SetAllowedFeatures([BranchFeature.AppBookings, BranchFeature.PreOrders], incompleteMenuItemCount: 0));
    }

    [Fact]
    public void Turning_table_service_on_needs_a_finished_menu_and_nothing_else_does()
    {
        var branch = NewBranch();

        var refused = Assert.Throws<BranchNotReadyForDinersException>(
            () => branch.SetAllowedFeatures(BranchFeatures.Full, incompleteMenuItemCount: 3));
        Assert.Equal(3, refused.IncompleteMenuItemCount);
        Assert.False(branch.Allows(BranchFeature.TableService));

        // Blocking something else with the menu unfinished is fine; so is keeping table service on once it is.
        Assert.True(branch.SetAllowedFeatures([BranchFeature.Reviews], incompleteMenuItemCount: 3));
        Assert.True(branch.SetAllowedFeatures(BranchFeatures.Full, incompleteMenuItemCount: 0));
        Assert.True(branch.SetAllowedFeatures([BranchFeature.TableService], incompleteMenuItemCount: 3));

        Assert.True(branch.WouldTurnOff(BranchFeatures.BasicBits, BranchFeature.TableService));
        Assert.False(branch.WouldTurnOn(BranchFeatures.FullBits, BranchFeature.TableService));
    }

    [Fact]
    public void Blocking_a_feature_keeps_the_owners_switch_and_the_effective_set_is_both()
    {
        var branch = NewBranch(BranchFeatures.Full);
        branch.SetTakeaway(enabled: true, autoAccept: false, prepMinutes: 20, lastOrderMinutes: 30, maxInProgress: 5);
        branch.SetAcceptsWebBookings(true);

        Assert.Contains(BranchFeature.Takeaway, branch.EffectiveFeatures);
        Assert.Contains(BranchFeature.WebBookings, branch.EffectiveFeatures);

        // K9: the app books only once somebody saved the reservation policy.
        Assert.DoesNotContain(BranchFeature.AppBookings, branch.EffectiveFeatures);

        // Pre-orders are off until the owner accepts them, though allowed.
        Assert.DoesNotContain(BranchFeature.PreOrders, branch.EffectiveFeatures);

        branch.SetAllowedFeatures([.. BranchFeatures.Full.Where(f => f != BranchFeature.Takeaway)], incompleteMenuItemCount: 0);

        Assert.DoesNotContain(BranchFeature.Takeaway, branch.EffectiveFeatures);
        Assert.True(branch.TakeawayEnabled);

        branch.SetAllowedFeatures(BranchFeatures.Full, incompleteMenuItemCount: 0);
        Assert.Contains(BranchFeature.Takeaway, branch.EffectiveFeatures);

        // Features with no owner switch are on exactly when allowed.
        Assert.Contains(BranchFeature.Reports, branch.EffectiveFeatures);
        Assert.Contains(BranchFeature.TableService, branch.EffectiveFeatures);
    }

    [Fact]
    public void Either_booking_feature_blocked_takes_both_off_the_effective_set()
    {
        // The app and the page book through one route the server cannot tell apart, so a diner books only with both.
        var switches = new BranchFeatures.OwnerSwitches(
            AcceptsWebBookings: true, ReservationPolicyReviewed: true, AcceptsPreOrders: false, TakeawayEnabled: false, GuestsCanMessage: false);

        Assert.Contains(BranchFeature.AppBookings, BranchFeatures.Effective(BranchFeatures.FullBits, switches));
        Assert.Contains(BranchFeature.WebBookings, BranchFeatures.Effective(BranchFeatures.FullBits, switches));

        foreach (var blocked in new[] { BranchFeature.AppBookings, BranchFeature.WebBookings })
        {
            var effective = BranchFeatures.Effective(BranchFeatures.FullBits & ~BranchFeatures.Bit(blocked), switches);

            Assert.DoesNotContain(BranchFeature.AppBookings, effective);
            Assert.DoesNotContain(BranchFeature.WebBookings, effective);
        }
    }

    [Fact]
    public void Feature_not_enabled_carries_the_feature_and_the_branch()
    {
        var branchId = Guid.CreateVersion7();

        var mapped = ApiExceptionMapper.Map(new FeatureNotEnabledException(BranchFeature.TableService, branchId));

        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal("feature-not-enabled", mapped.Code);
        Assert.Equal(BranchFeature.TableService, mapped.Context!["feature"]);
        Assert.Equal(branchId, mapped.Context["branchId"]);
        Assert.Equal(2, mapped.Context.Count);
    }
}
