using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A throwaway SQL Server database, migrated from scratch, shared by the integration tests.
/// </summary>
/// <remarks>
/// <para>
/// These tests need a real database rather than the in-memory provider, because the two things
/// they are proving are things only SQL Server does: <c>rowversion</c> concurrency tokens and
/// filtered unique indexes. The in-memory provider would pass every assertion while enforcing
/// neither, which is worse than not running at all.
/// </para>
/// <para>
/// When no server is reachable the tests <b>skip</b> rather than fail or silently pass, so a
/// machine without SQL Server gives an honest result.
/// </para>
/// <para>
/// The schema is created with <c>Migrate</c>, not <c>EnsureCreated</c>, so the migrations
/// themselves are exercised - including the check constraints and filtered indexes that
/// <c>EnsureCreated</c> would build from the model and never validate.
/// </para>
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"Yalla_Tests_{Guid.NewGuid():N}";

    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString =
            $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

        try
        {
            await using var connection = new SqlConnection(MasterConnectionString);
            await connection.OpenAsync();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or PlatformNotSupportedException)
        {
            SkipReason = $"No SQL Server reachable on localhost: {ex.Message}";
            return;
        }

        await using var db = CreateContext(new TestClock(DateTime.UtcNow));
        await db.Database.MigrateAsync();

        IsAvailable = true;
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        // Force it closed first: a lingering pooled connection makes DROP DATABASE hang.
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             IF DB_ID(N'{_databaseName}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{_databaseName}];
             END
             """;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A fresh context over the test database. Each one is its own unit of work. Interceptors are
    /// how the floor tests count the statements actually sent, which is the only way to catch an
    /// N+1 - the results look identical either way.
    /// </summary>
    public YallaDbContext CreateContext(IClock clock, params IInterceptor[] interceptors) =>
        new(
            new DbContextOptionsBuilder<YallaDbContext>()
                .UseSqlServer(ConnectionString)
                .EnableSensitiveDataLogging()
                .AddInterceptors(interceptors)
                .Options,
            clock);

    /// <summary>The state machine wired over one context, acting as the given staff member.</summary>
    internal TableStateService CreateService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, NullLogger<TableStateService>.Instance);

    internal FloorQuery CreateFloorQuery(YallaDbContext db, IClock clock) => new(db, clock);
}

/// <summary>Groups the integration tests so the database is created once, not per class.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

/// <summary>A clock the tests drive, so "reserved soon" is deterministic.</summary>
public sealed class TestClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>A stand-in actor, so permission paths can be exercised without authentication.</summary>
public sealed class TestActor(ActorType type, Guid? staffMemberId, StaffRole? role) : ICurrentActor
{
    public ActorType Type { get; } = type;

    public Guid? StaffMemberId { get; } = staffMemberId;

    public Guid? DinerUserId => null;

    public StaffRole? Role { get; } = role;

    public static TestActor Waiter(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.Waiter);

    public static TestActor Manager(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.Manager);

    public static TestActor Diner() => new(ActorType.Diner, null, null);
}
