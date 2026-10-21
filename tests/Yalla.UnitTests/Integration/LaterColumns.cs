using Microsoft.EntityFrameworkCore;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Columns of migrations that sort after the one a migration test stops at, but that today's entities write: put in
/// place so the fixture's builders can insert into the older schema.
/// </summary>
internal static class LaterColumns
{
    /// <summary>The <c>PreOrders</c> migration's columns on existing tables.</summary>
    public static Task AddPreOrderColumnsAsync(YallaDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.Branches ADD AcceptsPreOrders bit NOT NULL CONSTRAINT DF_Test_AcceptsPreOrders DEFAULT 0, "
            + "PreOrderMinutesBefore int NOT NULL CONSTRAINT DF_Test_PreOrderMinutes DEFAULT 30; "
            + "ALTER TABLE dbo.MenuItems ADD AvailableForPreOrder bit NOT NULL CONSTRAINT DF_Test_AvailableForPreOrder DEFAULT 1; "
            + "ALTER TABLE dbo.Reservations ADD GuestConfirmedOnWayAtUtc datetime2 NULL;");
}
