using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;

namespace Yalla.Api.Errors;

/// <summary>
/// One dish in <c>pre-order-items-unavailable</c>'s <c>context.items</c>. Declaration only, for the
/// <see cref="ProblemCatalogue"/>: the mapper writes the same two members.
/// </summary>
/// <param name="MenuItemId">The dish.</param>
/// <param name="Reason">Why it cannot be pre-ordered.</param>
public sealed record PreOrderItemRefusalShape(Guid MenuItemId, PreOrderItemRefusal Reason);

/// <summary>
/// One dish in <c>takeaway-items-unavailable</c>'s <c>context.items</c>. Declaration only, for the
/// <see cref="ProblemCatalogue"/>: the mapper writes the same two members.
/// </summary>
/// <param name="MenuItemId">The dish.</param>
/// <param name="Reason">Why it cannot be ordered to take away.</param>
public sealed record TakeawayItemRefusalShape(Guid MenuItemId, TakeawayItemRefusal Reason);
