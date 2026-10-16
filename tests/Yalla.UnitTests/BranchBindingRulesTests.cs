using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.UnitTests;

/// <summary>
/// The branch suits the role: a branch manager names their one branch, a general manager names
/// none. Refused on the entity, as a 422 on <c>branchId</c>, whichever way the row is reached.
/// </summary>
public class BranchBindingRulesTests
{
    private static readonly Guid Venue = Guid.CreateVersion7();
    private static readonly Guid Branch = Guid.CreateVersion7();

    [Fact]
    public void A_branch_manager_with_no_branch_is_refused_on_branchId()
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => new StaffMember(Venue, "Nune", "+37400000010", StaffRole.Manager, "hash"));

        Assert.Equal("branchId", refused.Field);
        Assert.Equal("A branch manager works at one branch. Choose it.", refused.Violations[0].Message);
    }

    [Fact]
    public void A_general_manager_with_a_branch_is_refused_on_branchId()
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => new StaffMember(Venue, "Gor", "+37400000011", StaffRole.GeneralManager, "hash", Branch));

        Assert.Equal("branchId", refused.Field);
        Assert.Equal(
            "A general manager runs every branch, so leave the branch empty.", refused.Violations[0].Message);
    }

    [Fact]
    public void Each_role_is_built_with_the_branch_that_suits_it()
    {
        Assert.Equal(Branch, new StaffMember(Venue, "Nune", "+37400000012", StaffRole.Manager, "hash", Branch).BranchId);
        Assert.Null(new StaffMember(Venue, "Gor", "+37400000013", StaffRole.GeneralManager, "hash").BranchId);

        // Owners and floor staff are as they were: either way.
        Assert.Null(new StaffMember(Venue, "Ani", "+37400000014", StaffRole.Owner, "hash").BranchId);
        Assert.Null(new StaffMember(Venue, "Aram", "+37400000015", StaffRole.Waiter, "hash").BranchId);
    }

    /// <summary>
    /// Promotion clears the branch in the same breath. Two separate calls would refuse whichever came
    /// first, because each half on its own is a row that must not exist.
    /// </summary>
    [Fact]
    public void A_branch_manager_is_promoted_to_general_manager_by_changing_the_role_and_the_branch_together()
    {
        var manager = new StaffMember(Venue, "Nune", "+37400000016", StaffRole.Manager, "hash", Branch);

        Assert.Throws<FieldValidationException>(() => manager.SetRole(StaffRole.GeneralManager));
        Assert.Equal(StaffRole.Manager, manager.Role);

        Assert.Throws<FieldValidationException>(() => manager.AssignToBranch(null));
        Assert.Equal(Branch, manager.BranchId);

        manager.Reassign(StaffRole.GeneralManager, null);

        Assert.Equal(StaffRole.GeneralManager, manager.Role);
        Assert.Null(manager.BranchId);
    }

    [Fact]
    public void A_refused_reassignment_changes_nothing()
    {
        var generalManager = new StaffMember(Venue, "Gor", "+37400000017", StaffRole.GeneralManager, "hash");

        var refused = Assert.Throws<FieldValidationException>(() => generalManager.Reassign(StaffRole.Manager, null));

        Assert.Equal("branchId", refused.Field);
        Assert.Equal(StaffRole.GeneralManager, generalManager.Role);
        Assert.Null(generalManager.BranchId);
    }

    [Fact]
    public void Owner_and_general_manager_cover_the_venue_and_a_branch_manager_does_not()
    {
        Assert.True(StaffRoleRules.CoversWholeVenue(StaffRole.Owner));
        Assert.True(StaffRoleRules.CoversWholeVenue(StaffRole.GeneralManager));
        Assert.False(StaffRoleRules.CoversWholeVenue(StaffRole.Manager));
        Assert.False(StaffRoleRules.CoversWholeVenue(StaffRole.Waiter));
        Assert.False(StaffRoleRules.CoversWholeVenue(StaffRole.PlatformAdmin));
    }

    /// <summary>Sign-in issuance is the strict rank, so a general manager cannot take over a peer's account.</summary>
    [Theory]
    [InlineData(StaffRole.Owner, StaffRole.GeneralManager, true)]
    [InlineData(StaffRole.GeneralManager, StaffRole.Manager, true)]
    [InlineData(StaffRole.GeneralManager, StaffRole.GeneralManager, false)]
    [InlineData(StaffRole.GeneralManager, StaffRole.Owner, false)]
    [InlineData(StaffRole.Manager, StaffRole.GeneralManager, false)]
    public void Who_outranks_whom_around_the_general_manager(StaffRole actor, StaffRole other, bool outranks) =>
        Assert.Equal(outranks, StaffRoleRules.Outranks(actor, other));
}
