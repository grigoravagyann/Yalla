using System.Reflection;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;

namespace Yalla.UnitTests.Architecture;

/// <summary>
/// "Side effects hang on domain events" (<c>AGENTS.md</c>, golden rules): on a migrated vertical a service never writes
/// an activity entry or an outbox message itself - a handler of the change's event does. Held through constructors: a
/// type outside <c>Infrastructure/DomainEvents/</c> that takes <see cref="IOutbox"/> or <see cref="IActivityLog"/> can
/// write one, so each that still does is listed here with its reason.
/// </summary>
public sealed class HandWrittenSideEffectTests
{
    /// <summary>
    /// The types outside the handlers that may still take the outbox or the activity log, and why. Each is a
    /// deliberate exception named in <c>docs/domain-events.md</c> (section 6, and section 8's table) or a read. An entry
    /// that is no longer needed fails the test, so the list only shrinks on purpose.
    /// </summary>
    private static readonly Dictionary<(string Type, Type Takes), string> Allowed = new()
    {
        // Settings, menu, staff: one path and one entry per change (AGENTS.md, "Record an activity entry", step 2;
        // docs/domain-events.md section 8, "Settings, menu, staff, platform, reviews").
        [("Yalla.Infrastructure.Services.BranchSettingsService", typeof(IActivityLog))] =
            "settings.* History lines: one path per setting, an event buys nothing",
        [("Yalla.Infrastructure.Services.MenuService", typeof(IActivityLog))] =
            "menu.* History lines: one path per change",
        [("Yalla.Infrastructure.Services.StaffManagementService", typeof(IActivityLog))] =
            "staff.* History lines: one path per change",
        [("Yalla.Infrastructure.Services.GuestMessageService", typeof(IActivityLog))] =
            "settings.messages, the message settings' one path; the messages' own lines are MessageActivity's",
        [("Yalla.Infrastructure.Services.TakeawayService", typeof(IActivityLog))] =
            "settings.takeaway, the takeaway settings' one path; the orders' own lines are TakeawayOrderActivity's",
        [("Yalla.Infrastructure.Services.PreOrderService", typeof(IActivityLog))] =
            "settings.pre-orders, the pre-order settings' one path (docs/domain-events.md section 6, pre-orders)",
        [("Yalla.Infrastructure.Identity.StaffAuthService", typeof(IActivityLog))] =
            "device.* and staff.pin-lockout-cleared: enrolment, pairing and a PIN unlock, one path each, no vertical",

        // A new booking's first reminders: written after the insert commits, under the account lock, carrying the
        // manage link's plaintext that no event should carry (docs/domain-events.md section 6, bookings).
        [("Yalla.Infrastructure.Services.ReservationReminders", typeof(IOutbox))] =
            "a new booking's first reminders (ScheduleRemindersAsync), after the insert commits",
        [("Yalla.Infrastructure.Services.ReservationService", typeof(IOutbox))] =
            "composes ReservationReminders for ReservationCreation: the first reminders",

        // A read, not a write.
        [("Yalla.Infrastructure.Services.PlatformOverviewQuery", typeof(IOutbox))] =
            "reads the outbox's health for the platform's Home (GetHealthAsync); writes nothing",
    };

    private static readonly Type[] SideEffectWriters = [typeof(IOutbox), typeof(IActivityLog)];

    [Fact]
    public void Only_the_named_exceptions_outside_the_handlers_take_the_outbox_or_the_activity_log()
    {
        var takers = ProductionCode.TypesOf(ProductionCode.Infrastructure)
            .Concat(ProductionCode.TypesOf(ProductionCode.Api))
            .Where(t => t is { IsClass: true, IsAbstract: false } && ProductionCode.IsWritten(t))
            .Where(t => t.Namespace?.StartsWith("Yalla.Infrastructure.DomainEvents.", StringComparison.Ordinal) != true)
            .SelectMany(t => t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .Where(p => SideEffectWriters.Contains(p.ParameterType))
                .Select(p => (Type: ProductionCode.NameOf(t), Takes: p.ParameterType)))
            .ToHashSet();

        var unexpected = takers
            .Where(t => !Allowed.ContainsKey(t))
            .Select(t => $"{t.Type} takes {t.Takes.Name} in its constructor");

        var stale = Allowed.Keys
            .Where(a => !takers.Contains(a))
            .Select(a => $"{a.Type} is allowed {a.Takes.Name} but no longer takes it: take the entry off Allowed");

        var offenders = unexpected.Concat(stale).ToList();

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "Services do not write side effects by hand: the activity entry, the outbox message, the feed row and the staff "
            + "push hang on domain events and are written by handlers in Infrastructure/DomainEvents/. Outside them only "
            + "the exceptions on HandWrittenSideEffectTests.Allowed may take IOutbox or IActivityLog, each with its reason.",
            offenders,
            "raise an event from the domain method that makes the change and write the side effect from its handler "
            + "(AGENTS.md, \"Raise a domain event and handle it\"; docs/domain-events.md sections 2 and 3). A genuine "
            + "one-path exception (a setting) goes on Allowed with a comment saying why."));
    }

    [Fact]
    public void The_outbox_and_the_activity_log_have_no_save()
    {
        var saves = SideEffectWriters
            .SelectMany(t => t.GetMethods())
            .Where(m => m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Flush", StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{ProductionCode.NameOf(m)}")
            .ToList();

        Assert.True(saves.Count == 0, ProductionCode.Violation(
            "IOutbox and IActivityLog deliberately have no save: what they add is written by the caller's single "
            + "SaveChangesAsync (or a lock scope's CommitAsync), in the same unit of work as the change.",
            saves,
            "add the row to the context and let the caller save. See AGENTS.md, golden rules, \"One unit of work\"."));
    }
}
