using Yalla.Application.Notifications;

namespace Yalla.UnitTests.Integration;

/// <summary>Keeps what would have been pushed, so a test can read the rendered words.</summary>
/// <remarks>
/// Lifted out of <c>SchedulerTests</c> as a private nested class so it can be measured against
/// <c>NotificationChannelContract</c>. A double nobody can reach from outside the file that
/// declares it is a double nothing can hold to the real interface's rules, which is how the
/// <c>ICurrentActor</c> double went four prompts without anyone noticing it was lying.
/// </remarks>
public sealed class RecordingChannel : INotificationChannel
{
    private readonly List<NotificationMessage> messages = [];

    private readonly TaskCompletionSource next =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Name => "Recording";

    /// <summary>Completes when a message arrives, so a test never has to poll for one.</summary>
    public Task NextMessage => next.Task;

    public IReadOnlyList<NotificationMessage> Messages => messages;

    public IReadOnlyList<NotificationMessage> OfKind(string kind) =>
        [.. messages.Where(m => m.Data.TryGetValue("kind", out var k) && k == kind)];

    public void Clear() => messages.Clear();

    public Task<IReadOnlyList<NotificationDelivery>> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        messages.Add(message);
        next.TrySetResult();

        // Empty, like the real channels when the diner has no live device - which is the ordinary
        // case for somebody who booked from the web and never installed the app, and explicitly not
        // a failure to retry.
        return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
    }
}

/// <summary>Accepts everything and counts, so "the handler ran" is a fact and not an assumption.</summary>
public sealed class CountingChannel : INotificationChannel
{
    private int sent;

    public string Name => "Counting";

    public int Sent => Volatile.Read(ref sent);

    public Task<IReadOnlyList<NotificationDelivery>> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref sent);

        return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
    }
}
