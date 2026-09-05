using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Yalla.Application.Abstractions;
using Yalla.Domain.Audit;
using Yalla.Domain.Common;
using Yalla.Domain.Identity;
using Yalla.Domain.Menus;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// The single unit of work over the Yalla database.
/// </summary>
/// <remarks>
/// All mapping lives in <c>IEntityTypeConfiguration</c> classes in this assembly and is applied
/// by <see cref="ModelBuilder.ApplyConfigurationsFromAssembly(System.Reflection.Assembly, System.Func{System.Type, bool})"/>.
/// The domain entities carry no data annotations and no fluent configuration, so the domain
/// project has no dependency on EF Core at all.
/// </remarks>
public sealed class YallaDbContext : DbContext
{
    private readonly IClock _clock;

    public YallaDbContext(DbContextOptions<YallaDbContext> options, IClock clock)
        : base(options) => _clock = clock;

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<OpeningHours> OpeningHours => Set<OpeningHours>();

    public DbSet<FloorArea> FloorAreas => Set<FloorArea>();

    public DbSet<DiningTable> DiningTables => Set<DiningTable>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<TableSession> TableSessions => Set<TableSession>();

    public DbSet<Tab> Tabs => Set<Tab>();

    public DbSet<TabParticipant> TabParticipants => Set<TabParticipant>();

    public DbSet<TabJoinToken> TabJoinTokens => Set<TabJoinToken>();

    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();

    public DbSet<MenuItem> MenuItems => Set<MenuItem>();

    public DbSet<TabOrder> TabOrders => Set<TabOrder>();

    public DbSet<TabOrderLine> TabOrderLines => Set<TabOrderLine>();

    public DbSet<TabOrderLineShare> TabOrderLineShares => Set<TabOrderLineShare>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();

    public DbSet<TableStateChange> TableStateChanges => Set<TableStateChange>();

    public DbSet<PlatformAuditLog> PlatformAuditLogs => Set<PlatformAuditLog>();

    public DbSet<ProcessedCommand> ProcessedCommands => Set<ProcessedCommand>();

    public DbSet<TabAdjustment> TabAdjustments => Set<TabAdjustment>();

    public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();

    public DbSet<TabEvent> TabEvents => Set<TabEvent>();

    public DbSet<Yalla.Domain.Media.Photo> Photos => Set<Yalla.Domain.Media.Photo>();

    public DbSet<DinerUser> DinerUsers => Set<DinerUser>();

    public DbSet<PhoneVerificationCode> PhoneVerificationCodes => Set<PhoneVerificationCode>();

    public DbSet<StaffDevice> StaffDevices => Set<StaffDevice>();

    public DbSet<StaffEnrolmentCode> StaffEnrolmentCodes => Set<StaffEnrolmentCode>();

    public DbSet<StaffSession> StaffSessions => Set<StaffSession>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(YallaDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every instant in this system is UTC and every one of them is datetime2. Setting it as a
        // convention means no configuration class can quietly forget it. DateOnly and TimeOnly
        // (opening hours, the local date and time on a booking) are wall-clock values and map to
        // date and time - they are deliberately not instants and are never converted to UTC.
        //
        // The conversion is what makes that true on the way BACK. datetime2 stores no offset, so
        // SQL Server hands every instant back as DateTimeKind.Unspecified - which System.Text.Json
        // then serialises without a trailing Z, and a client parses as its own local time. A
        // booking read from the database was going out as "2026-09-11T15:00:00" next to one
        // computed in memory as "2026-09-11T15:00:00Z", four hours apart in Yerevan and identical
        // to the eye. Stamping the kind on read costs nothing and removes the ambiguity entirely.
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("datetime2")
            .HaveConversion<UtcDateTimeConverter>();

        // Enums persist as int, which is EF Core's default and is left in place deliberately:
        // storing them as strings would let a member rename orphan existing rows. No
        // configuration class converts an enum to a string, and none should.
    }

    /// <summary>
    /// Stamps <see cref="Entity.CreatedAtUtc"/> on every inserted row that does not already carry
    /// one, so no caller has to remember to and no row is ever missing its creation time.
    /// </summary>
    /// <remarks>
    /// This is the overload every other <c>SaveChangesAsync</c> entry point funnels into.
    /// </remarks>
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampCreationTimes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc cref="SaveChangesAsync(bool, CancellationToken)"/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampCreationTimes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void StampCreationTimes()
    {
        var now = _clock.UtcNow;

        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            var createdAt = entry.Property(e => e.CreatedAtUtc);
            if (createdAt.CurrentValue == default)
            {
                createdAt.CurrentValue = now;
            }
        }
    }
}
