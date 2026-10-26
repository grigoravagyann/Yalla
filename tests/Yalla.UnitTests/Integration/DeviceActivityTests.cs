using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Activity;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The History's device entries, written in the same save as the change: a tablet's enrolment code, its enrolment and
/// its revocation; a phone paired by its person's PIN and every way it is unpaired - and a phone named for its person
/// ("Test's phone") wherever its person acts on it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DeviceActivityTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_phone_is_paired_by_its_person_on_it_and_each_unpairing_says_by_whom_and_why()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        // Paired: the waiter, on the phone being paired - no token named them yet, the PIN did.
        var (_, firstSession) = await StaffPhoneTests.PairWaiterAsync(factory, branch);
        var paired = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DevicePhonePaired));

        Assert.Equal(ActivityActorKind.Staff, paired.ActorKind);
        Assert.Equal(branch.WaiterId, paired.StaffMemberId);
        Assert.Equal("Test Waiter", paired.ActorName);
        Assert.Equal(paired.SubjectId, paired.DeviceId);
        Assert.Equal("Test's phone", paired.DeviceLabel);

        // The same as parts, for a client to word in its reader's language rather than read the English apart.
        Assert.Equal(StaffDeviceKind.Phone, paired.DeviceKind);
        Assert.Equal("Test Waiter", paired.DeviceOwnerName);
        Assert.Equal(ActivityCategory.Device, paired.Category);
        Assert.Equal(factory.Clock.UtcNow, paired.OccurredAtUtc, TimeSpan.FromSeconds(1));

        var summary = JsonDocument.Parse(paired.SummaryJson).RootElement;
        Assert.Equal("iPhone · Safari", summary.GetProperty("name").GetString());
        Assert.Equal(branch.WaiterId, summary.GetProperty("staffMemberId").GetGuid());
        Assert.False(summary.GetProperty("repaired").GetBoolean());
        Assert.Equal(branch.ManagerId, summary.GetProperty("codeCreatedByStaffMemberId").GetGuid());

        // Unpaired by its own person, from the phone: labelled as that phone.
        using (var onPhone = factory.CreateClientWithToken(firstSession))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await onPhone.PostAsync("/api/staff/device/unpair", null)).StatusCode);
        }

        var byOwner = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DevicePhoneUnpaired));
        Assert.Equal(paired.SubjectId, byOwner.SubjectId);
        Assert.Equal(branch.WaiterId, byOwner.StaffMemberId);
        Assert.Equal("Test's phone", byOwner.DeviceLabel);
        Assert.Equal("its-owner", Cause(byOwner));

        // Unpaired from the console by the manager.
        await StaffPhoneTests.PairWaiterAsync(factory, branch);
        var second = await LivePhoneAsync(factory, branch);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await manager.PostAsync($"/api/branches/{branch.BranchId}/devices/{second}/revoke", null)).StatusCode);

        var byConsole = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DevicePhoneUnpaired), e => e.SubjectId == second);
        Assert.Equal(branch.ManagerId, byConsole.StaffMemberId);
        Assert.Equal("Console", byConsole.DeviceLabel);
        Assert.Null(byConsole.DeviceKind);
        Assert.Null(byConsole.DeviceOwnerName);
        Assert.Equal("console", Cause(byConsole));
        Assert.Equal("Test Waiter", JsonDocument.Parse(byConsole.SummaryJson).RootElement.GetProperty("staffName").GetString());

        // A revoked phone revoked again is no second entry.
        await manager.PostAsync($"/api/branches/{branch.BranchId}/devices/{second}/revoke", null);
        Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DevicePhoneUnpaired), e => e.SubjectId == second);

        // Unpaired because its person was deactivated - in the same save as the change to the person.
        await StaffPhoneTests.PairWaiterAsync(factory, branch);
        var third = await LivePhoneAsync(factory, branch);

        var deactivated = await manager.PatchAsJsonAsync(
            $"/api/venues/{branch.VenueId}/staff/{branch.WaiterId}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var byDeactivation = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DevicePhoneUnpaired), e => e.SubjectId == third);
        Assert.Equal("deactivated", Cause(byDeactivation));
        Assert.Equal(branch.ManagerId, byDeactivation.StaffMemberId);

        var updated = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.StaffUpdated));
        Assert.Equal(updated.OccurredAtUtc, byDeactivation.OccurredAtUtc);

        // Every pairing was its person's, on its phone.
        Assert.All(
            await EntriesAsync(factory, branch, ActivityActions.DevicePhonePaired),
            e => Assert.Equal(("Test's phone", e.SubjectId), (e.DeviceLabel, e.DeviceId!.Value)));
    }

    [SkippableFact]
    public async Task The_same_browser_paired_to_somebody_else_unpairs_the_earlier_phone_as_replaced()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var phone = factory.CreateClient();
        const string browser = "browser-shared-by-two";

        (await StaffPhoneTests.PairAsync(phone, await StaffPhoneTests.IssueAsync(manager, branch, branch.WaiterId), branch.WaiterPin, browser))
            .EnsureSuccessStatusCode();
        var waiters = await LivePhoneAsync(factory, branch);

        (await StaffPhoneTests.PairAsync(phone, await StaffPhoneTests.IssueAsync(manager, branch, branch.ManagerId), "9182", browser))
            .EnsureSuccessStatusCode();

        var replaced = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DevicePhoneUnpaired));
        Assert.Equal(waiters, replaced.SubjectId);
        Assert.Equal("replaced", Cause(replaced));

        // Recorded as the manager, who proved themselves on the phone that replaced it.
        Assert.Equal(branch.ManagerId, replaced.StaffMemberId);

        var managers = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DevicePhonePaired), e => e.StaffMemberId == branch.ManagerId);
        Assert.Equal(managers.SubjectId, managers.DeviceId);
        Assert.EndsWith("'s phone", managers.DeviceLabel, StringComparison.Ordinal);
        Assert.Equal((StaffDeviceKind.Phone, "Test Manager"), (managers.DeviceKind!.Value, managers.DeviceOwnerName));
    }

    [SkippableFact]
    public async Task A_tablet_code_its_enrolment_and_its_revocation_are_in_the_history()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await SeedBranchAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        await StaffAuthTests.EnrolDeviceAsync(factory, branch);

        var code = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DeviceEnrolmentCodeCreated));
        Assert.Equal(branch.ManagerId, code.StaffMemberId);
        Assert.Equal("Console", code.DeviceLabel);
        Assert.Equal(branch.BranchId, code.SubjectId);

        // Nobody is signed in to a tablet being enrolled: the system's, naming who made the code.
        var enrolled = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DeviceEnrolled));
        Assert.Equal(ActivityActorKind.System, enrolled.ActorKind);
        var summary = JsonDocument.Parse(enrolled.SummaryJson).RootElement;
        Assert.Equal("Test tablet", summary.GetProperty("name").GetString());
        Assert.Equal(branch.ManagerId, summary.GetProperty("codeCreatedByStaffMemberId").GetGuid());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await manager.PostAsync($"/api/branches/{branch.BranchId}/devices/{enrolled.SubjectId}/revoke", null)).StatusCode);

        var revoked = Assert.Single(await EntriesAsync(factory, branch, ActivityActions.DeviceRevoked));
        Assert.Equal(enrolled.SubjectId, revoked.SubjectId);
        Assert.Equal(branch.ManagerId, revoked.StaffMemberId);
        Assert.Empty(await EntriesAsync(factory, branch, ActivityActions.DevicePhoneUnpaired));

        // And a PIN lockout cleared, in the service's own save now.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await manager.PostAsync($"/api/branches/{branch.BranchId}/staff/{branch.WaiterId}/clear-pin-lockout", null)).StatusCode);
        Assert.Single(await EntriesAsync(factory, branch, ActivityActions.StaffPinLockoutCleared), e => e.SubjectId == branch.WaiterId);
    }

    /// <summary>The middle of the test branches' day on today's Yerevan date: nothing here depends on the hour it runs.</summary>
    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString).StartingAtYerevan(new TimeOnly(15, 0));

    private async Task<AuthBranch> SeedBranchAsync(YallaApiFactory factory)
    {
        await using YallaDbContext db = fixture.CreateContext(factory.Clock);

        return await AuthTestData.CreateBranchAsync(db);
    }

    private async Task<List<ActivityEntry>> EntriesAsync(YallaApiFactory factory, AuthBranch branch, string action)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.ActivityEntries
            .AsNoTracking()
            .Where(e => e.BranchId == branch.BranchId && e.Action == action)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync();
    }

    private async Task<Guid> LivePhoneAsync(YallaApiFactory factory, AuthBranch branch)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.StaffDevices
            .Where(d => d.BranchId == branch.BranchId && d.Kind == StaffDeviceKind.Phone && d.RevokedAtUtc == null)
            .Select(d => d.Id)
            .SingleAsync();
    }

    private static string? Cause(ActivityEntry entry) =>
        JsonDocument.Parse(entry.SummaryJson).RootElement.GetProperty("cause").GetString();
}
