using Yalla.Domain.Common;

namespace Yalla.Application.Abstractions;

/// <summary>
/// Produces one side effect of a domain event, inside the save that carries the change: an activity entry, an outbox
/// message, a tab-stream event, a staff push. See <c>docs/domain-events.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Called once per event, before the save, in the save's unit of work.</b> A handler adds or changes tracked rows -
/// directly on the context, or through <c>IOutbox</c>, <c>IActivityLog</c> or the tab ledger, which enlist in it - and
/// never saves. Whatever it writes commits or rolls back with the change that raised the event; a handler that throws
/// fails the save.
/// </para>
/// <para>
/// <b>Who acted is the request's.</b> Inject <c>ICurrentActor</c> (and <c>IClock</c>) as any service does: the handler
/// runs in the same scope as the code that raised the event, so the activity log attributes it to the same person and
/// device.
/// </para>
/// <para>
/// Registered in <c>DependencyInjection.AddDomainEventHandlers</c>, which is also what the test fixture composes, so the
/// handlers a test runs are the handlers production runs. Several handlers of one event run in registration order.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The exact event type handled: dispatch does not look at base types.</typeparam>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
