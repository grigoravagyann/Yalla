using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>Ids of one branch's worth of fixture data with real credentials attached.</summary>
internal sealed record AuthBranch(
    Guid VenueId,
    Guid BranchId,
    Guid WaiterId,
    Guid ManagerId,
    string ManagerEmail,
    string ManagerPassword,
    string WaiterPin,
    IReadOnlyList<Guid> TableIds)
{
    public Guid FirstTableId => TableIds[0];
}

/// <summary>An open tab with a host participant already on it.</summary>
internal sealed record AuthTab(Guid TabId, Guid TableId, Guid SessionId, Guid HostParticipantId, string JoinToken);

/// <summary>A seeded platform admin and the credentials that sign them in.</summary>
internal sealed record PlatformAdminAccount(Guid StaffMemberId, string Email, string Password);

/// <summary>
/// An owner or manager seeded with an admin-panel sign-in, and the venue and branch their stored
/// row names. <see cref="BranchId"/> is null for somebody who works across the whole venue.
/// </summary>
internal sealed record PanelAccount(Guid StaffMemberId, Guid VenueId, Guid? BranchId, string Email, string Password);

/// <summary>
/// Fixture data for the authentication tests: a branch whose staff have real hashed credentials,
/// and tabs that a participant token can be minted against.
/// </summary>
/// <remarks>
/// The credentials are hashed with the same <see cref="SecretHasher"/> the running API verifies
/// against. Writing a placeholder string into <c>PinHash</c> would make every one of these tests
/// pass or fail for the wrong reason.
/// </remarks>
internal static class AuthTestData
{
    public const string ManagerPassword = "correct-horse-battery-staple";
    public const string WaiterPin = "4271";

    public static async Task<AuthBranch> CreateBranchAsync(
        YallaDbContext db,
        int tableCount = 2,
        CancellationToken cancellationToken = default)
    {
        var branch = await TestBranchBuilder.CreateAsync(
            db, tableCount: tableCount, cancellationToken: cancellationToken);

        var hasher = new SecretHasher();
        var email = $"manager-{Guid.NewGuid():N}@example.test";

        var waiter = await db.StaffMembers.FirstAsync(s => s.Id == branch.WaiterId, cancellationToken);
        var manager = await db.StaffMembers.FirstAsync(s => s.Id == branch.ManagerId, cancellationToken);

        waiter.SetPinHash(hasher.Hash(WaiterPin));

        // The manager gets both credentials, which is the realistic case and the reason they live
        // on one row: the same person taps a PIN on the floor and signs in to the panel at home,
        // and the audit log has to name them once either way.
        manager.SetPinHash(hasher.Hash("9182"));
        manager.SetPasswordCredentials(email, hasher.Hash(ManagerPassword));

        await db.SaveChangesAsync(cancellationToken);

        return new AuthBranch(
            branch.VenueId,
            branch.BranchId,
            branch.WaiterId,
            branch.ManagerId,
            email,
            ManagerPassword,
            WaiterPin,
            branch.TableIds);
    }

    /// <summary>
    /// An owner who can sign in to the panel. No branch unless one is given: an owner's row
    /// normally names none, which is also why their token carries no branch claim.
    /// </summary>
    public static async Task<PanelAccount> SeedOwnerAsync(
        YallaDbContext db,
        Guid venueId,
        Guid? branchId = null,
        CancellationToken cancellationToken = default)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.test";
        const string password = "owner-password-that-is-long-enough";

        var owner = new StaffMember(venueId, "Founder", Phone(), StaffRole.Owner, "hash", branchId);
        owner.SetPasswordCredentials(email, new SecretHasher().Hash(password));

        db.StaffMembers.Add(owner);
        await db.SaveChangesAsync(cancellationToken);

        return new PanelAccount(owner.Id, venueId, branchId, email, password);
    }

    /// <summary>
    /// A second manager of the venue, at a branch or across all of them. With
    /// <paramref name="canSignIn"/> false they have a PIN and nothing else - the console's own
    /// creation, as shipped - which the sign-in issue tests rely on.
    /// </summary>
    public static async Task<PanelAccount> SeedManagerAsync(
        YallaDbContext db,
        Guid venueId,
        Guid? branchId = null,
        bool canSignIn = true,
        CancellationToken cancellationToken = default)
    {
        var email = $"manager-{Guid.NewGuid():N}@example.test";

        var manager = new StaffMember(venueId, "Second Manager", Phone(), StaffRole.Manager, "hash", branchId);

        if (canSignIn)
        {
            manager.SetPasswordCredentials(email, new SecretHasher().Hash(ManagerPassword));
        }

        db.StaffMembers.Add(manager);
        await db.SaveChangesAsync(cancellationToken);

        return new PanelAccount(manager.Id, venueId, branchId, email, ManagerPassword);
    }

    /// <summary>Another branch of an existing venue, with its own time zone and no staff of its own.</summary>
    public static async Task<Guid> AddBranchAsync(
        YallaDbContext db,
        Guid venueId,
        string timeZoneId,
        string? name = null,
        bool isActive = true,
        int tableCount = 0,
        CancellationToken cancellationToken = default)
    {
        var venue = await db.Venues.FirstAsync(v => v.Id == venueId, cancellationToken);
        var unique = Guid.NewGuid().ToString("N")[..12];

        var branch = new Branch(
            venue,
            name: name ?? $"Second Branch {unique}",
            slug: $"second-branch-{unique}",
            address: "2 Test Street, Yerevan",
            latitude: 40.19,
            longitude: 44.52,
            timeZoneId: timeZoneId,
            floorWidth: 1000,
            floorHeight: 700,
            subscriptionTier: SubscriptionTier.Paid);

        branch.SetActive(isActive);
        db.Branches.Add(branch);

        for (var i = 1; i <= tableCount; i++)
        {
            db.DiningTables.Add(new DiningTable(
                branch.Id, label: i.ToString(), seats: 4, x: 50 * i, y: 100, width: 90, height: 90, shape: TableShape.Round));
        }

        await db.SaveChangesAsync(cancellationToken);

        return branch.Id;
    }

    /// <summary>Signs a seeded owner or manager in through the real endpoint and returns their access token.</summary>
    public static Task<string> SignInAsync(YallaApiFactory factory, PanelAccount account) =>
        SignInAsync(factory, account.Email, account.Password);

    public static async Task<string> SignInAsync(YallaApiFactory factory, string email, string password)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/venue/sign-in", new { email, password });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static string Phone() => $"+374{Random.Shared.Next(10_000_000, 99_999_999)}";

    /// <summary>
    /// The first platform admin, created the way startup creates them: through the seeder, from
    /// configuration. Returns the id and the credentials that sign them in at /api/auth/venue/sign-in.
    /// </summary>
    public static async Task<PlatformAdminAccount> CreatePlatformAdminAsync(
        YallaDbContext db,
        CancellationToken cancellationToken = default)
    {
        var email = $"platform-{Guid.NewGuid():N}@yalla.test";
        const string password = "platform-admin-password-that-is-long";

        var seeder = new PlatformAdminSeeder(
            db,
            new SecretHasher(),
            Microsoft.Extensions.Options.Options.Create(new PlatformAdminOptions { Email = email, Password = password }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformAdminSeeder>.Instance);

        var id = await seeder.SeedAsync(cancellationToken)
                 ?? throw new InvalidOperationException("The seeder did not create a platform admin.");

        return new PlatformAdminAccount(id, email, password);
    }

    /// <summary>
    /// Seats a party at a table and opens a tab on it, with a host participant and a live
    /// invitation token.
    /// </summary>
    /// <remarks>
    /// Built directly rather than through the table state machine, because these tests are about
    /// who may touch the tab, not about how it came to exist.
    /// </remarks>
    public static async Task<AuthTab> CreateOpenTabAsync(
        YallaDbContext db,
        AuthBranch branch,
        Guid tableId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var table = await db.DiningTables.FirstAsync(t => t.Id == tableId, cancellationToken);

        var session = TableSession.SeatWalkIn(
            branch.BranchId, tableId, partySize: 2, seatedAtUtc: nowUtc, seatedByStaffId: branch.WaiterId);

        db.TableSessions.Add(session);
        table.Occupy(session.Id);

        var tab = new Tab(
            branchId: branch.BranchId,
            diningTableId: tableId,
            tableSessionId: session.Id,
            openedAtUtc: nowUtc,
            serviceChargePercentSnapshot: 10m);

        db.Tabs.Add(tab);
        session.AttachTab(tab.Id);

        var host = new TabParticipant(
            tab.Id,
            "Host",
            $"device-{Guid.NewGuid():N}",
            ParticipantRole.Host,
            ParticipantStatus.Approved,
            nowUtc,
            canOrder: true,
            canSeeTableTotal: true,
            canPay: true);

        db.TabParticipants.Add(host);
        tab.SetHostParticipant(host.Id);

        var invitation = new TabJoinToken(tab.Id, host.Id, nowUtc);
        db.TabJoinTokens.Add(invitation);

        await db.SaveChangesAsync(cancellationToken);

        return new AuthTab(tab.Id, tableId, session.Id, host.Id, invitation.Token);
    }
}
