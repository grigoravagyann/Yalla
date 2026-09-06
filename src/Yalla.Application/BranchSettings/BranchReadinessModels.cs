namespace Yalla.Application.BranchSettings;

/// <summary>
/// What a branch still needs before it can take diners.
/// </summary>
/// <remarks>
/// <para>
/// The onboarding console already draws this checklist. It was drawing it from a client-side guess
/// - counting tables in the floor-plan response, inferring "the menu is done" from whether items
/// came back - which meant the console and the server had two different ideas of ready, and only
/// one of them decides whether the branch may go Paid. This is the server's answer, and it is the
/// one the going-live gate enforces.
/// </para>
/// <para>
/// Every line is a plain boolean with the count behind it, rather than a list of opaque check
/// objects, so the schema names each requirement and a generated client cannot mistype one. The
/// counts are there because "eleven dishes still need a photo" is a sentence somebody can act on
/// and "menu incomplete" is not.
/// </para>
/// </remarks>
/// <param name="BranchId">The branch this is about.</param>
/// <param name="IsReadyForDiners">
/// Every line below satisfied. This is exactly what going Paid requires on the menu side, plus the
/// rest of the checklist - the tier switch itself only enforces the menu, because a venue may want
/// its tabs enabled before it has finished enrolling tablets.
/// </param>
/// <param name="FloorPlanDrawn">At least one active table exists on the branch's canvas.</param>
/// <param name="TableCount">How many active tables there are.</param>
/// <param name="TablesLabelled">
/// Every active table carries a label. Labels are required by the entity and unique per branch, so
/// this is satisfied whenever there are tables at all - it is reported because the checklist has a
/// line for it and a silently-absent line reads as a failure.
/// </param>
/// <param name="MenuCategoriesPresent">The branch has at least one menu category.</param>
/// <param name="MenuCategoryCount">How many categories there are.</param>
/// <param name="MenuItemCount">How many items there are, complete or not.</param>
/// <param name="IncompleteMenuItemCount">
/// How many items are missing a photo, a description, ingredients, allergens, a portion size or a
/// prep time. <b>The number that blocks going live</b>, and the number the refusal quotes.
/// </param>
/// <param name="IncompleteMenuItemIds">Which ones, so the console can link straight to them.</param>
/// <param name="MenuComplete">No categories missing, and no incomplete items.</param>
/// <param name="OpeningHoursSet">At least one opening block exists for the week.</param>
/// <param name="OpeningHoursDayCount">How many days of the week have any opening block.</param>
/// <param name="ReservationPolicyReviewed">
/// Somebody has saved the reservation policy at least once. A branch ships with defaults, so
/// "has a policy" is always true and answers nothing; this asks whether a human has looked.
/// </param>
/// <param name="StaffEnrolled">At least one active staff member works at this branch.</param>
/// <param name="StaffCount">How many.</param>
/// <param name="DeviceEnrolled">At least one tablet is enrolled and not revoked.</param>
/// <param name="DeviceCount">How many.</param>
/// <param name="Blockers">
/// One sentence per unsatisfied line, in checklist order. The console renders its own labels; this
/// is for the places that need to say what is wrong without reimplementing the list - a log line,
/// a support answer, an email to the venue.
/// </param>
public sealed record BranchReadinessView(
    Guid BranchId,
    bool IsReadyForDiners,
    bool FloorPlanDrawn,
    int TableCount,
    bool TablesLabelled,
    bool MenuCategoriesPresent,
    int MenuCategoryCount,
    int MenuItemCount,
    int IncompleteMenuItemCount,
    IReadOnlyList<Guid> IncompleteMenuItemIds,
    bool MenuComplete,
    bool OpeningHoursSet,
    int OpeningHoursDayCount,
    bool ReservationPolicyReviewed,
    bool StaffEnrolled,
    int StaffCount,
    bool DeviceEnrolled,
    int DeviceCount,
    IReadOnlyList<string> Blockers);

/// <summary>Reads how far along a branch's setup is.</summary>
/// <remarks>
/// Separate from <see cref="IBranchSettingsService"/>, which writes: this is a projection over
/// seven different tables and nothing here changes anything. It is also what
/// <c>IPlatformService</c> consults before letting a branch onto the Paid tier, so both the
/// checklist a manager reads and the gate that refuses them answer from one query.
/// </remarks>
public interface IBranchReadinessQuery
{
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    Task<BranchReadinessView> GetAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many of a branch's menu items are not fit to show a diner, and which.
    /// </summary>
    /// <remarks>
    /// The going-live gate needs this and nothing else on the checklist, so it is separated out
    /// rather than making a tier switch pay for seven queries.
    /// </remarks>
    Task<IReadOnlyList<Guid>> IncompleteMenuItemIdsAsync(Guid branchId, CancellationToken cancellationToken = default);
}
