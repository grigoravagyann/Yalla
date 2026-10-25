using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Notifications;
using Yalla.Application.Telegram;
using Yalla.Domain.Common;
using Yalla.Infrastructure;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;
using Yalla.UnitTests.Integration;

namespace Yalla.UnitTests.Architecture;

/// <summary>
/// The rules of <c>docs/domain-events.md</c> that the compiled code can be held to: what an event is, where a handler
/// lives and how it is registered, that a handler never saves, and that the tests run the handlers production runs.
/// </summary>
/// <remarks>
/// Pure: the handlers are resolved from the real <c>AddDomainEventHandlers</c> over a context that is built but never
/// opened, so no database is needed and none is touched.
/// </remarks>
public sealed class DomainEventRulesTests
{
    private const string HandlersNamespace = "Yalla.Infrastructure.DomainEvents.";

    /// <summary>
    /// Events raised on purpose with no handler yet. Each entry names why: an event nobody listens to is either a
    /// forgotten side effect or a deliberate seam, and only a reason tells the two apart. Empty today - every event has a
    /// handler.
    /// </summary>
    private static readonly Dictionary<Type, string> EventsWithoutHandlers = new();

    private static readonly Type HandlerOpen = typeof(IDomainEventHandler<>);

    [Fact]
    public void Every_domain_event_is_a_sealed_record_in_Domain_carrying_OccurredAtUtc()
    {
        var offenders = new List<string>();

        foreach (var type in DomainEvents())
        {
            var name = ProductionCode.NameOf(type);

            if (type.Assembly != ProductionCode.Domain)
            {
                offenders.Add($"{name} is declared in {type.Assembly.GetName().Name}, not Yalla.Domain");
            }

            if (!type.IsSealed)
            {
                offenders.Add($"{name} is not sealed");
            }

            if (!IsRecord(type))
            {
                offenders.Add($"{name} is not a record");
            }

            var carriesInstant = type.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.Name == nameof(IDomainEvent.OccurredAtUtc) && p.ParameterType == typeof(DateTime)));

            if (!carriesInstant)
            {
                offenders.Add($"{name} takes no DateTime {nameof(IDomainEvent.OccurredAtUtc)} in its constructor");
            }
        }

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "Every IDomainEvent is a sealed, past-tense record in Yalla.Domain that carries OccurredAtUtc - an IClock instant "
            + "passed in by the domain method that raises it. Dispatch matches the exact type, so a derived event would never "
            + "reach its base type's handlers.",
            offenders,
            "declare it as `public sealed record XHappened(..., DateTime OccurredAtUtc) : IDomainEvent` in "
            + "Domain/<Area>/<Aggregate>Events.cs (model: Domain/Tabs/TabOrderEvents.cs). See docs/domain-events.md section 2 "
            + "(Raising) and AGENTS.md, \"Raise a domain event and handle it\"."));
    }

    [Fact]
    public void Every_handler_is_internal_sealed_and_lives_under_Infrastructure_DomainEvents()
    {
        var offenders = new List<string>();

        foreach (var handler in HandlerTypes())
        {
            var name = ProductionCode.NameOf(handler);

            if (handler.Assembly != ProductionCode.Infrastructure)
            {
                offenders.Add($"{name} is declared in {handler.Assembly.GetName().Name}, not Yalla.Infrastructure");
            }

            if (handler.IsNested || !handler.IsNotPublic)
            {
                offenders.Add($"{name} is not a top-level internal class");
            }

            if (!handler.IsSealed)
            {
                offenders.Add($"{name} is not sealed");
            }

            if (handler.Namespace?.StartsWith(HandlersNamespace, StringComparison.Ordinal) != true)
            {
                offenders.Add($"{name} is in namespace {handler.Namespace}, not {HandlersNamespace}<Area>");
            }
        }

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "Every IDomainEventHandler<T> is an `internal sealed class` in Yalla.Infrastructure.DomainEvents.<Area>: one side "
            + "effect of one vertical, never part of a public surface, found where the vertical's other handlers are.",
            offenders,
            "declare it as `internal sealed class X(...) : IDomainEventHandler<TEvent>` in "
            + "Infrastructure/DomainEvents/<Area>/. See docs/domain-events.md section 3 (Handling)."));
    }

    [Fact]
    public void Every_handler_is_registered_by_AddDomainEventHandlers_for_every_event_it_handles()
    {
        using var unit = new HandlerComposition();
        var offenders = new List<string>();

        foreach (var handler in HandlerTypes())
        {
            foreach (var handled in HandledEvents(handler))
            {
                var resolved = unit.HandlersOf(handled);

                if (!resolved.Any(handler.IsInstanceOfType))
                {
                    offenders.Add(
                        $"{ProductionCode.NameOf(handler)} implements IDomainEventHandler<{ProductionCode.NameOf(handled)}> "
                        + "but AddDomainEventHandlers resolves no instance of it for that event");
                }
            }
        }

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "A handler runs only if AddDomainEventHandlers registers it, for each event it handles - there is no scanning.",
            offenders,
            "register it in DependencyInjection.AddDomainEventHandlers (or the vertical's own list it calls, such as "
            + "PreOrderEventHandlers.Add or AddBookingHandlers), in the order the event's side effects are written. See "
            + "docs/domain-events.md section 3 (Handling) and AGENTS.md, \"Raise a domain event and handle it\", step 4."));
    }

    [Fact]
    public void Every_registered_handler_resolves_and_is_a_production_handler()
    {
        using var unit = new HandlerComposition();
        var offenders = new List<string>();

        foreach (var handled in unit.RegisteredEventTypes())
        {
            try
            {
                foreach (var instance in unit.HandlersOf(handled))
                {
                    if (!HandlerTypes().Contains(instance.GetType()))
                    {
                        offenders.Add($"IDomainEventHandler<{ProductionCode.NameOf(handled)}> resolves "
                                      + $"{ProductionCode.NameOf(instance.GetType())}, which the handler rules do not see");
                    }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                offenders.Add($"IDomainEventHandler<{ProductionCode.NameOf(handled)}> does not resolve: {e.Message}");
            }
        }

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "Everything AddDomainEventHandlers registers resolves in a unit of work holding only what a request scope "
            + "gives a handler (the context, IClock, ICurrentActor, IOutbox, IActivityLog, TabLedger, loggers).",
            offenders,
            "inject only what a service would, and register any helper the handler needs (as ConversationReads is). See "
            + "docs/domain-events.md section 3 (Handling)."));
    }

    [Fact]
    public void Every_domain_event_has_a_registered_handler_or_a_reason_on_the_allow_list()
    {
        using var unit = new HandlerComposition();

        var unhandled = DomainEvents()
            .Where(e => !EventsWithoutHandlers.ContainsKey(e))
            .Where(e => unit.HandlersOf(e).Count == 0)
            .Select(e => $"{ProductionCode.NameOf(e)} is raised but has no registered handler")
            .ToList();

        var stale = EventsWithoutHandlers.Keys
            .Where(e => unit.HandlersOf(e).Count > 0)
            .Select(e => $"{ProductionCode.NameOf(e)} is on EventsWithoutHandlers but has a handler now: take it off")
            .ToList();

        Assert.True(unhandled.Count + stale.Count == 0, ProductionCode.Violation(
            "Every domain event has at least one handler registered in AddDomainEventHandlers, or is on "
            + "DomainEventRulesTests.EventsWithoutHandlers with the reason nothing listens yet.",
            unhandled.Concat(stale),
            "register the handler that writes the event's side effect (docs/domain-events.md section 3), or - when it is "
            + "a deliberate seam - add the event to EventsWithoutHandlers with a comment saying why."));
    }

    [Fact]
    public void No_handler_saves_however_deep_the_call()
    {
        // The handlers, and what they enlist through: a save in the outbox or the activity log would be a save in every
        // handler that uses them, and an interface call is not followed into its implementation.
        var roots = HandlerTypes()
            .Concat([typeof(Outbox), typeof(ActivityLog)])
            .SelectMany(MethodCalls.DeclaredBy);

        var saves = MethodCalls.ReachableFrom(roots)
            .Where(call => IsSave(call.Called))
            .Select(call => MethodCalls.Describe(call.Path))
            .Distinct()
            .ToList();

        Assert.True(saves.Count == 0, ProductionCode.Violation(
            "A domain-event handler adds or changes tracked rows and never saves: it runs inside SaveChanges, and its rows "
            + "commit or roll back with the change that raised the event. Nothing it calls saves either (TabLedger's "
            + "SaveAppendedAsync included).",
            saves,
            "add the row (db.X.Add, IOutbox.Enqueue, IActivityLog.RecordAsync, TabLedger.Append, StaffPushSignals.StageOne) "
            + "and return; the caller's single SaveChangesAsync writes it. A lock held to the commit calls "
            + "db.EnsureTransactionAsync first. See docs/domain-events.md sections 3 (Handling) and 4 (The rules)."));
    }

    [Fact]
    public void No_handler_takes_anything_that_leaves_the_process()
    {
        Type[] outward =
        [
            typeof(HttpClient), typeof(IHttpClientFactory), typeof(INotificationChannel), typeof(ITelegramBotClient),
            typeof(StaffPushSender),
        ];

        var offenders = HandlerTypes()
            .SelectMany(h => h.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .Where(p => outward.Contains(p.ParameterType))
                .Select(p => $"{ProductionCode.NameOf(h)} takes {ProductionCode.NameOf(p.ParameterType)} {p.Name}"))
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "A handler runs before the commit: no HTTP, no Expo, no Web Push, no Telegram. Sent from a handler, a message "
            + "would leave for a change that may still roll back.",
            offenders,
            "enqueue an outbox message (IOutbox.Enqueue) and send it from an IOutboxHandler after the commit. See "
            + "docs/domain-events.md section 3 (Handling) and AGENTS.md, \"Add an outbox message\"."));
    }

    [Fact]
    public void The_test_fixture_composes_handlers_through_AddDomainEventHandlers_and_registers_none_by_hand()
    {
        var register = typeof(DependencyInjection).GetMethod("AddDomainEventHandlers", BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException("DependencyInjection.AddDomainEventHandlers is gone: update this test.");

        var offenders = new List<string>();

        foreach (var composer in new[] { nameof(SqlServerFixture.CreateUnitOfWork), nameof(SqlServerFixture.CreateContextWithHandlers) })
        {
            var method = typeof(SqlServerFixture).GetMethod(composer, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)!;

            if (!MethodCalls.BodiesOf(method).SelectMany(MethodCalls.CalledBy).Contains(register))
            {
                offenders.Add($"SqlServerFixture.{composer} does not call DependencyInjection.AddDomainEventHandlers");
            }
        }

        // A production event's handler registered by hand anywhere in the tests: a list that drifts from production's.
        // DomainEventDispatchTests registers its own handlers of its own events, which is the point of it.
        var byHand = typeof(SqlServerFixture).Assembly.GetTypes()
            .SelectMany(MethodCalls.DeclaredBy)
            .SelectMany(m => MethodCalls.CalledBy(m).Select(called => (m, called)))
            .Where(c => c.called is MethodInfo { IsGenericMethod: true } generic
                        && generic.GetGenericArguments().Any(IsProductionHandlerService))
            .Select(c => $"{ProductionCode.NameOf(c.m)} registers {ProductionCode.NameOf(c.called)}"
                         + $"<{string.Join(", ", c.called.GetGenericArguments().Select(ProductionCode.NameOf))}>")
            .Distinct();

        offenders.AddRange(byHand);

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "A service-level test runs the handlers production runs: the fixture's units of work call the real "
            + "DependencyInjection.AddDomainEventHandlers, and no test registers a production event's handler by hand.",
            offenders,
            "compose through SqlServerFixture.CreateUnitOfWork (or a service factory that uses it) or "
            + "CreateContextWithHandlers. See docs/domain-events.md section 7 (Testing)."));
    }

    /// <summary>Every concrete type that implements <see cref="IDomainEvent"/>, in any production assembly.</summary>
    internal static IReadOnlyList<Type> DomainEvents() =>
        ProductionCode.AllTypes()
            .Where(t => !t.IsInterface && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToList();

    /// <summary>Every type that implements a closed <see cref="IDomainEventHandler{TEvent}"/>, in any production assembly.</summary>
    internal static IReadOnlyList<Type> HandlerTypes() =>
        ProductionCode.AllTypes()
            .Where(t => !t.IsInterface && HandledEvents(t).Any())
            .ToList();

    private static IEnumerable<Type> HandledEvents(Type handler) =>
        handler.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == HandlerOpen)
            .Select(i => i.GetGenericArguments()[0]);

    /// <summary>A compiled record class: the compiler gives it a clone method no hand-written class has.</summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public) is not null;

    private static bool IsSave(MethodBase method) =>
        method.Name is nameof(DbContext.SaveChanges) or nameof(DbContext.SaveChangesAsync)
        && method.DeclaringType is { } owner
        && typeof(DbContext).IsAssignableFrom(owner);

    private static bool IsProductionHandlerService(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition() == HandlerOpen
        && type.GetGenericArguments()[0].Assembly == ProductionCode.Domain;

    /// <summary>
    /// A request scope's worth of what handlers inject, then the real <c>AddDomainEventHandlers</c> - the composition
    /// <c>SqlServerFixture.CreateUnitOfWork</c> builds, over a context that is constructed and never opened.
    /// </summary>
    private sealed class HandlerComposition : IDisposable
    {
        private readonly ServiceProvider _root;
        private readonly IServiceScope _scope;
        private readonly ServiceCollection _services = new();

        public HandlerComposition()
        {
            var clock = new TestClock(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            var options = new DbContextOptionsBuilder<YallaDbContext>()
                .UseSqlServer("Server=architecture-tests.invalid;Database=never-opened;Connect Timeout=1")
                .Options;

            _services.AddSingleton<IClock>(clock);
            _services.AddSingleton<ICurrentActor>(TestActor.Waiter(Guid.CreateVersion7()));
            _services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            _services.AddScoped<DomainEventDispatcher>();
            _services.AddScoped(sp => new YallaDbContext(options, clock, sp.GetRequiredService<DomainEventDispatcher>()));
            _services.AddScoped<Yalla.Application.Messaging.IOutbox>(sp =>
                new Outbox(sp.GetRequiredService<YallaDbContext>(), clock, NullLogger<Outbox>.Instance));
            _services.AddScoped<Yalla.Application.Activity.IActivityLog>(sp =>
                SqlServerFixture.CreateActivityLog(sp.GetRequiredService<YallaDbContext>(), clock, sp.GetRequiredService<ICurrentActor>()));
            _services.AddScoped<TabLedger>();
            _services.AddDomainEventHandlers();

            _root = _services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            _scope = _root.CreateScope();
        }

        /// <summary>The event types something is registered to handle.</summary>
        public IEnumerable<Type> RegisteredEventTypes() =>
            _services
                .Select(d => d.ServiceType)
                .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == HandlerOpen)
                .Select(t => t.GetGenericArguments()[0])
                .Distinct();

        /// <summary>What dispatch would run for <paramref name="eventType"/>, resolved as it resolves them.</summary>
        public IReadOnlyList<object> HandlersOf(Type eventType) =>
            [.. _scope.ServiceProvider.GetServices(HandlerOpen.MakeGenericType(eventType)).OfType<object>()];

        public void Dispose()
        {
            _scope.Dispose();
            _root.Dispose();
        }
    }
}
