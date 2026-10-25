using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Adds an <see cref="ActivityEntry"/> to the current unit of work. See <see cref="IActivityLog"/> and
/// <c>docs/activity.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who acted is read once per request</b> and kept: the staff member's name and role from their stored row (not the
/// token - a renamed waiter reads with today's name), the device's stored name. A request that records three entries
/// reads them once, and not at all when the context already tracks the rows.
/// </para>
/// <para>
/// <b>The device lookup is tolerant.</b> Any staff token that names a device is looked up by id in
/// <c>StaffDevices</c>; a device that is not found is recorded by id with no label rather than refused, and a token
/// that names none is the console. A tablet is labelled with its name ("Hall tablet"), a personal phone with its
/// person's ("Aram's phone") - what the manager would call it, not whatever the browser called itself when paired.
/// </para>
/// </remarks>
internal sealed class ActivityLog(YallaDbContext db, ICurrentActor actor, IClock clock) : IActivityLog
{
    /// <summary>The label for a staff member signed in to the console rather than on an enrolled device.</summary>
    internal const string ConsoleLabel = "Console";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<Guid, Guid> _venueOfBranch = [];

    private Actor? _actor;

    public async Task RecordAsync(ActivityRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var venueId = record.VenueId
                      ?? await VenueOfAsync(
                          record.BranchId ?? throw new ArgumentException(
                              "An activity entry names its branch or its venue.", nameof(record)),
                          cancellationToken);

        var who = record.BySystem
            ? Actor.System
            : record.ByGuest
                ? Actor.Diner
                : record.ProvedBy is { } proved
                    ? await StaffAsync(proved.StaffMemberId, proved.DeviceId, cancellationToken)
                    : await ActorAsync(record, cancellationToken);

        // Staff of another venue are never named in this one's History: a no-show at one venue cancelling a booking
        // at another, say, is the platform's doing there, not that waiter's.
        if (who.Kind == ActivityActorKind.Staff && who.VenueId != venueId)
        {
            who = Actor.System;
        }
        var isMoney = record.IsMoney || ActivityActions.Money.Contains(record.Action);

        db.ActivityEntries.Add(new ActivityEntry(
            venueId,
            record.BranchId,
            record.OccurredAtUtc ?? clock.UtcNow,
            who.Kind,
            who.StaffMemberId,
            who.Kind == ActivityActorKind.Diner ? record.GuestName : who.Name,
            who.DeviceId,
            who.DeviceLabel,
            record.Category,
            record.Action,
            record.SubjectType,
            record.SubjectId,
            Serialize(record.Summary) ?? "{}",
            subjectCode: record.SubjectCode,
            tableLabel: record.TableLabel,
            guestName: record.GuestName,
            itemNames: record.ItemNames is { Count: > 0 } items ? string.Join(", ", items.Distinct()) : null,
            amountAmd: record.AmountAmd,
            isMoney: isMoney,
            beforeJson: Serialize(record.Before),
            afterJson: Serialize(record.After),
            reason: record.Reason,
            tableSessionId: record.TableSessionId));
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, value.GetType(), Json);

    private async Task<Guid> VenueOfAsync(Guid branchId, CancellationToken cancellationToken)
    {
        if (_venueOfBranch.TryGetValue(branchId, out var known))
        {
            return known;
        }

        var venueId = db.Branches.Local.FirstOrDefault(b => b.Id == branchId)?.VenueId
                      ?? await db.Branches
                          .AsNoTracking()
                          .Where(b => b.Id == branchId)
                          .Select(b => (Guid?)b.VenueId)
                          .FirstOrDefaultAsync(cancellationToken)
                      ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        _venueOfBranch[branchId] = venueId;

        return venueId;
    }

    private async Task<Actor> ActorAsync(ActivityRecord record, CancellationToken cancellationToken)
    {
        if (_actor is { } known)
        {
            return known;
        }

        _actor = actor.Type switch
        {
            ActorType.Staff when actor.StaffMemberId is { } staffId => await StaffAsync(staffId, actor.DeviceId, cancellationToken),
            ActorType.Diner => Actor.Diner,
            _ => Actor.System,
        };

        return _actor;
    }

    private async Task<Actor> StaffAsync(Guid staffId, Guid? deviceId, CancellationToken cancellationToken)
    {
        var tracked = db.StaffMembers.Local.FirstOrDefault(s => s.Id == staffId);

        var (role, name, venueId) = tracked is not null
            ? (tracked.Role, (string?)tracked.FullName, (Guid?)tracked.VenueId)
            : await db.StaffMembers
                .AsNoTracking()
                .Where(s => s.Id == staffId)
                .Select(s => new ValueTuple<StaffRole, string?, Guid?>(s.Role, s.FullName, s.VenueId))
                .FirstOrDefaultAsync(cancellationToken);

        var kind = (role == StaffRole.PlatformAdmin
                    || (actor.Role == StaffRole.PlatformAdmin && actor.StaffMemberId == staffId))
            ? ActivityActorKind.PlatformAdmin
            : ActivityActorKind.Staff;

        if (deviceId is not { } device || device == Guid.Empty)
        {
            return new Actor(kind, staffId, name, null, ConsoleLabel, venueId);
        }

        var label = await DeviceLabelAsync(device, staffId, name, cancellationToken);

        return new Actor(kind, staffId, name, device, label, venueId);
    }

    /// <summary>
    /// A tablet's name, or "{first name}'s phone" for a personal phone - its person, read from their row when they
    /// are not the one acting. Null for a device that is not found.
    /// </summary>
    private async Task<string?> DeviceLabelAsync(
        Guid deviceId, Guid actingStaffId, string? actingName, CancellationToken cancellationToken)
    {
        var tracked = db.StaffDevices.Local.FirstOrDefault(d => d.Id == deviceId);

        var device = tracked is not null
            ? new DeviceRow(tracked.Name, tracked.Kind, tracked.BoundStaffMemberId)
            : await db.StaffDevices
                .AsNoTracking()
                .Where(d => d.Id == deviceId)
                .Select(d => new DeviceRow(d.Name, d.Kind, d.BoundStaffMemberId))
                .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
        {
            return null;
        }

        if (device.Kind != StaffDeviceKind.Phone || device.BoundStaffMemberId is not { } personId)
        {
            return device.Name;
        }

        var person = personId == actingStaffId
            ? actingName
            : db.StaffMembers.Local.FirstOrDefault(s => s.Id == personId)?.FullName
              ?? await db.StaffMembers
                  .AsNoTracking()
                  .Where(s => s.Id == personId)
                  .Select(s => s.FullName)
                  .FirstOrDefaultAsync(cancellationToken);

        return PhoneLabel(person) ?? device.Name;
    }

    /// <summary>"Aram's phone" for Aram Petrosyan; null for nobody.</summary>
    internal static string? PhoneLabel(string? fullName)
    {
        var first = fullName?.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrEmpty(first) ? null : $"{first}'s phone";
    }

    private sealed record DeviceRow(string Name, StaffDeviceKind Kind, Guid? BoundStaffMemberId);

    private sealed record Actor(
        ActivityActorKind Kind,
        Guid? StaffMemberId,
        string? Name,
        Guid? DeviceId,
        string? DeviceLabel,
        Guid? VenueId = null)
    {
        public static readonly Actor System = new(ActivityActorKind.System, null, null, null, null);

        public static readonly Actor Diner = new(ActivityActorKind.Diner, null, null, null, null);
    }
}
