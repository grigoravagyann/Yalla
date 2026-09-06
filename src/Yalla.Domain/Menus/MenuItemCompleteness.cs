using System.Linq.Expressions;

namespace Yalla.Domain.Menus;

/// <summary>
/// The one definition of a menu item being <b>complete</b> - fit to put in front of a diner.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is exactly one of these, on the server, on purpose.</b> The console renders a
/// completeness badge, the branch readiness checklist counts what is missing, the going-live gate
/// refuses a branch whose menu is not finished, and the diner-facing menu drops anything that is
/// not. Four readers, and if any of them carried its own idea of "complete" the four would drift -
/// most likely into a diner being shown a dish with no allergens, which is the outcome the
/// requirement exists to prevent.
/// </para>
/// <para>
/// <see cref="Rule"/> is the definition. <see cref="IsComplete(MenuItem)"/> is the same tree
/// compiled, for the paths that already hold the entity; <see cref="Incomplete"/> is the same tree
/// negated, for the queries that count what is missing in SQL. None of the three is written twice.
/// </para>
/// <para>
/// Blank is not a separate case from null: every one of these fields goes through
/// <c>Guard.OptionalText</c>, which trims and returns null for whitespace, so a stored empty string
/// cannot exist and a null check is the whole test.
/// </para>
/// </remarks>
public static class MenuItemCompleteness
{
    /// <summary>
    /// The definition: a photo, a description, ingredients, allergens, a portion size and a prep
    /// time.
    /// </summary>
    /// <remarks>
    /// These are the questions a diner would otherwise put to a waiter - what is in it, how big is
    /// it, how long will it take, does it have nuts, what does it look like - which is the entire
    /// argument for the feature. <c>SpiceLevel</c> is absent because it always has a value;
    /// <c>Name</c> and <c>PriceAmd</c> are absent because they are required to <i>save</i> an item
    /// at all, and an item without them was never created.
    /// </remarks>
    public static readonly Expression<Func<MenuItem, bool>> Rule = item =>
        item.PhotoId != null
        && item.Description != null
        && item.Ingredients != null
        && item.Allergens != null
        && item.PortionSize != null
        && item.PrepMinutes != null
        && item.PrepMinutes > 0;

    /// <summary>
    /// The negation of <see cref="Rule"/>, for the queries that count what a branch still owes.
    /// </summary>
    /// <remarks>
    /// Derived from the rule's own expression tree rather than written out, so the two cannot
    /// disagree about, say, whether a zero prep time counts.
    /// </remarks>
    public static readonly Expression<Func<MenuItem, bool>> Incomplete =
        Expression.Lambda<Func<MenuItem, bool>>(Expression.Not(Rule.Body), Rule.Parameters);

    private static readonly Func<MenuItem, bool> Compiled = Rule.Compile();

    /// <summary>The same rule, over an item already in memory.</summary>
    public static bool IsComplete(MenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return Compiled(item);
    }
}
