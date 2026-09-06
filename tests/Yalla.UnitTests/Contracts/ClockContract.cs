using Yalla.Application.Abstractions;
using Yalla.Infrastructure.Time;
using Yalla.UnitTests.Integration;

namespace Yalla.UnitTests.Contracts;

/// <summary>
/// What every <see cref="IClock"/> must produce, real or fake.
/// </summary>
/// <remarks>
/// <para>
/// A tiny surface with the same failure mode as the actor's: a fake that returns a
/// <see cref="DateTime"/> with the wrong <see cref="DateTimeKind"/> is accepted everywhere, and
/// then <c>Guard.NotLocalTime</c> - the one check standing between a local instant and a UTC
/// column - passes on <see cref="DateTimeKind.Unspecified"/> and would not have caught it.
/// </para>
/// <para>
/// The interface's own remarks say "implementations return UTC". That was a sentence in a comment
/// and nothing enforced it; this is the enforcement.
/// </para>
/// </remarks>
public abstract class ClockContract
{
    /// <summary>A clock reading approximately this instant.</summary>
    protected abstract IClock ClockAt(DateTime utcNow);

    /// <summary>
    /// <b>UTC, always.</b> Every stored instant in this system is UTC and the domain refuses a
    /// local one; a clock that hands out <see cref="DateTimeKind.Unspecified"/> slips straight past
    /// that guard.
    /// </summary>
    [Fact]
    public void The_instant_is_marked_utc()
    {
        var instant = new DateTime(2026, 9, 6, 18, 30, 0, DateTimeKind.Utc);

        Assert.Equal(DateTimeKind.Utc, ClockAt(instant).UtcNow.Kind);
    }

    /// <summary>
    /// A clock asked for a particular instant reports it, rather than something near it.
    /// </summary>
    /// <remarks>
    /// The real clock cannot be asked for an instant, so it overrides this. Every fake can, and a
    /// fake that quietly rounds or ignores the value makes "reserved at 18:00" mean nothing.
    /// </remarks>
    [Fact]
    public virtual void The_clock_reports_the_instant_it_was_given()
    {
        var instant = new DateTime(2026, 9, 6, 18, 30, 0, DateTimeKind.Utc);

        Assert.Equal(instant, ClockAt(instant).UtcNow);
    }

    /// <summary>
    /// Reading twice without advancing gives the same answer.
    /// </summary>
    /// <remarks>
    /// Overridden by the real clock, which genuinely moves. Every rule in this system that compares
    /// two reads - has grace run out, is this token expired - assumes a fake does not drift under
    /// it mid-test.
    /// </remarks>
    [Fact]
    public virtual void Two_reads_of_a_stopped_clock_agree()
    {
        var clock = ClockAt(new DateTime(2026, 9, 6, 18, 30, 0, DateTimeKind.Utc));

        Assert.Equal(clock.UtcNow, clock.UtcNow);
    }
}

/// <summary>The real clock. Cannot be set, and does move, so it overrides those two.</summary>
public sealed class SystemClockContractTests : ClockContract
{
    protected override IClock ClockAt(DateTime utcNow) => new SystemClock();

    /// <summary>Not applicable: the system clock reads the system, and cannot be told an instant.</summary>
    [Fact]
    public override void The_clock_reports_the_instant_it_was_given() =>
        Assert.InRange(
            new SystemClock().UtcNow,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(1));

    /// <summary>Not applicable: real time passes. What must hold is that it never goes backwards.</summary>
    [Fact]
    public override void Two_reads_of_a_stopped_clock_agree()
    {
        var clock = new SystemClock();
        var first = clock.UtcNow;

        Assert.True(clock.UtcNow >= first, "The clock went backwards.");
    }
}

/// <summary>
/// The clock most tests drive.
/// </summary>
/// <remarks>
/// It stored whatever <see cref="DateTime"/> it was handed, kind and all, so a test constructing
/// one from an unspecified-kind literal got an unspecified-kind clock - and every instant written
/// through it into a UTC column was unmarked. It now normalises, which is what the real one does.
/// </remarks>
public sealed class TestClockContractTests : ClockContract
{
    protected override IClock ClockAt(DateTime utcNow) => new TestClock(utcNow);

    /// <summary>The case the normalisation exists for, pinned separately so it reads as a rule.</summary>
    [Fact]
    public void An_unspecified_instant_is_normalised_to_utc()
    {
        var clock = new TestClock(new DateTime(2026, 9, 6, 18, 30, 0, DateTimeKind.Unspecified));

        Assert.Equal(DateTimeKind.Utc, clock.UtcNow.Kind);
        Assert.Equal(new DateTime(2026, 9, 6, 18, 30, 0, DateTimeKind.Utc), clock.UtcNow);
    }
}

/// <summary>The fake the scheduler tests drive, which also backs a <c>TimeProvider</c>.</summary>
public sealed class FakeClockContractTests : ClockContract
{
    protected override IClock ClockAt(DateTime utcNow) => new FakeClock(utcNow);
}
