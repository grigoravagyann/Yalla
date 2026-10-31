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

    /// <summary>The <c>GuestMessages</c> migration's columns on existing tables - and every later migration's.</summary>
    public static async Task AddGuestMessageColumnsAsync(YallaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.Branches ADD GuestsCanMessage bit NOT NULL CONSTRAINT DF_Test_GuestsCanMessage DEFAULT 1, "
            + "WaitersCanMessage bit NOT NULL CONSTRAINT DF_Test_WaitersCanMessage DEFAULT 1, "
            + "MessageTemplatesJson nvarchar(max) NULL, "
            + "MessageRemindAfterMinutes int NOT NULL CONSTRAINT DF_Test_MessageRemind DEFAULT 3;");

        await AddActivityColumnsAsync(db);
    }

    /// <summary>
    /// The <c>ActivityHistory</c> migration's columns on existing tables: whose each table, order, payment and claim was -
    /// and every later migration's.
    /// </summary>
    public static async Task AddActivityColumnsAsync(YallaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.TableSessions ADD ServerStaffMemberId uniqueidentifier NULL, ServerAssignedAtUtc datetime2 NULL; "
            + "ALTER TABLE dbo.TabOrders ADD ServerStaffMemberId uniqueidentifier NULL; "
            + "ALTER TABLE dbo.Payments ADD RecordedByStaffId uniqueidentifier NULL, ServerStaffMemberId uniqueidentifier NULL; "
            + "ALTER TABLE dbo.ServiceRequests ADD ClaimedAtUtc datetime2 NULL, ClaimedByStaffId uniqueidentifier NULL;");

        await AddStructuredDeviceColumnsAsync(db);
    }

    /// <summary>The <c>StructuredDeviceLabels</c> migration's columns on existing tables: what a phone runs on - and every later migration's.</summary>
    public static async Task AddStructuredDeviceColumnsAsync(YallaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.StaffDevices ADD Platform int NULL, Browser int NULL;");

        await AddBranchFeatureColumnsAsync(db);
    }

    /// <summary>
    /// The <c>ReplaceSubscriptionTierWithBranchFeatures</c> migration's column: the features the platform allows, every
    /// one here (1023). The older <c>SubscriptionTier</c> keeps its database default, so a builder never names it.
    /// </summary>
    public static async Task AddBranchFeatureColumnsAsync(YallaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.Branches ADD AllowedFeatures int NOT NULL CONSTRAINT DF_Test_AllowedFeatures DEFAULT 1023;");
        await AddTimeOnlyColumnsAsync(db);
    }

    /// <summary>
    /// The <c>BookByTimeOnly</c> migration's columns: a branch's booking mode (choose a table, 1) and guests at once, and
    /// whether a booking was made without a table. The table stays required here, as every builder names one - and every
    /// later migration's.
    /// </summary>
    public static async Task AddTimeOnlyColumnsAsync(YallaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.Branches ADD ReservationPolicy_BookingMode int NOT NULL CONSTRAINT DF_Test_BookingMode DEFAULT 1, "
            + "ReservationPolicy_GuestsAtOnce int NULL; "
            + "ALTER TABLE dbo.Reservations ADD BookedWithoutTable bit NOT NULL CONSTRAINT DF_Test_BookedWithoutTable DEFAULT 0;");

        await AddStaffTakenColumnsAsync(db);
    }

    /// <summary>
    /// The <c>StaffTakenBookings</c> migration's columns: who took a booking by hand, and who recorded the guest's
    /// "coming" for them - both null on every booking a builder makes.
    /// </summary>
    public static Task AddStaffTakenColumnsAsync(YallaDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.Reservations ADD TakenByStaffId uniqueidentifier NULL, OnWayRecordedByStaffId uniqueidentifier NULL;");
}
