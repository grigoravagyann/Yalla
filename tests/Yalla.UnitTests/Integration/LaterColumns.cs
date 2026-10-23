using Microsoft.EntityFrameworkCore;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Columns of migrations that sort after the one a migration test stops at, but that today's entities write: put in
/// place so the fixture's builders can insert into the older schema.
/// </summary>
internal static class LaterColumns
{
    /// <summary>The <c>PreOrders</c> migration's columns on existing tables - and every later migration's.</summary>
    public static async Task AddPreOrderColumnsAsync(YallaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.Branches ADD AcceptsPreOrders bit NOT NULL CONSTRAINT DF_Test_AcceptsPreOrders DEFAULT 0, "
            + "PreOrderMinutesBefore int NOT NULL CONSTRAINT DF_Test_PreOrderMinutes DEFAULT 30; "
            + "ALTER TABLE dbo.MenuItems ADD AvailableForPreOrder bit NOT NULL CONSTRAINT DF_Test_AvailableForPreOrder DEFAULT 1; "
            + "ALTER TABLE dbo.Reservations ADD GuestConfirmedOnWayAtUtc datetime2 NULL;");

        await AddTakeawayColumnsAsync(db);
    }

    /// <summary>The <c>TakeawayAndRunningLate</c> migration's columns on existing tables - and every later migration's.</summary>
    public static async Task AddTakeawayColumnsAsync(YallaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.Branches ADD TakeawayEnabled bit NOT NULL CONSTRAINT DF_Test_TakeawayEnabled DEFAULT 0, "
            + "TakeawayAutoAccept bit NOT NULL CONSTRAINT DF_Test_TakeawayAutoAccept DEFAULT 0, "
            + "TakeawayPrepMinutes int NOT NULL CONSTRAINT DF_Test_TakeawayPrep DEFAULT 20, "
            + "TakeawayLastOrderMinutes int NOT NULL CONSTRAINT DF_Test_TakeawayLastOrder DEFAULT 30, "
            + "TakeawayMaxInProgress int NOT NULL CONSTRAINT DF_Test_TakeawayMax DEFAULT 5; "
            + "ALTER TABLE dbo.MenuItems ADD AvailableForTakeaway bit NOT NULL CONSTRAINT DF_Test_AvailableForTakeaway DEFAULT 1; "
            + "ALTER TABLE dbo.Reservations ADD GuestRunningLateMinutes int NULL, GuestRunningLateAtUtc datetime2 NULL, "
            + "StaffAskedOnMyWayAtUtc datetime2 NULL;");

        await AddGuestMessageColumnsAsync(db);
    }

    /// <summary>The <c>GuestMessages</c> migration's columns on existing tables.</summary>
    public static Task AddGuestMessageColumnsAsync(YallaDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.Branches ADD GuestsCanMessage bit NOT NULL CONSTRAINT DF_Test_GuestsCanMessage DEFAULT 1, "
            + "WaitersCanMessage bit NOT NULL CONSTRAINT DF_Test_WaitersCanMessage DEFAULT 1, "
            + "MessageTemplatesJson nvarchar(max) NULL, "
            + "MessageRemindAfterMinutes int NOT NULL CONSTRAINT DF_Test_MessageRemind DEFAULT 3;");
}
