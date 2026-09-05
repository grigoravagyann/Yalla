using Yalla.Application.Abstractions;

namespace Yalla.Infrastructure.Time;

/// <summary>
/// The real clock, reading UTC through <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only place in the system that asks what time it is.</b> Everything else takes
/// <see cref="IClock"/> by injection - ninety-six call sites read <c>clock.UtcNow</c> and not one
/// reads <c>DateTime.UtcNow</c>. That was true before Prompt 9 and is what made the scheduler
/// testable without a sweep.
/// </para>
/// <para>
/// It delegates to <see cref="TimeProvider"/> rather than calling <c>DateTime.UtcNow</c> directly, so
/// there is exactly one clock in the process: the outbox loop's <c>PeriodicTimer</c> is built from
/// the same provider, and a test that advances a fake one moves both the timer and every rule that
/// asks the date. Two independent clocks is how a scheduler test passes while the thing it schedules
/// never fires.
/// </para>
/// <para>
/// <see cref="IClock"/> is kept in front of it deliberately. It returns the <see cref="DateTime"/>
/// the domain, the database columns and the EF converters all use, so nothing has to translate a
/// <see cref="DateTimeOffset"/> at ninety-six call sites to gain an abstraction that is already
/// there.
/// </para>
/// </remarks>
public sealed class SystemClock(TimeProvider time) : IClock
{
    /// <summary>For the composition roots that have no provider to hand. Uses the system one.</summary>
    public SystemClock()
        : this(TimeProvider.System)
    {
    }

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
}
