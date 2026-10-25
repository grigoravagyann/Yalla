using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;
using Xunit.Sdk;
using Yalla.UnitTests.Integration;

// Each SQL-backed class in a collection of its own (so they run in parallel), and the run's databases dropped at
// the end. See SqlServerClassCollectionFactory and YallaTestFramework below.
[assembly: CollectionBehavior(
    "Yalla.UnitTests.Integration.SqlServerClassCollectionFactory",
    "Yalla.UnitTests")]
[assembly: TestFramework("Yalla.UnitTests.Integration.YallaTestFramework", "Yalla.UnitTests")]
[assembly: TestCollectionOrderer("Yalla.UnitTests.Integration.LongestFirstCollectionOrderer", "Yalla.UnitTests")]

namespace Yalla.UnitTests.Integration;

/// <summary>
/// xUnit's collection-per-class, except that a class marked <c>[Collection(SqlServerCollection.Name)]</c> gets a
/// collection of its own rather than sharing one with every other such class.
/// </summary>
/// <remarks>
/// <para>
/// xUnit runs collections in parallel and the classes inside one collection one after another. All 140-odd
/// SQL-backed classes used to share the one <c>SqlServer</c> collection, so the integration suite took as long as
/// all of them end to end. Here each keeps <see cref="SqlServerCollection"/> as its definition - so each gets its
/// own <see cref="SqlServerFixture"/>, and with it a database leased from <see cref="TestDatabasePool"/> - under a
/// name of its own, and so runs beside the others, up to <c>maxParallelThreads</c> (xunit.runner.json) at once.
/// </para>
/// <para>
/// The test classes did not change: they are marked exactly as before, a new one is marked the same way, and the
/// unit of a CI shard (a class) is still the unit of a fixture. <see cref="TestDatabasePool.SerialEnvironmentVariable"/>
/// turns this off and puts them all back in the one collection.
/// </para>
/// </remarks>
public sealed class SqlServerClassCollectionFactory : IXunitTestCollectionFactory
{
    private readonly ITestAssembly _assembly;

    private readonly CollectionPerClassTestCollectionFactory _default;

    private readonly ITypeInfo _definition = Reflector.Wrap(typeof(SqlServerCollection));

    private readonly ConcurrentDictionary<string, ITestCollection> _perClass = new(StringComparer.Ordinal);

    private readonly bool _serial = TestDatabasePool.IsSerial;

    public SqlServerClassCollectionFactory(ITestAssembly testAssembly, IMessageSink diagnosticMessageSink)
    {
        _assembly = testAssembly;
        _default = new CollectionPerClassTestCollectionFactory(testAssembly, diagnosticMessageSink);
    }

    public string DisplayName => _serial
        ? $"collection-per-class, SQL-backed classes in one collection ({TestDatabasePool.SerialEnvironmentVariable})"
        : "collection-per-class, a database per SQL-backed class";

    public ITestCollection Get(ITypeInfo testClass)
    {
        if (_serial || !IsSqlBacked(testClass))
        {
            return _default.Get(testClass);
        }

        return _perClass.GetOrAdd(testClass.Name, name =>
        {
            var displayName = $"{SqlServerCollection.Name}: {name}";

            // Stable across discovery and execution, as the default factories' ids are, so a runner that discovers
            // and runs in separate passes (Test Explorer) matches the two up.
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(_assembly.Assembly.Name + "|" + displayName)).AsSpan(0, 16));

            return new TestCollection(_assembly, _definition, displayName, id);
        });
    }

    private static bool IsSqlBacked(ITypeInfo testClass) =>
        testClass.GetCustomAttributes(typeof(CollectionAttribute))
            .Select(attribute => attribute.GetConstructorArguments().FirstOrDefault() as string)
            .Any(name => name == SqlServerCollection.Name);
}

/// <summary>
/// Starts the classes that take longest first, so the run does not end waiting on one that started last.
/// </summary>
/// <remarks>
/// <para>
/// With the classes running side by side, a run lasts at least as long as its slowest class, and longer when that
/// class happens to start late - AuthorizationMatrixTests alone is a couple of minutes. Longest first is the
/// classic fix. The durations are the ones CI already keeps for balancing its shards
/// (<c>scripts/ci/test-timings.json</c>). A SQL-backed class it has not timed yet is estimated the way the shard
/// planner estimates one - a second and a half per test method - so a new, large class is not left to last. Order only - nothing a test can observe depends on it, since each class has
/// its own database and no test may assume what ran before it.
/// </para>
/// </remarks>
public sealed class LongestFirstCollectionOrderer : ITestCollectionOrderer
{
    private const string PerClassPrefix = SqlServerCollection.Name + ": ";

    private const string DefaultPrefix = "Test collection for ";

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        var seconds = RecordedSeconds();

        return testCollections
            .OrderByDescending(collection => Estimate(collection, seconds))
            .ThenBy(collection => collection.DisplayName, StringComparer.Ordinal);
    }

    private static double Estimate(ITestCollection collection, Dictionary<string, double> recorded)
    {
        var name = ClassOf(collection);

        if (recorded.TryGetValue(name, out var seconds))
        {
            return seconds;
        }

        if (!collection.DisplayName.StartsWith(PerClassPrefix, StringComparison.Ordinal)
            || typeof(LongestFirstCollectionOrderer).Assembly.GetType(name) is not { } type)
        {
            return 0;
        }

        // DEFAULT_HEAVY_SECONDS_PER_TEST in scripts/ci/test_shards.py.
        return 1.5 * type.GetMethods().Count(method => method.IsDefined(typeof(FactAttribute), inherit: true));
    }

    private static string ClassOf(ITestCollection collection) =>
        collection.DisplayName.StartsWith(PerClassPrefix, StringComparison.Ordinal) ? collection.DisplayName[PerClassPrefix.Length..]
        : collection.DisplayName.StartsWith(DefaultPrefix, StringComparison.Ordinal) ? collection.DisplayName[DefaultPrefix.Length..]
        : collection.DisplayName;

    private static Dictionary<string, double> RecordedSeconds()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yalla.sln")))
        {
            directory = directory.Parent;
        }

        var file = directory is null ? null : Path.Combine(directory.FullName, "scripts", "ci", "test-timings.json");
        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);

        if (file is null || !File.Exists(file))
        {
            return seconds;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));

            foreach (var entry in document.RootElement.GetProperty("classes").EnumerateObject())
            {
                seconds[entry.Name] = entry.Value.GetProperty("seconds").GetDouble();
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            // An unreadable timings file costs ordering, never a run.
            seconds.Clear();
        }

        return seconds;
    }
}

/// <summary>
/// xUnit's own framework, plus the one thing xUnit 2 has no hook for: work after the last test class.
/// </summary>
/// <remarks>
/// <see cref="TestDatabasePool"/> keeps its databases for the whole run so classes can reuse them, so the only
/// moment to drop them is when every class has finished - <see cref="XunitTestAssemblyRunner"/>'s
/// <c>BeforeTestAssemblyFinishedAsync</c>. Everything else is the stock framework.
/// </remarks>
public sealed class YallaTestFramework : XunitTestFramework
{
    public YallaTestFramework(IMessageSink messageSink)
        : base(messageSink)
    {
        TestDatabasePool.Diagnostics = messageSink;
    }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor(
        AssemblyName assemblyName,
        ISourceInformationProvider sourceInformationProvider,
        IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        // async void because the base is: the runner reports completion through the message sink, not a Task.
        protected override async void RunTestCases(
            IEnumerable<IXunitTestCase> testCases,
            IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new AssemblyRunner(
                TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);

            await runner.RunAsync();
        }
    }

    private sealed class AssemblyRunner(
        ITestAssembly testAssembly,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        protected override async Task BeforeTestAssemblyFinishedAsync()
        {
            await TestDatabasePool.DropAllAsync();
            await base.BeforeTestAssemblyFinishedAsync();
        }
    }
}
