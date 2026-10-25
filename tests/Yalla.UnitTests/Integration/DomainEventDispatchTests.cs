using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Abstractions;
using Yalla.Domain.Common;
using Yalla.Domain.Messaging;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The domain-event mechanism itself (<c>docs/domain-events.md</c>), against the real database: an event raised on a
/// tracked entity is handled once, before the save, in its transaction; a rollback or a failing save takes the handlers'
/// rows with it; a handler may raise another event, but not forever; a save that raised nothing resolves nothing and
/// sends nothing more; a context composed without handlers refuses an event rather than drop it.
/// </summary>
/// <remarks>
/// The rows are outbox messages - the simplest table to write - and the events are this class's own, raised through
/// <c>Entity.Raise</c> by reflection, so nothing here depends on a vertical's events or handlers.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DomainEventDispatchTests(SqlServerFixture fixture)
{
    private static readonly MethodInfo RaiseMethod =
        typeof(Entity).GetMethod("Raise", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly TestClock _clock = new(TestNow.UtcNow);

    [SkippableFact]
    public async Task An_event_is_handled_once_before_the_save_and_what_the_handler_adds_commits_with_the_change()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, WritesARow>());

        var subject = Subject(tag);
        unit.Db.OutboxMessages.Add(subject);
        Raise(subject, new Pinged(tag, _clock.UtcNow));

        await unit.Db.SaveChangesAsync();
        await unit.Db.SaveChangesAsync();

        Assert.Equal([$"handled:{tag}"], unit.Probe.Calls);
        Assert.True(unit.Probe.SawUnsavedSubject, "The handler ran after the save: it should see the change still pending.");
        Assert.Equal([$"test:{tag}", $"test:{tag}:handled"], await KeysAsync(tag));
    }

    [SkippableFact]
    public async Task The_synchronous_save_dispatches_too()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, WritesARow>());

        var subject = Subject(tag);
        unit.Db.OutboxMessages.Add(subject);
        Raise(subject, new Pinged(tag, _clock.UtcNow));

        unit.Db.SaveChanges();

        Assert.Equal([$"test:{tag}", $"test:{tag}:handled"], await KeysAsync(tag));
    }

    [SkippableFact]
    public async Task A_save_that_fails_writes_neither_the_change_nor_what_its_handlers_added()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();

        // Something already holds the subject's key, so the save's insert of the subject violates the unique index.
        await using (var seed = fixture.CreateContext(_clock))
        {
            seed.OutboxMessages.Add(Subject(tag));
            await seed.SaveChangesAsync();
        }

        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, WritesARow>());

        var subject = Subject(tag);
        unit.Db.OutboxMessages.Add(subject);
        Raise(subject, new Pinged(tag, _clock.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => unit.Db.SaveChangesAsync());

        Assert.Equal([$"handled:{tag}"], unit.Probe.Calls);
        Assert.Equal([$"test:{tag}"], await KeysAsync(tag));
    }

    [SkippableFact]
    public async Task A_rollback_discards_what_the_handlers_added_with_the_change()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, WritesARow>());

        await using (var transaction = await unit.Db.Database.BeginTransactionAsync())
        {
            var subject = Subject(tag);
            unit.Db.OutboxMessages.Add(subject);
            Raise(subject, new Pinged(tag, _clock.UtcNow));

            await unit.Db.SaveChangesAsync();

            Assert.True(unit.Probe.SawTransaction, "The handler ran outside the caller's transaction.");
            await transaction.RollbackAsync();
        }

        Assert.Equal([$"handled:{tag}"], unit.Probe.Calls);
        Assert.Empty(await KeysAsync(tag));
    }

    [SkippableFact]
    public async Task A_handler_may_raise_another_event_which_is_handled_in_the_same_save()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        await using var unit = Compose(s =>
        {
            s.AddScoped<IDomainEventHandler<Pinged>, RaisesAnEcho>();
            s.AddScoped<IDomainEventHandler<Echoed>, WritesARow>();
        });

        var subject = Subject(tag);
        unit.Db.OutboxMessages.Add(subject);
        Raise(subject, new Pinged(tag, _clock.UtcNow));

        await unit.Db.SaveChangesAsync();

        Assert.Equal([$"echo:{tag}", $"handled-echo:{tag}"], unit.Probe.Calls);
        Assert.Equal([$"test:{tag}", $"test:{tag}:echo"], await KeysAsync(tag));
    }

    [SkippableFact]
    public async Task A_handler_that_keeps_raising_fails_the_save_instead_of_spinning()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, RaisesItselfAgain>());

        var subject = Subject(tag);
        unit.Db.OutboxMessages.Add(subject);
        Raise(subject, new Pinged(tag, _clock.UtcNow));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Db.SaveChangesAsync());

        Assert.Contains($"{DomainEventDispatcher.MaxRounds} rounds", refused.Message, StringComparison.Ordinal);
        Assert.Equal(DomainEventDispatcher.MaxRounds, unit.Probe.Calls.Count);
        Assert.Empty(await KeysAsync(tag));
    }

    [SkippableFact]
    public async Task A_failing_handler_fails_the_save_and_nothing_is_written()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        await using var unit = Compose(s =>
        {
            s.AddScoped<IDomainEventHandler<Pinged>, WritesARow>();
            s.AddScoped<IDomainEventHandler<Pinged>, Throws>();
        });

        var subject = Subject(tag);
        unit.Db.OutboxMessages.Add(subject);
        Raise(subject, new Pinged(tag, _clock.UtcNow));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Db.SaveChangesAsync());

        Assert.Equal(Throws.Message, thrown.Message);
        Assert.Empty(await KeysAsync(tag));
    }

    [SkippableFact]
    public async Task Events_raised_on_different_entities_are_handled_in_the_order_they_were_raised()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, WritesARow>());

        var first = Subject(tag);
        var second = Subject($"{tag}-2");
        unit.Db.OutboxMessages.AddRange(first, second);

        Raise(second, new Pinged($"{tag}-2", _clock.UtcNow));
        Raise(first, new Pinged(tag, _clock.UtcNow));
        Raise(second, new Pinged($"{tag}-3", _clock.UtcNow));

        await unit.Db.SaveChangesAsync();

        Assert.Equal([$"handled:{tag}-2", $"handled:{tag}", $"handled:{tag}-3"], unit.Probe.Calls);
    }

    [SkippableFact]
    public async Task A_save_that_raised_nothing_resolves_no_handler_and_sends_nothing_more()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        var withHandlers = new CommandCounter();
        var resolved = 0;
        await using var unit = Compose(
            s => s.AddScoped<IDomainEventHandler<Pinged>>(_ =>
            {
                resolved++;
                return new WritesARow(null!, null!, null!);
            }),
            withHandlers);

        unit.Db.OutboxMessages.Add(Subject(tag));
        await unit.Db.SaveChangesAsync();

        var bare = new CommandCounter();
        await using (var db = fixture.CreateContext(_clock, bare))
        {
            db.OutboxMessages.Add(Subject($"{tag}-bare"));
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, resolved);
        Assert.Null(unit.Db.Database.CurrentTransaction);
        Assert.Equal(bare.Count, withHandlers.Count);
    }

    [SkippableFact]
    public async Task A_context_composed_without_handlers_refuses_an_event_instead_of_dropping_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();

        await using (var db = fixture.CreateContext(_clock))
        {
            var subject = Subject(tag);
            db.OutboxMessages.Add(subject);
            Raise(subject, new Pinged(tag, _clock.UtcNow));

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

            Assert.Contains(nameof(Pinged), refused.Message, StringComparison.Ordinal);
        }

        Assert.Empty(await KeysAsync(tag));
    }

    [SkippableFact]
    public async Task A_handler_that_needs_a_transaction_gets_one_the_save_commits_or_joins_the_callers()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();

        // On its own: the save commits the transaction the handler opened.
        await using (var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, NeedsATransaction>()))
        {
            var subject = Subject(tag);
            unit.Db.OutboxMessages.Add(subject);
            Raise(subject, new Pinged(tag, _clock.UtcNow));

            await unit.Db.SaveChangesAsync();

            Assert.True(unit.Probe.SawTransaction);
            Assert.Null(unit.Db.Database.CurrentTransaction);
        }

        Assert.Equal([$"test:{tag}", $"test:{tag}:handled"], await KeysAsync(tag));

        // Inside the caller's: joined, not committed - the caller's rollback takes it all.
        var joined = $"{tag}-joined";
        await using (var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, NeedsATransaction>()))
        {
            await using var transaction = await unit.Db.Database.BeginTransactionAsync();

            var subject = Subject(joined);
            unit.Db.OutboxMessages.Add(subject);
            Raise(subject, new Pinged(joined, _clock.UtcNow));

            await unit.Db.SaveChangesAsync();

            Assert.Same(transaction, unit.Db.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }

        Assert.Empty(await KeysAsync(joined));
    }

    /// <summary>
    /// The owned transaction across a failed save: still open for the retry on the same context, which writes what the
    /// handler added without running it again and commits; and when the caller ended the transaction itself in between,
    /// the retry does not try to commit what is no longer there.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_retried_after_a_failure_keeps_the_handlers_rows_and_commits_the_owned_transaction_once(
        bool callerRolledBackInBetween)
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();

        await using (var seed = fixture.CreateContext(_clock))
        {
            seed.OutboxMessages.Add(Subject(tag));
            await seed.SaveChangesAsync();
        }

        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, NeedsATransaction>());

        var subject = Subject(tag);
        unit.Db.OutboxMessages.Add(subject);
        Raise(subject, new Pinged(tag, _clock.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => unit.Db.SaveChangesAsync());
        Assert.NotNull(unit.Db.Database.CurrentTransaction);

        if (callerRolledBackInBetween)
        {
            await unit.Db.Database.RollbackTransactionAsync();
        }

        // The retry: the clashing row given up, the rest saved again.
        unit.Db.Entry(subject).State = EntityState.Detached;
        await unit.Db.SaveChangesAsync();

        Assert.Equal([$"needs-a-transaction:{tag}"], unit.Probe.Calls);
        Assert.Null(unit.Db.Database.CurrentTransaction);
        Assert.Equal([$"test:{tag}", $"test:{tag}:handled"], await KeysAsync(tag));
    }

    /// <summary>
    /// An event raised on an entity the context only finds by detecting changes - a line added to a tracked order after
    /// the order was added - is not dispatched (the walk does not detect changes), so the save refuses rather than write
    /// the change without its side effects and leave the event for some later save.
    /// </summary>
    [SkippableFact]
    public async Task An_event_raised_where_dispatch_cannot_see_it_fails_the_save_instead_of_being_left_behind()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, WritesARow>());

        var order = Yalla.Domain.Tabs.TabOrder.PlacedByDiner(Guid.CreateVersion7(), Guid.CreateVersion7(), _clock.UtcNow);
        unit.Db.TabOrders.Add(order);
        var line = order.AddLine(Guid.CreateVersion7(), "Flat white", 1_200L, 1);
        Raise(line, new Pinged(Tag(), _clock.UtcNow));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Db.SaveChangesAsync());

        Assert.Contains(nameof(Yalla.Domain.Tabs.TabOrderLine), refused.Message, StringComparison.Ordinal);
        Assert.Empty(unit.Probe.Calls);
        Assert.True(line.HasPendingEvents(), "The refused save should leave the event where it was, not drop it.");
    }

    /// <summary>
    /// A handler appending to a tab's stream on a save that does not number it (anything but the ledger's) would write
    /// the event at sequence 0, where no phone catching up reads it: the save refuses instead.
    /// </summary>
    [SkippableFact]
    public async Task A_tab_event_saved_without_its_place_on_the_stream_fails_the_save()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var tag = Tag();
        await using var unit = Compose(s => s.AddScoped<IDomainEventHandler<Pinged>, AppendsToAStreamUnnumbered>());

        var subject = Subject(tag);
        unit.Db.OutboxMessages.Add(subject);
        Raise(subject, new Pinged(tag, _clock.UtcNow));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Db.SaveChangesAsync());

        Assert.Contains("TabLedger.SaveAppendedAsync", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await KeysAsync(tag));
    }

    /// <summary>
    /// The host composes the context with its scope's dispatcher - the one the ledger and the handlers share - and never
    /// with <see cref="DomainEventDispatcher.None"/>. Nothing else would notice: every save that raises today goes
    /// through the ledger, which dispatches first.
    /// </summary>
    [SkippableFact]
    public async Task The_hosts_context_dispatches_with_its_own_scopes_dispatcher()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<YallaDbContext>();
        var dispatcher = typeof(YallaDbContext)
            .GetField("_domainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(db);

        Assert.NotSame(DomainEventDispatcher.None, dispatcher);
        Assert.Same(scope.ServiceProvider.GetRequiredService<DomainEventDispatcher>(), dispatcher);
    }

    // ------------------------------------------------------------------ composition

    private static string Tag() => $"de-{Guid.NewGuid():N}";

    private OutboxMessage Subject(string tag) => new("test.subject", "{}", _clock.UtcNow, $"test:{tag}", _clock.UtcNow);

    private static void Raise(Entity entity, IDomainEvent domainEvent) => RaiseMethod.Invoke(entity, [domainEvent]);

    private async Task<List<string>> KeysAsync(string tag)
    {
        await using var db = fixture.CreateContext(_clock);

        return await db.OutboxMessages.AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith($"test:{tag}"))
            .OrderBy(m => m.IdempotencyKey)
            .Select(m => m.IdempotencyKey)
            .ToListAsync();
    }

    /// <summary>A unit of work as a request's scope composes it: the context dispatching to these handlers.</summary>
    private Unit Compose(Action<IServiceCollection> handlers, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<YallaDbContext>()
            .UseSqlServer(fixture.ConnectionString)
            .AddInterceptors(interceptors)
            .Options;

        var services = new ServiceCollection();
        services.AddSingleton<IClock>(_clock);
        services.AddSingleton<Probe>();
        services.AddScoped<DomainEventDispatcher>();
        services.AddScoped(sp => new YallaDbContext(options, _clock, sp.GetRequiredService<DomainEventDispatcher>()));
        handlers(services);

        var root = services.BuildServiceProvider(validateScopes: true);
        var scope = root.CreateAsyncScope();

        return new Unit(root, scope);
    }

    private sealed class Unit(ServiceProvider root, AsyncServiceScope scope) : IAsyncDisposable
    {
        public YallaDbContext Db => scope.ServiceProvider.GetRequiredService<YallaDbContext>();

        public Probe Probe => scope.ServiceProvider.GetRequiredService<Probe>();

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await root.DisposeAsync();
        }
    }

    /// <summary>What the handlers saw.</summary>
    private sealed class Probe
    {
        public List<string> Calls { get; } = [];

        public bool SawTransaction { get; set; }

        public bool SawUnsavedSubject { get; set; }
    }

    private sealed record Pinged(string Tag, DateTime OccurredAtUtc) : IDomainEvent;

    private sealed record Echoed(string Tag, DateTime OccurredAtUtc) : IDomainEvent;

    /// <summary>Adds a row in the unit of work, and notes what it saw.</summary>
    private sealed class WritesARow(YallaDbContext db, IClock clock, Probe probe) :
        IDomainEventHandler<Pinged>, IDomainEventHandler<Echoed>
    {
        public Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken)
        {
            probe.Calls.Add($"handled:{domainEvent.Tag}");
            probe.SawTransaction = db.Database.CurrentTransaction is not null;
            probe.SawUnsavedSubject = db.ChangeTracker.Entries<OutboxMessage>()
                .Any(e => e.State == EntityState.Added && e.Entity.IdempotencyKey == $"test:{domainEvent.Tag}");

            db.OutboxMessages.Add(new OutboxMessage("test.handled", "{}", clock.UtcNow, $"test:{domainEvent.Tag}:handled", clock.UtcNow));

            return Task.CompletedTask;
        }

        public Task HandleAsync(Echoed domainEvent, CancellationToken cancellationToken)
        {
            probe.Calls.Add($"handled-echo:{domainEvent.Tag}");
            db.OutboxMessages.Add(new OutboxMessage("test.handled", "{}", clock.UtcNow, $"test:{domainEvent.Tag}:echo", clock.UtcNow));

            return Task.CompletedTask;
        }
    }

    /// <summary>Raises a second event on the subject, for the next round.</summary>
    private sealed class RaisesAnEcho(YallaDbContext db, IClock clock, Probe probe) : IDomainEventHandler<Pinged>
    {
        public Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken)
        {
            probe.Calls.Add($"echo:{domainEvent.Tag}");

            var subject = db.OutboxMessages.Local.Single(m => m.IdempotencyKey == $"test:{domainEvent.Tag}");
            Raise(subject, new Echoed(domainEvent.Tag, clock.UtcNow));

            return Task.CompletedTask;
        }
    }

    /// <summary>Raises what it handles: a loop.</summary>
    private sealed class RaisesItselfAgain(YallaDbContext db, Probe probe) : IDomainEventHandler<Pinged>
    {
        public Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken)
        {
            probe.Calls.Add($"again:{domainEvent.Tag}");

            var subject = db.OutboxMessages.Local.Single(m => m.IdempotencyKey == $"test:{domainEvent.Tag}");
            Raise(subject, domainEvent);

            return Task.CompletedTask;
        }
    }

    private sealed class Throws : IDomainEventHandler<Pinged>
    {
        public const string Message = "The handler could not do its part.";

        public Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Message);
    }

    /// <summary>Asks for a transaction held to the commit, as the order-ready notice does for the account lock.</summary>
    private sealed class NeedsATransaction(YallaDbContext db, IClock clock, Probe probe) : IDomainEventHandler<Pinged>
    {
        public async Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken)
        {
            probe.Calls.Add($"needs-a-transaction:{domainEvent.Tag}");
            await db.EnsureTransactionAsync(cancellationToken);
            probe.SawTransaction = db.Database.CurrentTransaction is not null;

            db.OutboxMessages.Add(new OutboxMessage("test.handled", "{}", clock.UtcNow, $"test:{domainEvent.Tag}:handled", clock.UtcNow));
        }
    }

    /// <summary>Appends to a tab's stream by hand, unnumbered - what a stream handler's append is on a save the ledger is not making.</summary>
    private sealed class AppendsToAStreamUnnumbered(YallaDbContext db, IClock clock) : IDomainEventHandler<Pinged>
    {
        public Task HandleAsync(Pinged domainEvent, CancellationToken cancellationToken)
        {
            db.TabEvents.Add(new Yalla.Domain.Tabs.TabEvent(
                Guid.CreateVersion7(), Yalla.Domain.Enums.TabEventType.OrderStatusChanged, "{}",
                Yalla.Domain.Enums.ActorType.System, null, clock.UtcNow));

            return Task.CompletedTask;
        }
    }

    /// <summary>Counts the commands a context sends.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
