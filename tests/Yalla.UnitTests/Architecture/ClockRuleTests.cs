using System.Reflection;

namespace Yalla.UnitTests.Architecture;

/// <summary>
/// "Time comes from <c>IClock</c>, never <c>DateTime.UtcNow</c>" (<c>AGENTS.md</c>, golden rules): a wall-clock read in
/// production code is a test that passes at 15:00 and fails at 05:30. Read from the IL, so a comment or a string that
/// says <c>DateTime.UtcNow</c> is fine and a call is not.
/// </summary>
public sealed class ClockRuleTests
{
    /// <summary>
    /// Wall-clock reads production code may make, and why. Empty: the one real clock, <c>SystemClock</c>, reads the
    /// injected <see cref="TimeProvider"/>, which is not a wall-clock read this test looks for.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new();

    [Fact]
    public void No_production_code_reads_the_wall_clock()
    {
        var offenders = ProductionCode.AllTypes()
            .SelectMany(MethodCalls.DeclaredBy)
            .SelectMany(m => MethodCalls.CalledBy(m).Where(IsWallClock).Select(called => (m, called)))
            .Select(c => (Where: ProductionCode.NameOf(c.m), What: ProductionCode.NameOf(c.called)))
            .Where(c => !Allowed.ContainsKey(c.Where))
            .Select(c => $"{c.Where} calls {c.What}")
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, ProductionCode.Violation(
            "Production code reads the time from IClock (or, for a timer, the injected TimeProvider) - never "
            + "DateTime.UtcNow, DateTime.Now, DateTime.Today, DateTimeOffset.UtcNow or DateTimeOffset.Now - so tests pin it "
            + "and YALLA_TEST_NOW moves it.",
            offenders,
            "inject IClock (Application/Abstractions/IClock.cs) and read clock.UtcNow; pass the instant into the domain. "
            + "See AGENTS.md, golden rules, \"Time comes from IClock\"."));
    }

    private static bool IsWallClock(MethodBase called) =>
        (called.DeclaringType == typeof(DateTime) && called.Name is "get_UtcNow" or "get_Now" or "get_Today")
        || (called.DeclaringType == typeof(DateTimeOffset) && called.Name is "get_UtcNow" or "get_Now");
}
