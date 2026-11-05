using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>The console's half: the branch's pre-order settings.</summary>
internal sealed partial class PreOrderService
{
    // ---------------------------------------------------------------- the console

    public async Task<PreOrderSettingsView> GetSettingsAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the pre-order settings", cancellationToken);

        return await SettingsViewAsync(branchId, cancellationToken);
    }

    public async Task<PreOrderSettingsView> SaveSettingsAsync(
        Guid branchId, PreOrderSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await branchGuard.RequireAtBranchAsync(branchId, "Change the pre-order settings", cancellationToken);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var items = await db.MenuItems
            .Where(m => m.MenuCategory.BranchId == branchId)
            .ToListAsync(cancellationToken);

        var excluded = (settings.ExcludedMenuItemIds ?? []).ToHashSet();
        var violations = new List<FieldViolation>();

        if (settings.MinutesBefore is < PreOrderRules.MinMinutesBefore or > PreOrderRules.MaxMinutesBefore)
        {
            violations.Add(new FieldViolation(
                PreOrderRules.Fields.MinutesBefore,
                $"The card appears {PreOrderRules.MinMinutesBefore} to {PreOrderRules.MaxMinutesBefore} minutes before a "
                + $"booking; {settings.MinutesBefore} was given.",
                settings.MinutesBefore < PreOrderRules.MinMinutesBefore ? FieldBounds.Min : FieldBounds.Max,
                Min: PreOrderRules.MinMinutesBefore,
                Max: PreOrderRules.MaxMinutesBefore,
                Value: settings.MinutesBefore));
        }

        if (excluded.Except(items.Select(i => i.Id)).Any())
        {
            violations.Add(new FieldViolation(
                PreOrderRules.Fields.ExcludedMenuItemIds,
                "Some of these dishes are not on this branch's menu. Reload the menu and choose again.",
                FieldBounds.Conflict));
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        var settingsBefore = new
        {
            acceptsPreOrders = branch.AcceptsPreOrders,
            minutesBefore = branch.PreOrderMinutesBefore,
            excludedMenuItemIds = items.Where(i => !i.AvailableForPreOrder).Select(i => i.Id).ToList(),
        };

        // Signal 6 for every card on a rail now, and the next one, hangs on PreOrderSettingsSaved.
        branch.SetPreOrders(settings.AcceptsPreOrders, settings.MinutesBefore, clock.UtcNow);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Settings, ActivityActions.SettingsPreOrders, ActivitySubjects.Branch, branchId)
            {
                BranchId = branchId,
                VenueId = branch.VenueId,
                Before = settingsBefore,
                After = new
                {
                    acceptsPreOrders = settings.AcceptsPreOrders,
                    minutesBefore = settings.MinutesBefore,
                    excludedMenuItemIds = excluded.ToList(),
                },
                Summary = new { acceptsPreOrders = settings.AcceptsPreOrders, minutesBefore = settings.MinutesBefore },
            },
            cancellationToken);

        // The whole list each time: every dish not named may be pre-ordered.
        foreach (var item in items)
        {
            item.SetAvailableForPreOrder(!excluded.Contains(item.Id));
        }

        await domainEvents.DispatchAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Branch {BranchId} pre-orders {State}, {Minutes} minutes before, {Excluded} dishes excluded.",
            branchId, settings.AcceptsPreOrders ? "on" : "off", settings.MinutesBefore, excluded.Count);

        return await SettingsViewAsync(branchId, cancellationToken);
    }

    private async Task<PreOrderSettingsView> SettingsViewAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.AcceptsPreOrders, b.PreOrderMinutesBefore })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var excluded = await db.MenuItems
            .AsNoTracking()
            .Where(m => m.MenuCategory.BranchId == branchId && !m.AvailableForPreOrder)
            .OrderBy(m => m.MenuCategory.DisplayOrder)
            .ThenBy(m => m.DisplayOrder)
            .ThenBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        return new PreOrderSettingsView(
            branch.AcceptsPreOrders,
            branch.PreOrderMinutesBefore,
            excluded,
            new PreOrderSettingsBounds(PreOrderRules.MinMinutesBefore, PreOrderRules.MaxMinutesBefore),
            PreOrderRules.EditableUntilHoursBefore,
            PreOrderRules.OnYourWayMinutesBefore);
    }
}
