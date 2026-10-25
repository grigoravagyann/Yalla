using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using Xunit.Sdk;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The test databases of one run: migrated once, cloned for every SQL-backed class running at the same time, and
/// dropped when the run ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>One database per running class, never two classes in one database at once.</b> Every SQL-backed class gets
/// its own <see cref="SqlServerFixture"/> (see <see cref="SqlServerClassCollectionFactory"/>), and the fixture
/// leases a database here for as long as its class runs. A database is only ever leased to one class at a time,
/// so everything the suite was written against still holds inside a class: nothing else writes to its database
/// while it runs - which is what <c>ClearOutboxAsync</c> and the before/after counts of the platform tests rely
/// on. A finished class hands its database back and the next class reuses it, exactly as every class used to
/// inherit the one shared database's leftovers; no test may assume an empty database, and none does.
/// </para>
/// <para>
/// <b>Migrated once, then copied.</b> The first database is built with <c>Migrate</c>, so the migrations
/// themselves are still exercised on every run. It is backed up before any test touches it, and every further
/// database is a <c>RESTORE</c> of that backup under a new name - well under a second, against seconds of
/// migrations. SQL Server has no <c>CREATE DATABASE ... AS COPY OF</c> outside Azure, a snapshot is read-only,
/// and <c>DBCC CLONEDATABASE</c> copies no data (so not the migrations' seed rows or their history), which
/// leaves backup and restore as the one copy that is fast, complete, and works the same on SQL Server Express
/// on Windows and on the Linux container CI runs. If the backup cannot be taken (no permission, a read-only
/// backup folder) every further database is migrated instead: slower, never wrong.
/// </para>
/// <para>
/// <b>Cleaned up twice.</b> <see cref="YallaTestFramework"/> drops every database this run made, and the
/// backup, once the last class has finished. A run that crashed or was killed cannot, so each run also drops
/// what earlier runs left: databases named like a test database (<see cref="IsTestDatabaseName"/>), older than
/// <see cref="StaleAfter"/>, that nobody is connected to. The developer's own <c>Yalla</c> database - or
/// anything else not named <c>Yalla_&lt;Word&gt;_&lt;32 hex digits&gt;</c> - is never dropped.
/// </para>
/// </remarks>
internal static partial class TestDatabasePool
{
    /// <summary>
    /// Set to <c>1</c> (or <c>true</c>) to put every SQL-backed class back in one collection - one fixture, one
    /// database, one class at a time - which is how the suite ran before it ran them in parallel. For debugging
    /// a test that only fails alongside others. The plain unit tests stay parallel either way, as they always were.
    /// </summary>
    public const string SerialEnvironmentVariable = "YALLA_TEST_SERIAL";

    /// <summary>
    /// Set to <c>migrate</c> to build every test database with migrations instead of restoring the first one's
    /// backup. Slower; for proving the backup is not hiding a difference.
    /// </summary>
    public const string CloneEnvironmentVariable = "YALLA_TEST_SQL_CLONE";

    /// <summary>How old a leftover test database has to be before a run drops it.</summary>
    /// <remarks>Far longer than any run takes, so a run in another worktree is never pulled out from under it.</remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    /// <summary>Restores at once. More than a few only queue on the disk, and the first classes wait for all of them.</summary>
    private const int ConcurrentCreates = 4;

    private const string Prefix = "Yalla_Tests_";

    /// <summary>
    /// Where a developer machine actually keeps SQL Server, in the order worth trying.
    /// </summary>
    /// <remarks>
    /// The fixture used to hard-code <c>localhost</c>, which is the default instance. A machine with SQL Server
    /// Express installed - the common case on Windows - has a <i>named</i> instance and no default one, so every
    /// integration test skipped and the suite reported green while proving nothing about the two things only a
    /// real server can prove: rowversion tokens and the locking these bookings depend on.
    /// </remarks>
    private static readonly string[] CandidateServers =
    [
        "localhost",
        @"localhost\SQLEXPRESS",
        @"(localdb)\MSSQLLocalDB",
    ];

    private static readonly string RunId = Guid.NewGuid().ToString("N");

    private static Lazy<Task<Setup>> _setup = NewSetup();

    private static readonly SemaphoreSlim CreateGate = new(ConcurrentCreates, ConcurrentCreates);

    private static readonly object Gate = new();

    private static readonly Stack<string> Free = new();

    private static readonly List<string> Created = [];

    private static readonly Stopwatch RunClock = Stopwatch.StartNew();

    private static int _nextNumber;

    private static int _inUse;

    private static int _peakInUse;

    private static int _leases;

    private static Task? _staleSweep;

    private static string? _server;

    private static string? _backupFile;

    /// <summary>Where the pool reports what it did. Set by <see cref="YallaTestFramework"/>.</summary>
    public static IMessageSink? Diagnostics { get; set; }

    /// <summary>Whether <see cref="SerialEnvironmentVariable"/> asks for the old one-collection layout.</summary>
    public static bool IsSerial =>
        Environment.GetEnvironmentVariable(SerialEnvironmentVariable)?.Trim().ToLowerInvariant() is "1" or "true" or "yes";

    /// <summary>
    /// Whether <paramref name="name"/> is a database a test made: <c>Yalla_&lt;Word&gt;_&lt;32 hex digits&gt;</c>,
    /// optionally followed by <c>_&lt;number&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The fixture's databases (<c>Yalla_Tests_…</c>, old and new) and the ones the migration tests make for
    /// themselves (<c>Yalla_Rollback_…</c>, <c>Yalla_DevSeed_…</c>) all carry a <c>Guid</c> in <c>N</c> format.
    /// Nothing a person names - <c>Yalla</c>, <c>YallaPhone</c>, <c>Yalla_Staging</c> - looks like that, and
    /// nothing is dropped that does not.
    /// </remarks>
    internal static bool IsTestDatabaseName(string name) => TestDatabaseName().IsMatch(name);

    [GeneratedRegex("^Yalla_[A-Za-z]+_[0-9a-f]{32}(_[0-9]{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex TestDatabaseName();

    [GeneratedRegex("^Yalla_Tests_[0-9a-f]{32}\\.bak$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TestBackupName();

    /// <summary>
    /// A database for one class to use alone until it calls <see cref="Return"/>; or, when no server is
    /// reachable, the reason the class's tests skip.
    /// </summary>
    public static async Task<Lease> LeaseAsync()
    {
        var setup = await Volatile.Read(ref _setup).Value;

        if (setup.SkipReason is { } reason)
        {
            return new Lease(null, null, reason);
        }

        string? name = null;

        lock (Gate)
        {
            if (Free.Count > 0)
            {
                name = Free.Pop();
            }

            _leases++;
            _inUse++;
            _peakInUse = Math.Max(_peakInUse, _inUse);
        }

        if (name is null)
        {
            try
            {
                name = await CreateCopyAsync(setup);
            }
            catch
            {
                lock (Gate)
                {
                    _inUse--;
                }

                throw;
            }
        }

        return new Lease(name, ConnectionString(setup.Server, name), null);
    }

    /// <summary>Hands a leased database back for the next class.</summary>
    public static void Return(Lease lease)
    {
        if (lease.Database is not { } name)
        {
            return;
        }

        lock (Gate)
        {
            Free.Push(name);
            _inUse--;
        }
    }

    /// <summary>
    /// Drops every database this run made, and the backup they were restored from. Called once, after the last
    /// class has finished.
    /// </summary>
    /// <remarks>
    /// Failures are reported, not thrown: a database left behind is tidied by the next run's stale sweep, and is
    /// no reason to turn a green run red.
    /// </remarks>
    public static async Task DropAllAsync()
    {
        try
        {
            await DropEverythingAsync();
        }
        finally
        {
            // A runner that keeps its test host alive between runs (Test Explorer can) starts the next run in
            // this same process: it must find an empty pool and set up afresh, not lease databases just dropped.
            lock (Gate)
            {
                Free.Clear();
                Created.Clear();
                _leases = _inUse = _peakInUse = 0;
                _server = null;
                _backupFile = null;
                _staleSweep = null;
                Volatile.Write(ref _setup, NewSetup());
            }
        }
    }

    private static Lazy<Task<Setup>> NewSetup() => new(SetUpAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static async Task DropEverythingAsync()
    {
        if (_server is not { } server)
        {
            return;
        }

        if (_staleSweep is { } sweep)
        {
            await sweep;
        }

        string[] names;

        lock (Gate)
        {
            names = [.. Created];
        }

        // A lingering pooled connection makes DROP DATABASE wait for it.
        SqlConnection.ClearAllPools();

        // A few at a time: one by one, each waiting on its own checkpoint, twenty took twenty seconds.
        using var gate = new SemaphoreSlim(ConcurrentCreates);
        var dropped = 0;

        await Task.WhenAll(names.Select(async name =>
        {
            await gate.WaitAsync();

            try
            {
                await DropAsync(server, name, rollbackOthers: true);
                Interlocked.Increment(ref dropped);
            }
            catch (SqlException ex)
            {
                Report($"Could not drop test database {name}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }));

        if (_backupFile is { } backup)
        {
            await DeleteServerFileAsync(server, backup);
        }

        Report(
            $"{_leases} SQL-backed classes ran on {names.Length} databases (at most {_peakInUse} at once, "
            + $"{(_backupFile is null ? "each migrated" : "copied by restore")}); dropped {dropped} "
            + $"after {RunClock.Elapsed.TotalSeconds:F0}s.");
    }

    private static async Task<Setup> SetUpAsync()
    {
        var attempts = new List<string>();
        string? server = null;

        foreach (var candidate in Candidates())
        {
            try
            {
                await using var connection = new SqlConnection(MasterConnectionString(candidate));
                await connection.OpenAsync();

                server = candidate;

                break;
            }
            catch (Exception ex)
                when (ex is SqlException or InvalidOperationException or PlatformNotSupportedException)
            {
                attempts.Add($"{candidate}: {ex.Message.Split('\n')[0]}");
            }
        }

        if (server is null)
        {
            return new Setup(
                string.Empty,
                null,
                null,
                [],
                $"No SQL Server reachable. Set {SqlServerFixture.ServerEnvironmentVariable} to one. Tried - "
                + string.Join(" | ", attempts));
        }

        _server = server;

        // Alongside the migrations, not before them: it is housekeeping, and nothing here depends on it.
        _staleSweep = Task.Run(() => DropStaleAsync(server));

        var watch = Stopwatch.StartNew();
        var first = NextName();

        // Tracked before it exists, so a migration that fails half way still leaves nothing behind.
        Track(first);
        await MigrateAsync(server, first);

        var migrated = watch.Elapsed;
        var files = await FilesOfAsync(server, first);
        string? backup = null;

        if (!string.Equals(
                Environment.GetEnvironmentVariable(CloneEnvironmentVariable)?.Trim(), "migrate",
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                backup = await BackUpAsync(server, first, files);
                _backupFile = backup;
            }
            catch (SqlException ex)
            {
                Report($"Could not back up {first}, so every test database will be migrated: {ex.Message}");
            }
        }

        lock (Gate)
        {
            Free.Push(first);
        }

        Report(
            $"Test databases on {server}: {first} migrated in {migrated.TotalSeconds:F1}s"
            + (backup is null ? "." : $", backed up in {(watch.Elapsed - migrated).TotalSeconds:F1}s to {backup}.")
            + (IsSerial ? $" {SerialEnvironmentVariable} is set: SQL-backed classes run one at a time." : string.Empty));

        return new Setup(server, backup, DirectoryOf(files[0].PhysicalName), files, null);
    }

    private static async Task<string> CreateCopyAsync(Setup setup)
    {
        var name = NextName();

        Track(name);
        await CreateGate.WaitAsync();

        try
        {
            if (setup.BackupFile is { } backup)
            {
                await RestoreAsync(setup, backup, name);
            }
            else
            {
                await MigrateAsync(setup.Server, name);
            }
        }
        finally
        {
            CreateGate.Release();
        }

        return name;
    }

    private static async Task MigrateAsync(string server, string name)
    {
        await using var db = new YallaDbContext(
            new DbContextOptionsBuilder<YallaDbContext>()
                .UseSqlServer(ConnectionString(server, name), sql => sql.CommandTimeout(300))
                .Options,
            new TestClock(TestNow.UtcNow));

        await db.Database.MigrateAsync();
    }

    private static async Task<string> BackUpAsync(string server, string name, IReadOnlyList<DatabaseFile> files)
    {
        await using var connection = new SqlConnection(MasterConnectionString(server));
        await connection.OpenAsync();

        string? folder;

        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
            folder = await query.ExecuteScalarAsync() as string;
        }

        // LocalDB and some containers report no backup folder; the data folder is always the server's to write.
        var file = Combine(string.IsNullOrWhiteSpace(folder) ? DirectoryOf(files[0].PhysicalName) : folder, $"{Prefix}{RunId}.bak");

        await using var backup = connection.CreateCommand();
        backup.CommandTimeout = 300;
        backup.CommandText = $"BACKUP DATABASE [{name}] TO DISK = @file WITH COPY_ONLY, INIT, FORMAT";
        backup.Parameters.AddWithValue("@file", file);
        await backup.ExecuteNonQueryAsync();

        return file;
    }

    private static async Task RestoreAsync(Setup setup, string backup, string name)
    {
        await using var connection = new SqlConnection(MasterConnectionString(setup.Server));
        await connection.OpenAsync();

        await using var restore = connection.CreateCommand();
        restore.CommandTimeout = 300;

        var moves = new List<string>();

        foreach (var (file, index) in setup.Files.Select((f, i) => (f, i)))
        {
            var extension = file.Type == 1 ? "ldf" : index == 0 ? "mdf" : "ndf";

            moves.Add($"MOVE @logical{index} TO @physical{index}");
            restore.Parameters.AddWithValue($"@logical{index}", file.LogicalName);
            restore.Parameters.AddWithValue($"@physical{index}", Combine(setup.DataFolder!, $"{name}_{file.FileId}.{extension}"));
        }

        restore.CommandText = $"RESTORE DATABASE [{name}] FROM DISK = @backup WITH {string.Join(", ", moves)}, RECOVERY";
        restore.Parameters.AddWithValue("@backup", backup);

        await restore.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<DatabaseFile>> FilesOfAsync(string server, string name)
    {
        await using var connection = new SqlConnection(MasterConnectionString(server));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, type, file_id, physical_name FROM sys.master_files WHERE database_id = DB_ID(@name) ORDER BY type, file_id";
        command.Parameters.AddWithValue("@name", name);

        var files = new List<DatabaseFile>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            files.Add(new DatabaseFile(reader.GetString(0), reader.GetByte(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return files.Count > 0
            ? files
            : throw new InvalidOperationException($"{name} was migrated but sys.master_files lists no file for it.");
    }

    /// <summary>
    /// Drops what earlier runs left behind: test-named databases older than <see cref="StaleAfter"/> that
    /// nobody is connected to, and this pool's backups as old.
    /// </summary>
    private static async Task DropStaleAsync(string server)
    {
        try
        {
            await using var connection = new SqlConnection(MasterConnectionString(server));
            await connection.OpenAsync();

            var stale = new List<string>();

            await using (var query = connection.CreateCommand())
            {
                query.CommandText =
                    """
                    SELECT d.name
                    FROM sys.databases AS d
                    WHERE d.name LIKE N'Yalla[_]%'
                      AND d.create_date < DATEADD(minute, -@minutes, GETDATE())
                      AND NOT EXISTS (SELECT 1 FROM sys.dm_exec_sessions AS s WHERE s.database_id = d.database_id)
                    """;
                query.Parameters.AddWithValue("@minutes", (int)StaleAfter.TotalMinutes);

                await using var reader = await query.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    stale.Add(reader.GetString(0));
                }
            }

            var dropped = new List<string>();

            foreach (var name in stale.Where(IsTestDatabaseName))
            {
                try
                {
                    // No ROLLBACK IMMEDIATE: nobody was connected a moment ago, and if somebody is now, the drop
                    // failing is the right outcome.
                    await DropAsync(server, name, rollbackOthers: false);
                    dropped.Add(name);
                }
                catch (SqlException ex)
                {
                    Report($"Left stale test database {name}: {ex.Message}");
                }
            }

            if (dropped.Count > 0)
            {
                Report($"Dropped {dropped.Count} test database(s) older than {StaleAfter.TotalHours:F0}h: {string.Join(", ", dropped)}.");
            }

            await DeleteStaleBackupsAsync(server, connection);
        }
        catch (SqlException ex)
        {
            Report($"The stale test database sweep failed: {ex.Message}");
        }
    }

    private static async Task DeleteStaleBackupsAsync(string server, SqlConnection connection)
    {
        var files = new List<string>();

        try
        {
            await using var query = connection.CreateCommand();
            query.CommandText =
                """
                DECLARE @folder nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000));
                IF @folder IS NOT NULL
                    SELECT full_filesystem_path, file_or_directory_name
                    FROM sys.dm_os_enumerate_filesystem(@folder, N'Yalla_Tests_*.bak')
                    WHERE is_directory = 0 AND last_write_time < DATEADD(minute, -@minutes, GETDATE());
                """;
            query.Parameters.AddWithValue("@minutes", (int)StaleAfter.TotalMinutes);

            await using var reader = await query.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                if (TestBackupName().IsMatch(reader.GetString(1)))
                {
                    files.Add(reader.GetString(0));
                }
            }
        }
        catch (SqlException)
        {
            // sys.dm_os_enumerate_filesystem is SQL Server 2017 and later; an older server keeps its old backups.
            return;
        }

        foreach (var file in files)
        {
            await DeleteServerFileAsync(server, file);
        }
    }

    private static async Task DropAsync(string server, string name, bool rollbackOthers)
    {
        if (!IsTestDatabaseName(name))
        {
            throw new InvalidOperationException($"Refusing to drop '{name}': it is not named like a test database.");
        }

        await using var connection = new SqlConnection(MasterConnectionString(server));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = rollbackOthers
            ? $"""
               IF DB_ID(N'{name}') IS NOT NULL
               BEGIN
                   ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                   DROP DATABASE [{name}];
               END
               """
            : $"IF DB_ID(N'{name}') IS NOT NULL DROP DATABASE [{name}];";

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Deletes a file on the server's disk - which is not necessarily this machine's.</summary>
    private static async Task DeleteServerFileAsync(string server, string path)
    {
        try
        {
            await using var connection = new SqlConnection(MasterConnectionString(server));
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();

            // xp_delete_files is SQL Server 2019 and later; xp_delete_file (backup files only) is older.
            command.CommandText =
                """
                IF OBJECT_ID(N'master.sys.xp_delete_files') IS NOT NULL
                    EXEC master.sys.xp_delete_files @path;
                ELSE
                    EXEC master.dbo.xp_delete_file 0, @path;
                """;
            command.Parameters.AddWithValue("@path", path);

            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            Report($"Could not delete {path} on the server: {ex.Message}");
        }
    }

    private static string NextName() => $"{Prefix}{RunId}_{Interlocked.Increment(ref _nextNumber):00}";

    private static void Track(string name)
    {
        lock (Gate)
        {
            Created.Add(name);
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFixture.ServerEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        foreach (var server in CandidateServers)
        {
            yield return server;
        }
    }

    private static string ConnectionString(string server, string database) =>
        $"Server={server};Database={database};{Credentials()}TrustServerCertificate=True;MultipleActiveResultSets=True";

    private static string MasterConnectionString(string server) =>
        $"Server={server};Database=master;{Credentials()}TrustServerCertificate=True;Connect Timeout=5";

    /// <summary>
    /// How to authenticate: a SQL login when CI supplies one, Windows authentication otherwise.
    /// </summary>
    /// <remarks>
    /// A developer machine uses <c>Trusted_Connection</c> and needs no credentials at all, which is why that
    /// stays the default. CI runs SQL Server in a Linux container that has no notion of a Windows identity, so it
    /// sets <see cref="SqlServerFixture.UserEnvironmentVariable"/> and the password - and without them the pool
    /// would find no server, <b>skip</b> every integration test, and report green while proving nothing. Returns
    /// a fragment ending in a semicolon so the call sites can concatenate it.
    /// </remarks>
    private static string Credentials()
    {
        var user = Environment.GetEnvironmentVariable(SqlServerFixture.UserEnvironmentVariable);
        var password = Environment.GetEnvironmentVariable(SqlServerFixture.PasswordEnvironmentVariable);

        return string.IsNullOrWhiteSpace(user)
            ? "Trusted_Connection=True;"
            : $"User ID={user};Password={password};";
    }

    /// <summary>The folder part of a path on the server, which is Windows or Linux whatever this machine is.</summary>
    private static string DirectoryOf(string path) => path[..(path.LastIndexOfAny(['\\', '/']) + 1)];

    private static string Combine(string folder, string file)
    {
        var separator = folder.Contains('/') && !folder.Contains('\\') ? '/' : '\\';

        return folder.TrimEnd('\\', '/') + separator + file;
    }

    private static void Report(string message) => Diagnostics?.OnMessage(new DiagnosticMessage("[test databases] " + message));

    /// <summary>A database leased to one class, or why there is none.</summary>
    internal sealed record Lease(string? Database, string? ConnectionString, string? SkipReason);

    private sealed record DatabaseFile(string LogicalName, byte Type, int FileId, string PhysicalName);

    private sealed record Setup(
        string Server,
        string? BackupFile,
        string? DataFolder,
        IReadOnlyList<DatabaseFile> Files,
        string? SkipReason);
}
