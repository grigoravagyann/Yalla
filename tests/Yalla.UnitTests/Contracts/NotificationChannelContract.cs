using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Notifications;
using Yalla.UnitTests.Integration;

namespace Yalla.UnitTests.Contracts;

/// <summary>
/// What every <see cref="INotificationChannel"/> must do, shipped or fake.
/// </summary>
/// <remarks>
/// <para>
/// The surface is one method, and the failure mode is still the actor's: a double that returns
/// something the real channels cannot lets a scheduler test assert on a world that does not exist.
/// The specific trap here is the empty result. An empty list means "this diner has no live
/// device" - not a failure, and explicitly not something to retry, because they never installed
/// the app or they uninstalled it. A double that threw, or returned a fabricated delivery, would
/// send the dispatcher down a path production never takes.
/// </para>
/// <para>
/// A cancelled token is the other one. The dispatcher runs in a hosted service that is stopped on
/// shutdown, so every channel has to end quickly rather than finish sending on the way out.
/// </para>
/// </remarks>
public abstract class NotificationChannelContract
{
    protected abstract INotificationChannel Channel();

    private static NotificationMessage Message() => new(
        DinerUserId: Guid.CreateVersion7(),
        Title: "Your table at 19:00",
        Body: "See you soon.",
        Data: new Dictionary<string, string> { ["kind"] = "reservation.reminder" },
        CategoryId: "reservation-reminder");

    /// <summary>
    /// Every channel names itself, and the name is stable and not blank.
    /// </summary>
    /// <remarks>
    /// It goes into the log line and the platform view, which is how anyone answers "did this go
    /// out over Expo or into a file" three weeks later.
    /// </remarks>
    [SkippableFact]
    public void The_channel_names_itself()
    {
        var channel = Channel();

        Assert.False(string.IsNullOrWhiteSpace(channel.Name));
        Assert.Equal(channel.Name, channel.Name);
    }

    /// <summary>
    /// A diner with no live device is answered with an empty list, not an exception.
    /// </summary>
    /// <remarks>
    /// The contract's central rule. Somebody who booked from the web and never installed the app is
    /// the ordinary case, not an error, and a channel that throws for them turns a normal Tuesday
    /// into a dead-lettered outbox message.
    /// </remarks>
    [SkippableFact]
    public async Task A_diner_with_no_device_is_an_empty_result_and_not_a_failure()
    {
        var deliveries = await Channel().SendAsync(Message());

        Assert.NotNull(deliveries);
        Assert.Empty(deliveries);
    }

    /// <summary>Sending twice is allowed; the channel holds no per-message state that breaks.</summary>
    [SkippableFact]
    public async Task The_channel_can_be_used_more_than_once()
    {
        var channel = Channel();

        Assert.NotNull(await channel.SendAsync(Message()));
        Assert.NotNull(await channel.SendAsync(Message()));
    }

    /// <summary>
    /// A cancelled token ends the send rather than being ignored.
    /// </summary>
    /// <remarks>
    /// Either outcome is acceptable - an <see cref="OperationCanceledException"/>, or a result that
    /// arrives promptly - and neither is a hang. What is being ruled out is a channel that takes
    /// the token and does nothing with it, which is how a shutdown turns into a timeout.
    /// </remarks>
    [SkippableFact]
    public async Task A_cancelled_send_does_not_hang()
    {
        var channel = Channel();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Wrapped, because a channel is allowed to throw the cancellation synchronously rather than
        // returning a faulted task, and either is a fine answer to "you were cancelled". What is
        // being ruled out is the third one: taking the token and ignoring it.
        var send = Task.Run(() => channel.SendAsync(Message(), cancelled.Token));
        var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(send, finished);

        // Swallowed rather than asserted on: cancelling is allowed to throw and allowed to return.
        try
        {
            await send;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>
/// The channel that ships in every non-Expo configuration, over a real database.
/// </summary>
/// <remarks>
/// It reads the diner's live devices, so it needs one - which is also what makes it the honest
/// definition of the empty-result rule the doubles are measured against.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class LoggingNotificationChannelContractTests(SqlServerFixture fixture)
    : NotificationChannelContract, IDisposable
{
    private readonly List<IDisposable> contexts = [];

    protected override INotificationChannel Channel()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var db = fixture.CreateContext(clock);
        contexts.Add(db);

        return new LoggingNotificationChannel(db, clock, NullLogger<LoggingNotificationChannel>.Instance);
    }

    public void Dispose()
    {
        foreach (var context in contexts)
        {
            context.Dispose();
        }
    }
}

/// <summary>The double the scheduler tests read rendered messages out of.</summary>
public sealed class RecordingChannelContractTests : NotificationChannelContract
{
    protected override INotificationChannel Channel() => new RecordingChannel();
}

/// <summary>The double that only counts, used where "the handler ran" is the whole question.</summary>
public sealed class CountingChannelContractTests : NotificationChannelContract
{
    protected override INotificationChannel Channel() => new CountingChannel();
}
