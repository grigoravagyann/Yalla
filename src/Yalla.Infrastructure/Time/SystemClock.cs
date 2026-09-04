using Yalla.Application.Abstractions;

namespace Yalla.Infrastructure.Time;

/// <summary>The real clock, reading UTC from the host.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
