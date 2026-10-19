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
/// <see cref="Rule"/> is the definition, built from <see cref="Fields"/> - one test per field, named
/// as the wire names it. <see cref="IsComplete(MenuItem)"/> is the same tree compiled, for the paths
/// that already hold the entity; <see cref="Incomplete"/> is the same tree negated, for the queries
/// that count what is missing in SQL; <see cref="MissingFields(MenuItem)"/> asks the same per-field
/// tests which ones fail, so the console can say "photo, allergens" without a copy of the rule. None
/// of them is written twice.
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
    /// Each field the rule asks for, under the name the item's wire uses, with the test it has to pass:
    /// <c>photo</c>, <c>description</c>, <c>ingredients</c>, <c>allergens</c>, <c>portionSize</c>,
    /// <c>prepMinutes</c> (set, and more than zero). In the order the console lists them.
    /// </summary>
    /// <remarks>
    /// Declared before <see cref="Rule"/>, which is built from it: static fields initialise in order.
    /// </remarks>
    public static readonly IReadOnlyList<(string Field, Expression<Func<MenuItem, bool>> Present)> Fields =
    [
        ("photo", item => item.PhotoId != null),
        ("description", item => item.Description != null),
        ("ingredients", item => item.Ingredients != null),
        ("allergens", item => item.Allergens != null),
        ("portionSize", item => item.PortionSize != null),
        ("prepMinutes", item => item.PrepMinutes != null && item.PrepMinutes > 0),
    ];

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
    public static readonly Expression<Func<MenuItem, bool>> Rule = AllOf(Fields);

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

    private static readonly (string Field, Func<MenuItem, bool> Present)[] CompiledFields =
        [.. Fields.Select(f => (f.Field, f.Present.Compile()))];

    /// <summary>The same rule, over an item already in memory.</summary>
    public static bool IsComplete(MenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return Compiled(item);
    }

    /// <summary>The fields an item still lacks, by their wire names, in <see cref="Fields"/> order. Empty: complete.</summary>
    public static IReadOnlyList<string> MissingFields(MenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return [.. CompiledFields.Where(f => !f.Present(item)).Select(f => f.Field)];
    }

    /// <summary>The per-field tests joined with AND over one parameter - a tree EF translates as it did the written-out rule.</summary>
    private static Expression<Func<MenuItem, bool>> AllOf(
        IReadOnlyList<(string Field, Expression<Func<MenuItem, bool>> Present)> fields)
    {
        var item = Expression.Parameter(typeof(MenuItem), "item");

        var body = fields
            .Select(f => new Rebind(f.Present.Parameters[0], item).Visit(f.Present.Body))
            .Aggregate(Expression.AndAlso);

        return Expression.Lambda<Func<MenuItem, bool>>(body, item);
    }

    /// <summary>Points one lambda's parameter at another, so several bodies share it.</summary>
    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
