using System.ComponentModel.DataAnnotations;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.BranchSettings;
using Yalla.Application.Menus;
using Yalla.Application.Staff;
using Yalla.Domain.Common;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Configuring a venue: reservation policy, opening hours, the floor plan, the menu, the staff.
/// </summary>
/// <remarks>
/// <para>
/// <c>ManagerOrAbove</c> within scope, or a platform admin - the scope handlers pass the platform
/// tier for every branch and venue, so no route here needs a role check of its own. Branch routes
/// carry <c>BranchScoped</c>; the staff routes are addressed by venue and carry <c>VenueScoped</c>;
/// the one table route resolves its branch from the table.
/// </para>
/// <para>
/// This is the tooling the team uses to draw the first twenty venues' floor plans during
/// onboarding, so the floor-plan replace is the endpoint that matters most: the editor sends the
/// finished layout, once, and it lands whole or not at all.
/// </para>
/// </remarks>
public static class VenueAdminEndpoints
{
    public static IEndpointRouteBuilder MapVenueAdminEndpoints(this IEndpointRouteBuilder app)
    {
        MapBranchSettings(app);
        MapMenu(app);
        MapStaff(app);

        return app;
    }

    // ------------------------------------------------------------ branch settings

    private static void MapBranchSettings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/public-profile", GetPublicProfileAsync)
            .WithName("getBranchPublicProfile")
            .WithSummary("What this branch publishes on its public page")
            .Produces<PublicProfileView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/public-profile", PutPublicProfileAsync)
            .WithName("putBranchPublicProfile")
            .WithSummary("Set the published phone number and whether the page takes bookings")
            .WithDescription(
                "`acceptsWebBookings` is **false until somebody switches it on**, and stays false "
                + "for every branch that has never been asked. A venue has not agreed to take "
                + "bookings from strangers on the internet by never having been consulted, so this "
                + "is a decision made during onboarding rather than a default inherited - which is "
                + "also why it appears on the branch readiness checklist.\n\n"
                + "While it is off the public page still shows the room, the menu and the hours and "
                + "simply offers no booking.\n\n"
                + "`phoneE164` must be E.164 (`+37411223344`); spaces, dashes and brackets are "
                + "stripped first. Null or blank clears it. A branch with no number published is "
                + "a branch a diner on the public page has no way to ask about a high chair.")
            .Produces<PublicProfileView>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`phoneE164` is not a valid E.164 number.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapGet("/reservation-policy", GetPolicyAsync)
            .WithName("getReservationPolicy")
            .WithSummary("Every field of the branch's reservation policy")
            .Produces<ReservationPolicyView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/reservation-policy", PutPolicyAsync)
            .WithName("putReservationPolicy")
            .WithSummary("Replace the reservation policy")
            .WithDescription(
                "Every field, as one form. Out-of-range values are **refused, never clamped** - a "
                + "turn time of 5 minutes or 12 hours gets a 422 that says so.\n\n"
                + "**Every refusal names its field.** `context.field` is the first offending property "
                + "and `context.fields` is all of them, each with the `bound` it broke, the `min` and "
                + "`max` allowed and the `value` sent - in the same casing this schema uses, so a form "
                + "can put each message against its own input without mapping English prose back to a "
                + "field. A request that breaks six bounds reports six.\n\n"
                + "**Existing bookings are never touched.** If the new window or turn time would not "
                + "have allowed some of them, `affectedExistingReservations` says how many and "
                + "`affectedReservationIds` which; they stay exactly as booked. The new rules apply "
                + "to future bookings only.\n\n"
                + "Saving this form is what marks the policy as reviewed on the branch readiness "
                + "checklist.")
            .Produces<ReservationPolicyChangeResult>()
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "One or more fields are outside their bounds; `context.fields` names every one.");

        group.MapGet("/opening-hours", GetHoursAsync)
            .WithName("getOpeningHours")
            .WithSummary("The weekly opening hours")
            .Produces<IReadOnlyList<OpeningHoursView>>();

        group.MapPut("/opening-hours", PutHoursAsync)
            .WithName("putOpeningHours")
            .WithSummary("Replace the whole week atomically")
            .WithDescription(
                "Send every block for every day. `closesNextDay` is derived - a closing time at or "
                + "before the opening time means after midnight - and is not accepted from the client. "
                + "Blocks on one day may touch but not overlap.\n\n"
                + "The body is an array, so a refusal names the offending block by index: "
                + "`context.field` reads `[2].closesAt`. Every bad block is reported, not just the "
                + "first.")
            .Produces<IReadOnlyList<OpeningHoursView>>()
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "Blocks overlap, or one opens and closes at the same minute; `context.fields` names each.");

        group.MapGet("/readiness", GetReadinessAsync)
            .WithName("getBranchReadiness")
            .WithSummary("What this branch still needs before it can take diners")
            .WithDescription(
                "The onboarding checklist, answered by the server: floor plan drawn, tables labelled, "
                + "at least one menu category, how many menu items are still incomplete, hours set, "
                + "reservation policy reviewed, staff enrolled, at least one tablet.\n\n"
                + "The console used to render this from a client-side guess, which meant the console "
                + "and the server had two different ideas of ready - and only the server's decides "
                + "whether the branch may be switched to `Paid`. `incompleteMenuItemCount` is the line "
                + "that gate enforces, and `incompleteMenuItemIds` says which items to finish.")
            .Produces<BranchReadinessView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapGet("/floor-plan", GetFloorPlanAsync)
            .WithName("getFloorPlan")
            .WithSummary("Canvas size, areas, and every table with its geometry")
            .Produces<FloorPlanView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/floor-plan", PutFloorPlanAsync)
            .WithName("putFloorPlan")
            .WithSummary("Replace the whole plan in one atomic call")
            .WithDescription(
                "The editor sends the finished layout: canvas, areas and tables together. It lands "
                + "whole or not at all.\n\n"
                + "- Tables match by `id`, then by `label`, so an editor that lost the ids still edits "
                + "the same tables and their **QR codes survive**. A QR token is never regenerated by an edit.\n"
                + "- Every table must sit inside the canvas; labels must be unique. Violations are 422 "
                + "naming the tables.\n"
                + "- **Overlapping tables are a warning, not an error.** Real rooms have stools under bars.\n"
                + "- A table omitted from the plan is deleted only if it has never been used. One with any "
                + "reservation, session or tab is **deactivated** instead, and `deactivatedTables` says so.")
            .Produces<FloorPlanReplaceResult>()
            .ProducesProblemDetails(
                StatusCodes.Status422UnprocessableEntity,
                "Tables outside the canvas or repeated labels; `context` names them.");

        group.MapPost("/floor-areas", CreateAreaAsync)
            .WithName("createFloorArea")
            .WithSummary("Add a floor area")
            .Produces<FloorAreaView>(StatusCodes.Status201Created);

        group.MapPatch("/floor-areas/{areaId:guid}", UpdateAreaAsync)
            .WithName("updateFloorArea")
            .WithSummary("Rename or reorder a floor area")
            .Produces<FloorAreaView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such area at this branch.");

        group.MapDelete("/floor-areas/{areaId:guid}", DeleteAreaAsync)
            .WithName("deleteFloorArea")
            .WithSummary("Remove a floor area; its tables stay, with no area")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such area at this branch.");

        group.MapDelete("/tables/{tableId:guid}", DeleteTableAsync)
            .WithName("deleteTable")
            .WithSummary("Delete a never-used table, or deactivate one with history")
            .WithDescription(
                "A table with any reservation, session or tab is **deactivated**, not deleted - "
                + "deleting it would orphan financial and occupancy records. The response says which "
                + "happened. Only a table that has never been used is removed outright.")
            .Produces<TableDeletionResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such table at this branch.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "A party is seated at the table.");

        // Addressed by table, so BranchScoped resolves the branch from the table.
        app.MapPost("/api/tables/{tableId:guid}/regenerate-qr", RegenerateQrAsync)
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped)
            .WithName("regenerateTableQr")
            .WithSummary("Replace a compromised QR code")
            .WithDescription(
                "The one way a table's QR token changes. Editing the table never touches it - the "
                + "printed code must keep working - so this is explicit, and audited to the platform log.")
            .Produces<FloorTableView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such table.");
    }

    private static async Task<IResult> GetPublicProfileAsync(
        Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetPublicProfileAsync(branchId, ct));

    private static async Task<IResult> PutPublicProfileAsync(
        Guid branchId, PublicProfileCommand command, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.UpdatePublicProfileAsync(branchId, command, ct));

    private static async Task<IResult> GetPolicyAsync(Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetReservationPolicyAsync(branchId, ct));

    private static async Task<IResult> PutPolicyAsync(
        Guid branchId, ReservationPolicyCommand command, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateReservationPolicyAsync(branchId, command, ct));

    private static async Task<IResult> GetHoursAsync(Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetOpeningHoursAsync(branchId, ct));

    private static async Task<IResult> GetReadinessAsync(
        Guid branchId, IBranchReadinessQuery readiness, CancellationToken ct) =>
        Results.Ok(await readiness.GetAsync(branchId, ct));

    private static async Task<IResult> PutHoursAsync(
        Guid branchId, IReadOnlyList<OpeningHoursBlock> blocks, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.ReplaceOpeningHoursAsync(branchId, blocks, ct));

    private static async Task<IResult> GetFloorPlanAsync(Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetFloorPlanAsync(branchId, ct));

    private static async Task<IResult> PutFloorPlanAsync(
        Guid branchId, ReplaceFloorPlanCommand command, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.ReplaceFloorPlanAsync(branchId, command, ct));

    private static async Task<IResult> CreateAreaAsync(
        Guid branchId, FloorAreaCommand command, IBranchSettingsService service, CancellationToken ct)
    {
        var area = await service.CreateFloorAreaAsync(branchId, command, ct);

        return Results.Created($"/api/branches/{branchId}/floor-plan", area);
    }

    private static async Task<IResult> UpdateAreaAsync(
        Guid branchId, Guid areaId, FloorAreaCommand command, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateFloorAreaAsync(branchId, areaId, command, ct));

    private static async Task<IResult> DeleteAreaAsync(Guid branchId, Guid areaId, IBranchSettingsService service, CancellationToken ct)
    {
        await service.DeleteFloorAreaAsync(branchId, areaId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteTableAsync(Guid branchId, Guid tableId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.DeleteTableAsync(branchId, tableId, ct));

    private static async Task<IResult> RegenerateQrAsync(Guid tableId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.RegenerateQrTokenAsync(tableId, ct));

    // ------------------------------------------------------------ menu

    private static void MapMenu(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}/menu")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        // Deliberately not "/" - that is the diner-facing GET /api/branches/{id}/menu, and two
        // operations cannot share a path in OpenAPI 3.0. The two reads return the same shape; this
        // one exists because it is reachable for a venue that is suspended, which the diner route
        // refuses on purpose. A manager fixing their menu during a suspension needs to see it.
        group.MapGet("/manage", GetMenuAsync)
            .WithName("getMenuForAdmin")
            .WithSummary("The full menu, including unavailable and unfinished items")
            .WithDescription(
                "The same shape as the public `GET /api/branches/{branchId}/menu`, without its "
                + "open-for-business gate, so a suspended venue's manager can still edit.\n\n"
                + "**This read includes items with `isComplete: false`; the diner-facing one does "
                + "not.** A manager has to be able to see the eleven dishes that still need a photo - "
                + "that is the entire point of being allowed to save them half-entered - and a diner "
                + "must never be shown a dish with no allergen list.")
            .Produces<IReadOnlyList<MenuCategoryView>>();

        group.MapPost("/categories", CreateCategoryAsync)
            .WithName("createMenuCategory")
            .WithSummary("Add a category")
            .Produces<MenuCategoryView>(StatusCodes.Status201Created);

        group.MapPatch("/categories/{categoryId:guid}", UpdateCategoryAsync)
            .WithName("updateMenuCategory")
            .WithSummary("Rename or reorder a category")
            .Produces<MenuCategoryView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such category at this branch.");

        group.MapDelete("/categories/{categoryId:guid}", DeleteCategoryAsync)
            .WithName("deleteMenuCategory")
            .WithSummary("Remove a category and its items")
            .WithDescription("Refused while any of its items appears on an order; mark them unavailable instead.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "Items in this category appear on orders.");

        group.MapPost("/categories/{categoryId:guid}/items", CreateItemAsync)
            .WithName("createMenuItem")
            .WithSummary("Add an item; only a name and a price are required")
            .WithDescription(
                "**A photo and the descriptive fields are optional here and required to go live.** "
                + "Ingredients, allergens, portion size, prep minutes and a photo are what a diner "
                + "would otherwise ask a waiter, so an item without them is not fit to show one - but "
                + "requiring them at this point meant an eighty-dish menu could not be entered without "
                + "eighty photo uploads first, in order, before a single name or price could be typed. "
                + "That is not the order the work happens in.\n\n"
                + "So the rule moved rather than went away. An item saved without them comes back with "
                + "`isComplete: false`; `GET /api/branches/{branchId}/readiness` counts it; the branch "
                + "cannot be switched to `Paid` while any remain; and the diner-facing menu does not "
                + "return it at all.")
            .Produces<MenuItemView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "The name is blank, or the price is negative.");

        group.MapPatch("/items/{itemId:guid}", UpdateItemAsync)
            .WithName("updateMenuItem")
            .WithSummary("Edit an item, including its price and its category")
            .WithDescription(
                "A price change never affects existing order lines, which snapshotted the price they "
                + "were placed at.\n\n"
                + "`categoryId` **moves the item to another category of the same branch**, where it "
                + "lands last in the display order - reorder afterwards if that is not where it "
                + "belongs. Existing order lines are unaffected: they never reference a category. A "
                + "category belonging to another branch is refused with `context.field` of "
                + "`categoryId`.\n\n"
                + "A field left out is left alone, including one that was never filled in, so a "
                + "half-entered item can be saved again without the edit insisting on the half that "
                + "is missing.")
            .Produces<MenuItemView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such item at this branch.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`categoryId` names a category that is not on this branch.");

        group.MapPost("/items/{itemId:guid}/availability", SetAvailabilityAsync)
            .WithName("setMenuItemAvailability")
            .WithSummary("\"We're out of khachapuri tonight\"")
            .WithDescription("Separate from delete. The item stays on the menu record; diners cannot order it while unavailable.")
            .Produces<MenuItemView>();

        group.MapDelete("/items/{itemId:guid}", DeleteItemAsync)
            .WithName("deleteMenuItem")
            .WithSummary("Remove an item, or deactivate one that appears on orders")
            .WithDescription(
                "An item referenced by any order line cannot be deleted - the reference must survive - so it "
                + "is marked unavailable instead, and the response says so.")
            .Produces<MenuItemDeletionResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such item at this branch.");
    }

    private static async Task<IResult> GetMenuAsync(Guid branchId, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.GetMenuAsync(branchId, ct));

    private static async Task<IResult> CreateCategoryAsync(
        Guid branchId, CreateMenuCategoryCommand command, IMenuService service, CancellationToken ct)
    {
        var category = await service.CreateCategoryAsync(branchId, command, ct);

        return Results.Created($"/api/branches/{branchId}/menu", category);
    }

    private static async Task<IResult> UpdateCategoryAsync(
        Guid branchId, Guid categoryId, UpdateMenuCategoryCommand command, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateCategoryAsync(branchId, categoryId, command, ct));

    private static async Task<IResult> DeleteCategoryAsync(Guid branchId, Guid categoryId, IMenuService service, CancellationToken ct)
    {
        await service.DeleteCategoryAsync(branchId, categoryId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> CreateItemAsync(
        Guid branchId, Guid categoryId, CreateMenuItemCommand command, IMenuService service, CancellationToken ct)
    {
        var item = await service.CreateItemAsync(branchId, categoryId, command, ct);

        return Results.Created($"/api/branches/{branchId}/menu", item);
    }

    private static async Task<IResult> UpdateItemAsync(
        Guid branchId, Guid itemId, UpdateMenuItemCommand command, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateItemAsync(branchId, itemId, command, ct));

    /// <summary>Body of the availability toggle.</summary>
    public sealed record SetAvailabilityRequest(bool IsAvailable);

    private static async Task<IResult> SetAvailabilityAsync(
        Guid branchId, Guid itemId, SetAvailabilityRequest request, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.SetItemAvailabilityAsync(branchId, itemId, request.IsAvailable, ct));

    private static async Task<IResult> DeleteItemAsync(Guid branchId, Guid itemId, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.DeleteItemAsync(branchId, itemId, ct));

    // ------------------------------------------------------------ staff

    private static void MapStaff(IEndpointRouteBuilder app)
    {
        // Staff belong to a venue, not a branch - an owner works everywhere - so the routes are
        // addressed by venue and VenueScoped compares the token's venue claim.
        var group = app.MapGroup("/api/venues/{venueId:guid}/staff")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.VenueScoped);

        group.MapGet("/", ListStaffAsync)
            .WithName("listStaff")
            .WithSummary("Everyone who works for the venue")
            .Produces<IReadOnlyList<StaffMemberView>>();

        group.MapPost("/", CreateStaffAsync)
            .WithName("createStaff")
            .WithSummary("Add a staff member, with a PIN")
            .WithDescription(
                "A manager may create waiters and kitchen staff; an owner or platform admin may create "
                + "managers and owners. Nobody creates a role above their own.\n\n"
                + "A manager or owner gets their admin-panel sign-in through `issueStaffSignIn` after "
                + "they exist: it stores the address and returns a link they choose their own password "
                + "by. Sending `email` and `password` here still works, and is the one path where the "
                + "caller types a password for somebody else.")
            .Produces<StaffMemberView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "The role asked for is above what the caller may assign.");

        group.MapPatch("/{staffMemberId:guid}", UpdateStaffAsync)
            .WithName("updateStaff")
            .WithSummary("Edit a staff member")
            .WithDescription("Nobody may change their own role or deactivate their own account. Set `setBranch` to apply `branchId`, including null for venue-wide.")
            .Produces<StaffMemberView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Changing your own role, or editing someone you could not have created.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such staff member in this venue.");

        group.MapPost("/{staffMemberId:guid}/pin", SetPinAsync)
            .WithName("setStaffPin")
            .WithSummary("Set or reset a PIN")
            .WithDescription("4 to 8 digits. Clears any lockout.")
            .Produces<StaffMemberView>();

        // Mints a credential, so it carries the sign-in budget rather than the global one: ten a
        // minute per caller is plenty for a person and not much for a script.
        group.MapPost("/{staffMemberId:guid}/sign-in", IssueSignInAsync)
            .WithName("issueStaffSignIn")
            .WithSummary("Give a manager or owner their admin-panel sign-in")
            .WithDescription(
                "Stores the address and returns a link the person opens to choose their own password. "
                + "Nobody types a password for somebody else: the reset endpoint that consumes the link is "
                + "the only thing that ever sets one.\n\n"
                + "The link is returned **once** and this is the only copy - the server keeps the token's "
                + "hash and never logs it, so a lost link is replaced by issuing another. It is good for "
                + "24 hours and exactly one use. Issuing a new link retires any earlier unused one; two "
                + "links issued at the same instant can both be live until one is used.\n\n"
                + "Only somebody the caller strictly outranks: an owner issues for managers, a platform "
                + "admin for owners and managers, and nobody for a peer or for themselves. A person who "
                + "already has a password keeps it - and their open sessions - until the link is used, "
                + "when both are replaced. Their sign-in address changes to the one given as soon as this "
                + "answers.")
            .Produces<StaffSignInLink>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not allowed to issue a sign-in for this person: yourself, an equal, or someone above you.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such staff member in this venue.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "A waiter or kitchen hand signs in with a PIN; the person is deactivated; or that address already has an account.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`email` is missing or not an address; `context.field` names it.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        // Device enrolment codes and revocation live under /api/branches/{branchId}/devices from
        // the authentication task, with the same policies; nothing is duplicated here.
    }

    private static async Task<IResult> ListStaffAsync(Guid venueId, IStaffManagementService service, CancellationToken ct) =>
        Results.Ok(await service.ListAsync(venueId, ct));

    private static async Task<IResult> CreateStaffAsync(
        Guid venueId, CreateStaffCommand command, IStaffManagementService service, CancellationToken ct)
    {
        var created = await service.CreateAsync(venueId, command, ct);

        return Results.Created($"/api/venues/{venueId}/staff/{created.Id}", created);
    }

    private static async Task<IResult> UpdateStaffAsync(
        Guid venueId, Guid staffMemberId, UpdateStaffCommand command, IStaffManagementService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateAsync(venueId, staffMemberId, command, ct));

    /// <summary>Body of the PIN reset.</summary>
    public sealed record SetPinRequest(string Pin);

    private static async Task<IResult> SetPinAsync(
        Guid venueId, Guid staffMemberId, SetPinRequest request, IStaffManagementService service, CancellationToken ct) =>
        Results.Ok(await service.SetPinAsync(venueId, staffMemberId, request.Pin, ct));

    /// <summary>Body of the sign-in issue.</summary>
    /// <param name="Email">The address this person will sign in with. Stored lowercased.</param>
    public sealed record IssueSignInRequest(
        [Required][EmailAddress][StringLength(FieldLengths.Email)] string Email);

    private static async Task<IResult> IssueSignInAsync(
        Guid venueId, Guid staffMemberId, IssueSignInRequest request, IStaffManagementService service, CancellationToken ct) =>
        Results.Ok(await service.IssueSignInAsync(venueId, staffMemberId, new IssueSignInCommand(request.Email), ct));
}
