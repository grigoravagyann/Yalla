using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reservation policy, opening hours and the floor plan for one branch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where each setting lives.</b> This is the one <see cref="IBranchSettingsService"/> the endpoints see. Each
/// settings area is a class of its own over the same unit of work - <see cref="BranchPublicProfileSettings"/>,
/// <see cref="BranchPolicySettings"/>, <see cref="BranchHoursSettings"/> (opening hours and special days),
/// <see cref="FloorPlanReads"/>, <see cref="FloorPlanReplace"/> (with its pure rules in
/// <see cref="FloorPlanMatching"/>) and <see cref="FloorPlanEdits"/> (one room or table at a time) - beside the parts
/// they share: <see cref="BranchSettingsLookups"/> (the branch row, the branch write lock, revisions),
/// <see cref="FloorPlanTables"/> (a table's history and the editor's projection) and
/// <see cref="BranchSettingsHistory"/> (the settings' History lines). They are composed here rather than registered
/// one by one, so this constructor stays the service's whole list of dependencies.
/// </para>
/// <para>
/// <b>Settings changes never touch bookings.</b> Shortening the booking window or the turn time
/// applies to future bookings; the ones already made are counted and reported so the owner knows,
/// and are otherwise left exactly as they were.
/// </para>
/// <para>
/// <b>The floor plan is replaced whole.</b> Rooms, their photos and every table's pin land in one
/// transaction; a partially applied plan is a room whose pins point at pictures it no longer shows. A table left out of the plan is deleted only if nothing
/// ever happened at it - otherwise it is deactivated, because a reservation, session or tab that
/// pointed at it must still resolve.
/// </para>
/// <para>
/// <b>A QR token never changes on edit.</b> Nothing in the replace path writes it. The one way it
/// changes is <see cref="RegenerateQrTokenAsync"/>, which is audited.
/// </para>
/// <para>
/// <b>Who (K4).</b> Every method checks the caller again from the stored staff row through
/// <see cref="IStaffBranchGuard"/>, underneath the route's <c>BranchScoped</c> policy: a manager
/// with a home branch is confined to it even while their token still says otherwise.
/// </para>
/// <para>
/// <b>One writer of pins.</b> A table's pin is a place on one of its room's photos, and the floor-plan
/// replace is the only call that sets one. It takes an update lock on the branch row first and reads
/// everything it decides on inside it; deleting a room, which takes pins off, takes the same lock.
/// The branch's cover photo has nothing to do with pins and changes freely.
/// </para>
/// </remarks>
internal sealed class BranchSettingsService : IBranchSettingsService
{
    private readonly BranchPublicProfileSettings publicProfile;
    private readonly BranchPolicySettings policy;
    private readonly BranchHoursSettings hours;
    private readonly FloorPlanReads floorPlan;
    private readonly FloorPlanReplace replace;
    private readonly FloorPlanEdits edits;

    public BranchSettingsService(
        YallaDbContext db,
        IClock clock,
        ICurrentActor actor,
        IStaffBranchGuard branchGuard,
        BranchSettingsHistory history,
        ILogger<BranchSettingsService> logger)
    {
        var lookups = new BranchSettingsLookups(db);
        var floorTables = new FloorPlanTables(db, clock);

        publicProfile = new BranchPublicProfileSettings(db, branchGuard, logger, lookups);
        policy = new BranchPolicySettings(db, clock, branchGuard, history, logger, lookups);
        hours = new BranchHoursSettings(db, clock, branchGuard, history, logger, lookups);
        floorPlan = new FloorPlanReads(db, branchGuard, floorTables);
        replace = new FloorPlanReplace(db, actor, branchGuard, logger, lookups, floorTables, floorPlan);
        edits = new FloorPlanEdits(db, clock, actor, branchGuard, logger, lookups, floorTables);
    }

    // ------------------------------------------------------------ the public page

    public Task<PublicProfileView> GetPublicProfileAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        publicProfile.GetPublicProfileAsync(branchId, cancellationToken);

    public Task<PublicProfileView> UpdatePublicProfileAsync(
        Guid branchId,
        PublicProfileCommand command,
        CancellationToken cancellationToken = default) =>
        publicProfile.UpdatePublicProfileAsync(branchId, command, cancellationToken);

    // ------------------------------------------------------------ reservation policy

    public Task<ReservationPolicyView> GetReservationPolicyAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        policy.GetReservationPolicyAsync(branchId, cancellationToken);

    public Task<ReservationPolicyDefaultsView> GetReservationPolicyDefaultsAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        policy.GetReservationPolicyDefaultsAsync(branchId, cancellationToken);

    public Task<ReservationPolicyChangeResult> UpdateReservationPolicyAsync(
        Guid branchId,
        ReservationPolicyCommand command,
        CancellationToken cancellationToken = default) =>
        policy.UpdateReservationPolicyAsync(branchId, command, cancellationToken);

    // ------------------------------------------------------------ opening hours

    public Task<IReadOnlyList<OpeningHoursView>> GetOpeningHoursAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        hours.GetOpeningHoursAsync(branchId, cancellationToken);

    public Task<IReadOnlyList<OpeningHoursView>> ReplaceOpeningHoursAsync(
        Guid branchId,
        IReadOnlyList<OpeningHoursBlock> blocks,
        CancellationToken cancellationToken = default) =>
        hours.ReplaceOpeningHoursAsync(branchId, blocks, cancellationToken);

    // ------------------------------------------------------------ special opening days

    public Task<SpecialOpeningDaysView> GetSpecialOpeningDaysAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        hours.GetSpecialOpeningDaysAsync(branchId, cancellationToken);

    public Task<SpecialOpeningDaysChangeResult> ReplaceSpecialOpeningDaysAsync(
        Guid branchId,
        ReplaceSpecialOpeningDaysCommand command,
        CancellationToken cancellationToken = default) =>
        hours.ReplaceSpecialOpeningDaysAsync(branchId, command, cancellationToken);

    // ------------------------------------------------------------ floor plan: rooms, photos, pins

    public Task<FloorPlanView> GetFloorPlanAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        floorPlan.GetFloorPlanAsync(branchId, cancellationToken);

    public Task<FloorPlanReplaceResult> ReplaceFloorPlanAsync(
        Guid branchId,
        ReplaceFloorPlanCommand command,
        CancellationToken cancellationToken = default) =>
        replace.ReplaceFloorPlanAsync(branchId, command, cancellationToken);

    // ------------------------------------------------------------ areas and tables

    public Task<FloorAreaView> CreateFloorAreaAsync(
        Guid branchId,
        FloorAreaCommand command,
        CancellationToken cancellationToken = default) =>
        edits.CreateFloorAreaAsync(branchId, command, cancellationToken);

    public Task<FloorAreaView> UpdateFloorAreaAsync(
        Guid branchId,
        Guid areaId,
        FloorAreaCommand command,
        CancellationToken cancellationToken = default) =>
        edits.UpdateFloorAreaAsync(branchId, areaId, command, cancellationToken);

    public Task DeleteFloorAreaAsync(Guid branchId, Guid areaId, CancellationToken cancellationToken = default) =>
        edits.DeleteFloorAreaAsync(branchId, areaId, cancellationToken);

    public Task<TableDeletionResult> DeleteTableAsync(
        Guid branchId,
        Guid tableId,
        CancellationToken cancellationToken = default) =>
        edits.DeleteTableAsync(branchId, tableId, cancellationToken);

    public Task<FloorTableView> RegenerateQrTokenAsync(Guid tableId, CancellationToken cancellationToken = default) =>
        edits.RegenerateQrTokenAsync(tableId, cancellationToken);
}
