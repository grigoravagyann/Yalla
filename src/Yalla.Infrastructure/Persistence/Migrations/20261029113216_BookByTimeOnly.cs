using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Booking by time only (2026-10-29): a branch's <c>ReservationPolicy_BookingMode</c> (1 choose a table, 2 time
    /// only) and <c>ReservationPolicy_GuestsAtOnce</c>; a booking's table becomes optional, with
    /// <c>BookedWithoutTable</c> marking the ones made by time only and a filtered index for the room's count.
    /// </summary>
    /// <remarks>
    /// Every existing branch keeps choosing tables (mode 1) and every existing booking has its table, so nothing about
    /// them changes. <see cref="Down"/> refuses while a booking without a table exists: the old column cannot hold it,
    /// and inventing a table for somebody's booking is not a rollback.
    /// </remarks>
    public partial class BookByTimeOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "DiningTableId",
                table: "Reservations",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<bool>(
                name: "BookedWithoutTable",
                table: "Reservations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // A default only so the column can be added to a table with rows; the model has none, every write states it.
            migrationBuilder.AddColumn<int>(
                name: "ReservationPolicy_BookingMode",
                table: "Branches",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ReservationPolicy_GuestsAtOnce",
                table: "Branches",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_TimeOnly_BranchId_StartUtc_EndUtc",
                table: "Reservations",
                columns: new[] { "BranchId", "StartUtc", "EndUtc" },
                filter: "[BookedWithoutTable] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM dbo.Reservations WHERE DiningTableId IS NULL)
                    THROW 50000, 'Bookings made by time only have no table yet; the earlier schema cannot hold them. Seat or cancel them first.', 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Reservations_TimeOnly_BranchId_StartUtc_EndUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "BookedWithoutTable",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ReservationPolicy_BookingMode",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "ReservationPolicy_GuestsAtOnce",
                table: "Branches");

            migrationBuilder.AlterColumn<Guid>(
                name: "DiningTableId",
                table: "Reservations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
