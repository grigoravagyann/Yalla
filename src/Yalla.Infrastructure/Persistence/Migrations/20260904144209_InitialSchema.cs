using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Venues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Venues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VenueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    FloorWidth = table.Column<int>(type: "int", nullable: false),
                    FloorHeight = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_TurnTimeMinutes = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_BufferMinutes = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_GraceMinutes = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_LateNudgeAfterMinutes = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_GraceExtensionMinutes = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_MinLeadMinutes = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_BookingWindowDays = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_CancellationDeadlineMinutes = table.Column<int>(type: "int", nullable: false),
                    ReservationPolicy_AutoConfirm = table.Column<bool>(type: "bit", nullable: false),
                    ReservationPolicy_ServiceChargePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ReservationPolicy_PricesIncludeVat = table.Column<bool>(type: "bit", nullable: false),
                    ReservationPolicy_MaxSeatOverhang = table.Column<int>(type: "int", nullable: true),
                    ReservationPolicy_ApprovalRequiredAbovePartySize = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Branches_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FloorAreas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorAreas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloorAreas_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MenuCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuCategories_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpeningHours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    OpensAt = table.Column<TimeOnly>(type: "time", nullable: false),
                    ClosesAt = table.Column<TimeOnly>(type: "time", nullable: false),
                    ClosesNextDay = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningHours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpeningHours_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VenueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PinHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffMembers_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffMembers_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DiningTables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FloorAreaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Label = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Seats = table.Column<int>(type: "int", nullable: false),
                    X = table.Column<int>(type: "int", nullable: false),
                    Y = table.Column<int>(type: "int", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    RotationDegrees = table.Column<double>(type: "float", nullable: false),
                    Shape = table.Column<int>(type: "int", nullable: false),
                    IsBookable = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    QrToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiningTables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiningTables_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiningTables_FloorAreas_FloorAreaId",
                        column: x => x.FloorAreaId,
                        principalTable: "FloorAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MenuItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MenuCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PriceAmd = table.Column<long>(type: "bigint", nullable: false),
                    PhotoUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Ingredients = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Allergens = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PortionSize = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SpiceLevel = table.Column<int>(type: "int", nullable: false),
                    PrepMinutes = table.Column<int>(type: "int", nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuItems_MenuCategories_MenuCategoryId",
                        column: x => x.MenuCategoryId,
                        principalTable: "MenuCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiningTableId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DinerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GuestName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GuestPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PartySize = table.Column<int>(type: "int", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LocalStartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HoldExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    GraceExtensionsUsed = table.Column<int>(type: "int", nullable: false),
                    StayHint = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reservations_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reservations_DiningTables_DiningTableId",
                        column: x => x.DiningTableId,
                        principalTable: "DiningTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TableStateChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiningTableId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<int>(type: "int", nullable: false),
                    ToStatus = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActorType = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableStateChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TableStateChanges_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableStateChanges_DiningTables_DiningTableId",
                        column: x => x.DiningTableId,
                        principalTable: "DiningTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TableSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiningTableId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PartySize = table.Column<int>(type: "int", nullable: false),
                    SeatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SeatedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TableSessions_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableSessions_DiningTables_DiningTableId",
                        column: x => x.DiningTableId,
                        principalTable: "DiningTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableSessions_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableSessions_StaffMembers_SeatedByStaffId",
                        column: x => x.SeatedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tabs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiningTableId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SettlementMode = table.Column<int>(type: "int", nullable: false),
                    SettlementModeLockedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HostParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ServiceChargePercentSnapshot = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    HideTotalFromGuests = table.Column<bool>(type: "bit", nullable: false),
                    SubtotalAmd = table.Column<long>(type: "bigint", nullable: false),
                    ServiceChargeAmd = table.Column<long>(type: "bigint", nullable: false),
                    TotalAmd = table.Column<long>(type: "bigint", nullable: false),
                    PaidAmd = table.Column<long>(type: "bigint", nullable: false),
                    RemainingAmd = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tabs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tabs_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tabs_DiningTables_DiningTableId",
                        column: x => x.DiningTableId,
                        principalTable: "DiningTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tabs_TableSessions_TableSessionId",
                        column: x => x.TableSessionId,
                        principalTable: "TableSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TabJoinTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedByParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabJoinTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TabJoinTokens_Tabs_TabId",
                        column: x => x.TabId,
                        principalTable: "Tabs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TabParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CanOrder = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CanSeeTableTotal = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CanPay = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TabParticipants_Tabs_TabId",
                        column: x => x.TabId,
                        principalTable: "Tabs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AmountAmd = table.Column<long>(type: "bigint", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_TabParticipants_TabParticipantId",
                        column: x => x.TabParticipantId,
                        principalTable: "TabParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Tabs_TabId",
                        column: x => x.TabId,
                        principalTable: "Tabs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TabOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlacedByParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PlacedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PlacedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TabOrders_StaffMembers_PlacedByStaffId",
                        column: x => x.PlacedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TabOrders_TabParticipants_PlacedByParticipantId",
                        column: x => x.PlacedByParticipantId,
                        principalTable: "TabParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TabOrders_Tabs_TabId",
                        column: x => x.TabId,
                        principalTable: "Tabs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TabOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MenuItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UnitPriceAmdSnapshot = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    IsShared = table.Column<bool>(type: "bit", nullable: false),
                    VoidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedByStaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TabOrderLines_MenuItems_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "MenuItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TabOrderLines_StaffMembers_VoidedByStaffId",
                        column: x => x.VoidedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TabOrderLines_TabOrders_TabOrderId",
                        column: x => x.TabOrderId,
                        principalTable: "TabOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TabOrderLineShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TabParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabOrderLineShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TabOrderLineShares_TabOrderLines_TabOrderLineId",
                        column: x => x.TabOrderLineId,
                        principalTable: "TabOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TabOrderLineShares_TabParticipants_TabParticipantId",
                        column: x => x.TabParticipantId,
                        principalTable: "TabParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Branches_VenueId_Slug",
                table: "Branches",
                columns: new[] { "VenueId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiningTables_BranchId_Label",
                table: "DiningTables",
                columns: new[] { "BranchId", "Label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiningTables_BranchId_Status",
                table: "DiningTables",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DiningTables_FloorAreaId",
                table: "DiningTables",
                column: "FloorAreaId");

            migrationBuilder.CreateIndex(
                name: "IX_DiningTables_QrToken",
                table: "DiningTables",
                column: "QrToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreas_BranchId_DisplayOrder",
                table: "FloorAreas",
                columns: new[] { "BranchId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MenuCategories_BranchId_DisplayOrder",
                table: "MenuCategories",
                columns: new[] { "BranchId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MenuItems_MenuCategoryId_DisplayOrder",
                table: "MenuItems",
                columns: new[] { "MenuCategoryId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningHours_BranchId_Day",
                table: "OpeningHours",
                columns: new[] { "BranchId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProviderReference",
                table: "Payments",
                column: "ProviderReference");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TabId_Status",
                table: "Payments",
                columns: new[] { "TabId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TabParticipantId",
                table: "Payments",
                column: "TabParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_BranchId_StartUtc",
                table: "Reservations",
                columns: new[] { "BranchId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_Code",
                table: "Reservations",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_DiningTableId_StartUtc_EndUtc",
                table: "Reservations",
                columns: new[] { "DiningTableId", "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_BranchId",
                table: "StaffMembers",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_VenueId_IsActive",
                table: "StaffMembers",
                columns: new[] { "VenueId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_TabJoinTokens_TabId",
                table: "TabJoinTokens",
                column: "TabId");

            migrationBuilder.CreateIndex(
                name: "IX_TabJoinTokens_Token",
                table: "TabJoinTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TableSessions_DiningTableId_SeatedAtUtc",
                table: "TableSessions",
                columns: new[] { "DiningTableId", "SeatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TableSessions_Open",
                table: "TableSessions",
                columns: new[] { "BranchId", "DiningTableId" },
                filter: "[ClosedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TableSessions_ReservationId",
                table: "TableSessions",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_TableSessions_SeatedByStaffId",
                table: "TableSessions",
                column: "SeatedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TableStateChanges_BranchId_AtUtc",
                table: "TableStateChanges",
                columns: new[] { "BranchId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TableStateChanges_DiningTableId_AtUtc",
                table: "TableStateChanges",
                columns: new[] { "DiningTableId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TabOrderLines_MenuItemId",
                table: "TabOrderLines",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrderLines_TabOrderId",
                table: "TabOrderLines",
                column: "TabOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrderLines_VoidedByStaffId",
                table: "TabOrderLines",
                column: "VoidedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrderLineShares_TabOrderLineId_TabParticipantId",
                table: "TabOrderLineShares",
                columns: new[] { "TabOrderLineId", "TabParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TabOrderLineShares_TabParticipantId",
                table: "TabOrderLineShares",
                column: "TabParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrders_PlacedByParticipantId",
                table: "TabOrders",
                column: "PlacedByParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrders_PlacedByStaffId",
                table: "TabOrders",
                column: "PlacedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TabOrders_TabId_Status",
                table: "TabOrders",
                columns: new[] { "TabId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TabParticipants_TabId_DeviceId",
                table: "TabParticipants",
                columns: new[] { "TabId", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_TabParticipants_TabId_Status",
                table: "TabParticipants",
                columns: new[] { "TabId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_BranchId_Status",
                table: "Tabs",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_DiningTableId",
                table: "Tabs",
                column: "DiningTableId");

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_TableSessionId",
                table: "Tabs",
                column: "TableSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Venues_Slug",
                table: "Venues",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpeningHours");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "TabJoinTokens");

            migrationBuilder.DropTable(
                name: "TableStateChanges");

            migrationBuilder.DropTable(
                name: "TabOrderLineShares");

            migrationBuilder.DropTable(
                name: "TabOrderLines");

            migrationBuilder.DropTable(
                name: "MenuItems");

            migrationBuilder.DropTable(
                name: "TabOrders");

            migrationBuilder.DropTable(
                name: "MenuCategories");

            migrationBuilder.DropTable(
                name: "TabParticipants");

            migrationBuilder.DropTable(
                name: "Tabs");

            migrationBuilder.DropTable(
                name: "TableSessions");

            migrationBuilder.DropTable(
                name: "Reservations");

            migrationBuilder.DropTable(
                name: "StaffMembers");

            migrationBuilder.DropTable(
                name: "DiningTables");

            migrationBuilder.DropTable(
                name: "FloorAreas");

            migrationBuilder.DropTable(
                name: "Branches");

            migrationBuilder.DropTable(
                name: "Venues");
        }
    }
}
