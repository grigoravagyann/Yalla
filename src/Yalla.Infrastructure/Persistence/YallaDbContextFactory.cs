using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yalla.Application.Abstractions;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="YallaDbContext"/> for the EF Core command-line tools.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>dotnet ef</c> has to boot the API's host to find a context - which means the
/// tools need the API to build, need its configuration, and in CI need a connection string just to
/// answer a question about the <i>model</i>. <c>migrations has-pending-model-changes</c> is the
/// case that matters: it compares the model to the last snapshot and touches no database at all, so
/// making it depend on a running application was never right.
/// </para>
/// <para>
/// The connection string here is a placeholder and is never connected to. It only has to be
/// well-formed enough for the SQL Server provider to configure itself, because everything the tools
/// do with this context - scaffolding a migration, diffing the snapshot - is offline. Set
/// <c>YALLA_MIGRATIONS_CONNECTION</c> for the one case that is not: <c>database update</c>.
/// </para>
/// </remarks>
internal sealed class YallaDbContextFactory : IDesignTimeDbContextFactory<YallaDbContext>
{
    /// <summary>Names a real database for <c>dotnet ef database update</c>.</summary>
    public const string ConnectionEnvironmentVariable = "YALLA_MIGRATIONS_CONNECTION";

    private const string OfflinePlaceholder =
        "Server=(localdb)\\MSSQLLocalDB;Database=Yalla_DesignTime;Trusted_Connection=True;TrustServerCertificate=True";

    public YallaDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable) is { Length: > 0 } configured
                ? configured
                : OfflinePlaceholder;

        var options = new DbContextOptionsBuilder<YallaDbContext>()
            .UseSqlServer(
                connectionString,
                sql => sql.MigrationsAssembly(typeof(YallaDbContext).Assembly.GetName().Name))
            .Options;

        return new YallaDbContext(options, new DesignTimeClock());
    }

    /// <summary>
    /// The clock the tools get. Never read: nothing in a migration asks what time it is.
    /// </summary>
    /// <remarks>
    /// <see cref="YallaDbContext"/> takes an <see cref="IClock"/> because it stamps
    /// <c>UpdatedAtUtc</c> on save, and the tools never save. A fixed instant rather than
    /// <c>DateTime.UtcNow</c> so that nothing about a scaffolded migration can depend on when it
    /// was scaffolded.
    /// </remarks>
    private sealed class DesignTimeClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
