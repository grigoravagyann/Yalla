using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// General manager and branch manager in the staff service: who may add, change, re-PIN and give a
/// sign-in to whom, and where. Rank from <c>StaffRoleRules</c>, reach from the stored rows.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class GeneralManagerStaffTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task A_general_manager_adds_and_manages_branch_managers_and_floor_staff_at_any_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var sibling = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan");
        var generalManager = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId);

        var asGeneral = fixture.CreateStaffManagementService(db, clock, TestActor.GeneralManager(generalManager.StaffMemberId));

        var siblingManager = await asGeneral.CreateAsync(branch.VenueId, Staff("Sibling Manager", StaffRole.Manager, sibling, "1111"));
        Assert.Equal(StaffRole.Manager, siblingManager.Role);
        Assert.Equal(sibling, siblingManager.BranchId);

        var waiter = await asGeneral.CreateAsync(branch.VenueId, Staff("Home Waiter", StaffRole.Waiter, branch.BranchId, "2222"));
        var cook = await asGeneral.CreateAsync(branch.VenueId, Staff("Sibling Cook", StaffRole.Kitchen, sibling, "3333"));
        Assert.Equal(sibling, cook.BranchId);

        // Editing, re-PINning and moving people below them, at either branch.
        var renamed = await asGeneral.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(FullName: "Renamed by GM"));
        Assert.Equal("Renamed by GM", renamed.FullName);

        await asGeneral.SetPinAsync(branch.VenueId, siblingManager.Id, "4444");

        var moved = await asGeneral.UpdateAsync(branch.VenueId, waiter.Id, new UpdateStaffCommand(SetBranch: true, BranchId: sibling));
        Assert.Equal(sibling, moved.BranchId);

        // A general manager may hold an admin-panel sign-in, and give one to a branch manager.
        var link = await asGeneral.IssueSignInAsync(
            branch.VenueId, siblingManager.Id, new IssueSignInCommand($"sibling-{Guid.NewGuid():N}@example.test"));
        Assert.Equal(siblingManager.Id, link.StaffMemberId);

        // Everyone in the venue, most senior first.
        var listed = await asGeneral.ListAsync(branch.VenueId);
        Assert.Equal(StaffRole.GeneralManager, listed[0].Role);
        Assert.Contains(listed, s => s.Id == cook.Id);
    }

    [SkippableFact]
    public async Task A_general_manager_cannot_add_change_re_pin_or_sign_in_an_owner_or_another_general_manager()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        var generalManager = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId);
        var peer = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId, canSignIn: false);

        var asGeneral = fixture.CreateStaffManagementService(db, clock, TestActor.GeneralManager(generalManager.StaffMemberId));

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asGeneral.CreateAsync(branch.VenueId, Staff("Would-be Owner", StaffRole.Owner, null, "5555")));
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asGeneral.CreateAsync(branch.VenueId, Staff("Would-be GM", StaffRole.GeneralManager, null, "5556")));

        foreach (var above in new[] { owner.StaffMemberId, peer.StaffMemberId })
        {
            await Assert.ThrowsAsync<StaffPermissionException>(
                () => asGeneral.UpdateAsync(branch.VenueId, above, new UpdateStaffCommand(IsActive: false)));
            await Assert.ThrowsAsync<StaffPermissionException>(
                () => asGeneral.UpdateAsync(branch.VenueId, above, new UpdateStaffCommand(FullName: "Hijacked")));
            await Assert.ThrowsAsync<StaffPermissionException>(
                () => asGeneral.SetPinAsync(branch.VenueId, above, "6666"));
            await Assert.ThrowsAsync<StaffPermissionException>(
                () => asGeneral.IssueSignInAsync(branch.VenueId, above, new IssueSignInCommand($"x-{Guid.NewGuid():N}@example.test")));
        }

        // Nor promote somebody to their own rank.
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asGeneral.UpdateAsync(
                branch.VenueId, branch.ManagerId, new UpdateStaffCommand(Role: StaffRole.GeneralManager, SetBranch: true, BranchId: null)));

        await using var verify = fixture.CreateContext(clock);
        Assert.True((await verify.StaffMembers.SingleAsync(s => s.Id == owner.StaffMemberId)).IsActive);
        Assert.Equal("General Manager", (await verify.StaffMembers.SingleAsync(s => s.Id == peer.StaffMemberId)).FullName);
    }

    [SkippableFact]
    public async Task A_branch_manager_manages_floor_staff_at_their_own_branch_and_nowhere_else()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var sibling = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan");

        var siblingWaiter = new StaffMember(branch.VenueId, "Sibling Waiter", "+37455500002", StaffRole.Waiter, "hash", sibling);
        db.StaffMembers.Add(siblingWaiter);
        await db.SaveChangesAsync();

        var asManager = fixture.CreateStaffManagementService(db, clock, TestActor.Manager(branch.ManagerId));

        // Their own branch: fine.
        var waiter = await asManager.CreateAsync(branch.VenueId, Staff("Own Waiter", StaffRole.Waiter, branch.BranchId, "7777"));
        Assert.Equal(branch.BranchId, waiter.BranchId);
        await asManager.SetPinAsync(branch.VenueId, waiter.Id, "7778");

        // Anywhere else, or nowhere in particular - a branchless waiter is on every tablet: refused.
        var elsewhere = await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.CreateAsync(branch.VenueId, Staff("Sibling Hire", StaffRole.Waiter, sibling, "7779")));
        Assert.Equal(StaffRole.GeneralManager, elsewhere.RequiredRole);

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.CreateAsync(branch.VenueId, Staff("Everywhere Hire", StaffRole.Kitchen, null, "7780")));
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.UpdateAsync(branch.VenueId, siblingWaiter.Id, new UpdateStaffCommand(FullName: "Hijacked")));
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.SetPinAsync(branch.VenueId, siblingWaiter.Id, "7781"));
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.UpdateAsync(branch.VenueId, waiter.Id, new UpdateStaffCommand(SetBranch: true, BranchId: sibling)));
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.UpdateAsync(branch.VenueId, waiter.Id, new UpdateStaffCommand(SetBranch: true, BranchId: null)));

        // The list is the same reach.
        var listed = (await asManager.ListAsync(branch.VenueId)).Select(s => s.Id).ToHashSet();
        Assert.Contains(waiter.Id, listed);
        Assert.Contains(branch.WaiterId, listed);
        Assert.DoesNotContain(siblingWaiter.Id, listed);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal("Sibling Waiter", (await verify.StaffMembers.SingleAsync(s => s.Id == siblingWaiter.Id)).FullName);
        Assert.Equal(branch.BranchId, (await verify.StaffMembers.SingleAsync(s => s.Id == waiter.Id)).BranchId);
    }

    [SkippableFact]
    public async Task The_branch_has_to_suit_the_role_on_create_and_on_every_change()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.StaffMemberId));

        var noBranch = await Assert.ThrowsAsync<FieldValidationException>(
            () => asOwner.CreateAsync(branch.VenueId, Staff("Branchless Manager", StaffRole.Manager, null, "1212")));
        Assert.Equal("branchId", noBranch.Field);
        Assert.Equal("A branch manager works at one branch. Choose it.", noBranch.Violations[0].Message);

        var withBranch = await Assert.ThrowsAsync<FieldValidationException>(
            () => asOwner.CreateAsync(branch.VenueId, Staff("Branched GM", StaffRole.GeneralManager, branch.BranchId, "1213")));
        Assert.Equal("branchId", withBranch.Field);
        Assert.Equal("A general manager runs every branch, so leave the branch empty.", withBranch.Violations[0].Message);

        // Promotion without clearing the branch, and clearing a branch manager's branch: both refused.
        await Assert.ThrowsAsync<FieldValidationException>(
            () => asOwner.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(Role: StaffRole.GeneralManager)));
        await Assert.ThrowsAsync<FieldValidationException>(
            () => asOwner.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(SetBranch: true, BranchId: null)));

        // Both in one request: the general manager now covers the venue.
        var promoted = await asOwner.UpdateAsync(
            branch.VenueId, branch.ManagerId, new UpdateStaffCommand(Role: StaffRole.GeneralManager, SetBranch: true, BranchId: null));
        Assert.Equal(StaffRole.GeneralManager, promoted.Role);
        Assert.Null(promoted.BranchId);

        // And back down needs a branch again.
        await Assert.ThrowsAsync<FieldValidationException>(
            () => asOwner.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(Role: StaffRole.Manager)));

        var demoted = await asOwner.UpdateAsync(
            branch.VenueId, branch.ManagerId, new UpdateStaffCommand(Role: StaffRole.Manager, SetBranch: true, BranchId: branch.BranchId));
        Assert.Equal(StaffRole.Manager, demoted.Role);
        Assert.Equal(branch.BranchId, demoted.BranchId);

        // The table holds the same rule for a write that skips the entity.
        await using var raw = fixture.CreateContext(clock);
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => raw.StaffMembers
            .Where(s => s.Id == branch.ManagerId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.BranchId, (Guid?)null)));
    }

    /// <summary>The refusal over HTTP: 422 <c>validation-failed</c>, <c>context.field = "branchId"</c>, and the sentence.</summary>
    [SkippableFact]
    public async Task A_branchless_branch_manager_is_a_422_on_branchId_over_http()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        AuthBranch branch;
        PanelAccount owner;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        }

        using var client = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));
        var route = $"/api/venues/{branch.VenueId}/staff";

        foreach (var (body, sentence) in new (object Body, string Sentence)[]
        {
            (new { fullName = "No Branch", phone = "+37455500003", role = StaffRole.Manager, pin = "1414" },
                "A branch manager works at one branch. Choose it."),
            (new { fullName = "Has Branch", phone = "+37455500004", role = StaffRole.GeneralManager, pin = "1415", branchId = branch.BranchId },
                "A general manager runs every branch, so leave the branch empty."),
        })
        {
            var response = await client.PostAsJsonAsync(route, body);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("validation-failed", problem.GetProperty("code").GetString());
            Assert.Equal("branchId", problem.GetProperty("context").GetProperty("field").GetString());
            Assert.Equal(sentence, problem.GetProperty("detail").GetString());
        }

        // The wire value of the new role is 6, and a general manager made over HTTP comes back as one.
        var created = await client.PostAsJsonAsync(route, new
        {
            fullName = "Venue GM", phone = "+37455500005", role = 6, pin = "1416",
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(6, view.GetProperty("role").GetInt32());

        // Venue-wide: no branch. A null is left off the body, like every null this API answers with.
        Assert.False(view.TryGetProperty("branchId", out var branchId) && branchId.ValueKind != JsonValueKind.Null);
    }

    private static CreateStaffCommand Staff(string name, StaffRole role, Guid? branchId, string pin) => new(
        FullName: name,
        Phone: $"+374{Random.Shared.Next(10_000_000, 99_999_999)}",
        Role: role,
        Pin: pin,
        BranchId: branchId);
}
