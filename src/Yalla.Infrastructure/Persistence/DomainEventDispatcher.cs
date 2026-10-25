using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Abstractions;
using Yalla.Domain.Common;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Runs the handlers of every domain event the context's tracked entities raised, before the save that carries them.
/// See <c>docs/domain-events.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rounds until quiet.</b> A round takes every pending event from the tracker (in the order raised, across all
/// entities) and runs each one's handlers; a handler may change an aggregate that raises another event, which the next
/// round picks up. More than <see cref="MaxRounds"/> rounds is a loop in the handlers, and fails the save rather than
/// spinning.
/// </para>
/// <para>
/// <b>Handlers come from the scope</b> the context belongs to - the request's in production, so they see the same
/// actor, ledger and activity log as the code that raised the event. Resolved per event and only when one is pending:
/// a save that raised nothing resolves nothing.
/// </para>
/// </remarks>
public sealed class DomainEventDispatcher
{
    /// <summary>How many rounds of handler-raised events a save may go through before it is called a loop.</summary>
    public const int MaxRounds = 8;

    private static readonly ConcurrentDictionary<Type, Func<object, IDomainEvent, CancellationToken, Task>> Invokers = new();

    private static readonly MethodInfo InvokeOpen =
        typeof(DomainEventDispatcher).GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly IServiceProvider? _services;

    /// <summary>Resolves handlers from <paramref name="services"/> - the unit of work's scope.</summary>
    public DomainEventDispatcher(IServiceProvider services) => _services = services;

    private DomainEventDispatcher()
    {
    }

    /// <summary>
    /// For a context composed without handlers - the design-time factory, a test seeding rows directly. Raising an
    /// event on one is a composition mistake, and its save says so instead of dropping the side effects.
    /// </summary>
    public static DomainEventDispatcher None { get; } = new();

    /// <summary>Dispatches until no tracked entity has an event pending.</summary>
    /// <exception cref="InvalidOperationException">
    /// Events are pending and this is <see cref="None"/>, or the handlers kept raising past <see cref="MaxRounds"/>.
    /// </exception>
    public async Task DispatchAsync(YallaDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        for (var round = 1; ; round++)
        {
            var pending = db.DequeueDomainEvents();

            if (pending.Count == 0)
            {
                return;
            }

            if (_services is null)
            {
                throw new InvalidOperationException(
                    $"Domain events were raised ({string.Join(", ", pending.Select(e => e.GetType().Name).Distinct())}) on a "
                    + "context composed without handlers. Resolve the context from the service provider, or in a test "
                    + "build it through SqlServerFixture.CreateUnitOfWork or a service the fixture composes.");
            }

            if (round > MaxRounds)
            {
                throw new InvalidOperationException(
                    $"Domain event handlers were still raising events after {MaxRounds} rounds "
                    + $"({string.Join(", ", pending.Select(e => e.GetType().Name).Distinct())}): a handler is re-raising "
                    + "what it handles.");
            }

            foreach (var domainEvent in pending)
            {
                var type = domainEvent.GetType();
                var invoke = Invokers.GetOrAdd(
                    type,
                    t => InvokeOpen.MakeGenericMethod(t).CreateDelegate<Func<object, IDomainEvent, CancellationToken, Task>>());

                foreach (var handler in _services.GetServices(typeof(IDomainEventHandler<>).MakeGenericType(type)))
                {
                    await invoke(handler!, domainEvent, cancellationToken);
                }
            }
        }
    }

    private static Task InvokeAsync<TEvent>(object handler, IDomainEvent domainEvent, CancellationToken cancellationToken)
        where TEvent : IDomainEvent =>
        ((IDomainEventHandler<TEvent>)handler).HandleAsync((TEvent)domainEvent, cancellationToken);
}
