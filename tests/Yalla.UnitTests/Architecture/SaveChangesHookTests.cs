using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Architecture;

/// <summary>
/// <c>docs/domain-events.md</c> section 5: no change-tracker hook detects anything in <c>YallaDbContext.SaveChanges</c>
/// any more - every side effect is a handler of an event. Pinned as the code stands, so a new hook (a call in the save,
/// a tracker event, an interceptor) is a deliberate change to this test, not something a save picks up quietly.
/// </summary>
public sealed class SaveChangesHookTests
{
    private const string Instead =
        "raise a domain event from the domain method that makes the change and write the side effect from its handler "
        + "(docs/domain-events.md sections 2, 3 and 5; AGENTS.md, \"Raise a domain event and handle it\"). Change this "
        + "test only with a reason the docs record.";

    /// <summary>
    /// What the save itself calls in Yalla's code, in either overload: dispatch, the signal 6 writer the pre-order
    /// events asked for (a writer only: it detects nothing, section 5), the creation stamps with the pending-event
    /// guard, and committing the transaction a handler began.
    /// </summary>
    private static readonly string[] SaveSteps =
    [
        "Yalla.Infrastructure.Persistence.DomainEventDispatcher.DispatchAsync",
        "Yalla.Infrastructure.Persistence.YallaDbContext.SignalPreOrderChangesAsync",
        "Yalla.Infrastructure.Persistence.YallaDbContext.StampCreationTimes",
        "Yalla.Infrastructure.Persistence.YallaDbContext.TakeOwnedTransaction",
    ];

    [Fact]
    public void The_save_calls_only_dispatch_and_its_fixed_steps()
    {
        var saves = typeof(YallaDbContext)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.Name is nameof(DbContext.SaveChanges) or nameof(DbContext.SaveChangesAsync))
            .ToList();

        var overloads = saves
            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            overloads.SequenceEqual(["SaveChanges(Boolean)", "SaveChangesAsync(Boolean, CancellationToken)"]),
            ProductionCode.Violation(
                "YallaDbContext overrides exactly SaveChanges(bool) and SaveChangesAsync(bool, CancellationToken), the two "
                + "overloads every other entry point funnels into - another override would be a second save path.",
                overloads,
                Instead));

        foreach (var save in saves)
        {
            var steps = MethodCalls.BodiesOf(save)
                .SelectMany(MethodCalls.CalledBy)
                .Where(m => ProductionCode.IsYalla(m.Module.Assembly))

                // The compiler's own: the async override's state machine (a class in Debug, a struct in Release) and the
                // thunk through which it calls base.SaveChangesAsync (<>n__0). Not steps.
                .Where(m => !m.Name.StartsWith('<') && m.DeclaringType is { } owner && ProductionCode.IsWritten(owner))
                .Select(ProductionCode.NameOf)
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();

            var added = steps.Except(SaveSteps).Select(s => $"YallaDbContext.{save.Name} now calls {s}");
            var gone = SaveSteps.Except(steps).Select(s => $"YallaDbContext.{save.Name} no longer calls {s}: update SaveSteps");

            var offenders = added.Concat(gone).ToList();

            Assert.True(offenders.Count == 0, ProductionCode.Violation(
                "YallaDbContext.SaveChanges[Async] dispatches the domain events and then runs only its fixed steps: no "
                + "change-tracker hook detects anything in the save any more (docs/domain-events.md section 5).",
                offenders,
                Instead));
        }
    }

    [Fact]
    public void Nothing_subscribes_to_the_tracker_or_intercepts_the_save()
    {
        var subscriptions = typeof(YallaDbContext)
            .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .Prepend(typeof(YallaDbContext))
            .SelectMany(MethodCalls.DeclaredBy)
            .SelectMany(m => MethodCalls.CalledBy(m).Select(called => (m, called)))
            .Where(c => IsTrackerHook(c.called))
            .Select(c => $"{ProductionCode.NameOf(c.m)} calls {ProductionCode.NameOf(c.called)}");

        var interceptors = ProductionCode.AllTypes()
            .Where(t => !t.IsInterface && typeof(IInterceptor).IsAssignableFrom(t))
            .Select(t => $"{ProductionCode.NameOf(t)} is an EF Core interceptor");

        var offenders = subscriptions.Concat(interceptors).Distinct().ToList();

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "No change-tracker hook: YallaDbContext subscribes to no ChangeTracker or DbContext event, never calls "
            + "DetectChanges itself, and no production type is an EF Core interceptor (a SaveChanges interceptor is a "
            + "tracker hook by another name).",
            offenders,
            Instead));
    }

    [Fact]
    public void StaffPushSignals_keeps_only_StageOne_Again_and_Read()
    {
        var signals = typeof(YallaDbContext).Assembly.GetType("Yalla.Infrastructure.Persistence.StaffPushSignals")
                      ?? throw new InvalidOperationException("StaffPushSignals is gone: update this test and docs/domain-events.md section 5.");

        var surface = signals
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsPrivate && !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        string[] kept = ["Again", "Read", "StageOne"];

        Assert.True(surface.SequenceEqual(kept), ProductionCode.Violation(
            "StaffPushSignals.Stage - the tracker hook that detected staff pushes from the change tracker - is gone; what "
            + "is left is StageOne (called from a handler), Again and Read.",
            surface.Except(kept).Select(n => $"StaffPushSignals.{n}").DefaultIfEmpty($"StaffPushSignals exposes {string.Join(", ", surface)}"),
            "stage a staff push from a domain-event handler with StaffPushSignals.StageOne (AGENTS.md, \"Add a staff push "
            + "kind\", step 2; docs/domain-events.md section 5)."));
    }

    private static bool IsTrackerHook(MethodBase called) =>
        called.DeclaringType is { } owner
        && (owner == typeof(ChangeTracker) || typeof(DbContext).IsAssignableFrom(owner))
        && (called.Name.StartsWith("add_", StringComparison.Ordinal) || called.Name == nameof(ChangeTracker.DetectChanges));
}
