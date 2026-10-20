using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Booking without a proved number, one active booking per number, and a waiter's admission for a scan
    /// with no proved number behind it (2026-10-20) - see <c>docs/reservations.md</c> and <c>docs/tabs.md</c>.
    /// </summary>
    /// <remarks>
    /// Existing branches approve unverified bookings first and make unverified scans wait for a waiter (both
    /// defaults, both a branch setting), existing tabs are admitted (they could all order), and existing
    /// account bookings are keyed on their account's number so the one-active-booking rule sees them from the
    /// first booking after the deploy.
    /// </remarks>
    public partial class UnverifiedGuests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Tabs_TableSessionId",
                table: "Tabs");

            migrationBuilder.AddColumn<int>(
                name: "Admission",
                table: "Tabs",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<DateTime>(
                name: "AdmissionDecidedAtUtc",
                table: "Tabs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AdmissionDecidedByStaffId",
                table: "Tabs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdmittedBecause",
                table: "Tabs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Signal",
                table: "TableStateChanges",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookerDeviceHash",
                table: "Reservations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookerPhoneE164",
                table: "Reservations",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PhoneUnverified",
                table: "Reservations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReservationPolicy_UnverifiedBookingsNeedApproval",
                table: "Branches",
                type: "bit",
                nullable: false,

                // The shipped default for every existing branch. Not a model default - see
                // BranchConfiguration - so only this migration states it.
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservationPolicy_UnverifiedMaxPartySize",
                table: "Branches",
                type: "int",
                nullable: false,

                // Six for every existing branch. Not a model default either: 0 is a setting, and EF would
                // leave an int equal to its CLR default out of the write.
                defaultValue: 6);

            migrationBuilder.AddColumn<bool>(
                name: "ReservationPolicy_UnverifiedScansNeedAdmission",
                table: "Branches",
                type: "bit",
                nullable: false,

                // On for every existing branch, as for a new one (decided 2026-10-20, after review). Not a
                // model default, for the same reason as the setting above.
                defaultValue: true);

            // Every booking an account made is keyed on the account's number, in the canonical form the
            // service now writes (PhoneNumber.Canonical): for Armenia, the one country in service, that is
            // the number without a trunk 0 after +374. A booking from before the rule with no account (none
            // exist outside tests) stays unkeyed: exempt.
            migrationBuilder.Sql(
                """
                UPDATE r
                SET r.BookerPhoneE164 = CASE
                        WHEN d.PhoneE164 LIKE '+3740%' THEN '+374' + SUBSTRING(d.PhoneE164, 6, 32)
                        ELSE d.PhoneE164
                    END
                FROM Reservations r
                INNER JOIN DinerUsers d ON d.Id = r.DinerUserId
                WHERE r.BookerPhoneE164 IS NULL AND d.PhoneE164 IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_BranchId_AwaitingAdmission",
                table: "Tabs",
                columns: new[] { "BranchId", "OpenedAtUtc" },
                filter: "[Admission] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_BranchId_Rejected",
                table: "Tabs",
                columns: new[] { "BranchId", "AdmissionDecidedAtUtc" },
                filter: "[Admission] = 3");

            migrationBuilder.CreateIndex(
                name: "UX_Tabs_TableSessionId",
                table: "Tabs",
                column: "TableSessionId",
                unique: true,
                filter: "[Admission] <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_BookerDeviceHash_Active",
                table: "Reservations",
                columns: new[] { "BookerDeviceHash", "EndUtc" },
                filter: "[BookerDeviceHash] IS NOT NULL AND [Status] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_BookerPhoneE164_Active",
                table: "Reservations",
                columns: new[] { "BookerPhoneE164", "EndUtc" },
                filter: "[BookerPhoneE164] IS NOT NULL AND [Status] IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // First, while the Admission column still exists (review S5): a sitting that had a rejected tab
            // and then a real one has two tabs, which the old unfiltered index cannot hold. Those rejected
            // tabs are deleted with their rows - phantoms by definition, with nothing ever ordered on them.
            migrationBuilder.Sql(
                """
                DECLARE @phantoms TABLE (Id uniqueidentifier PRIMARY KEY);
                INSERT INTO @phantoms
                SELECT t.Id FROM Tabs t
                WHERE t.Admission = 3
                  AND EXISTS (SELECT 1 FROM Tabs o WHERE o.TableSessionId = t.TableSessionId AND o.Id <> t.Id);
                DELETE FROM TabEvents WHERE TabId IN (SELECT Id FROM @phantoms);
                DELETE FROM TabJoinTokens WHERE TabId IN (SELECT Id FROM @phantoms);
                DELETE FROM ServiceRequests WHERE TabId IN (SELECT Id FROM @phantoms);
                DELETE FROM TabParticipants WHERE TabId IN (SELECT Id FROM @phantoms);
                DELETE FROM Tabs WHERE Id IN (SELECT Id FROM @phantoms);
                """);

            migrationBuilder.DropIndex(
                name: "IX_Tabs_BranchId_AwaitingAdmission",
                table: "Tabs");

            migrationBuilder.DropIndex(
                name: "IX_Tabs_BranchId_Rejected",
                table: "Tabs");

            migrationBuilder.DropIndex(
                name: "UX_Tabs_TableSessionId",
                table: "Tabs");

            migrationBuilder.DropIndex(
                name: "IX_Reservations_BookerDeviceHash_Active",
                table: "Reservations");

            migrationBuilder.DropIndex(
                name: "IX_Reservations_BookerPhoneE164_Active",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "Admission",
                table: "Tabs");

            migrationBuilder.DropColumn(
                name: "AdmissionDecidedAtUtc",
                table: "Tabs");

            migrationBuilder.DropColumn(
                name: "AdmissionDecidedByStaffId",
                table: "Tabs");

            migrationBuilder.DropColumn(
                name: "AdmittedBecause",
                table: "Tabs");

            migrationBuilder.DropColumn(
                name: "Signal",
                table: "TableStateChanges");

            migrationBuilder.DropColumn(
                name: "BookerDeviceHash",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "BookerPhoneE164",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "PhoneUnverified",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ReservationPolicy_UnverifiedBookingsNeedApproval",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "ReservationPolicy_UnverifiedMaxPartySize",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "ReservationPolicy_UnverifiedScansNeedAdmission",
                table: "Branches");

            migrationBuilder.CreateIndex(
                name: "UX_Tabs_TableSessionId",
                table: "Tabs",
                column: "TableSessionId",
                unique: true);
        }
    }
}
