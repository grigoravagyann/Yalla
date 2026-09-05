using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Audit;

/// <summary>
/// One caller-issued command that has already been applied, with the answer it was given.
/// </summary>
/// <remarks>
/// <para>
/// There were three separate idempotency mechanisms - a unique <c>ClientCommandId</c> on
/// <c>TableStateChange</c>, on <c>Reservation</c>, and on <c>Tab</c> - each catching its own
/// constraint violation, and not one of them able to return the <i>original response</i>. A
/// replayed <c>seat-walk-in</c> could tell you the command had been applied but not which
/// <c>TableSessionId</c> it had created, because the audit row does not store one. So the tablet
/// that queued the command offline still could not finish what it started.
/// </para>
/// <para>
/// This stores the answer. A replay is looked up here and served the recorded body and status code
/// verbatim - the same answer, not a recomputed one that might differ because the world moved on.
/// </para>
/// <para>
/// Written in the same <c>SaveChanges</c> as the command's effects, so the two cannot come apart:
/// there is no state in which the work happened and the record of it did not. The existing unique
/// indexes stay as a database-level backstop; they now catch a bug rather than being the mechanism.
/// </para>
/// </remarks>
public sealed class ProcessedCommand : Entity
{
    /// <summary>The caller's own id for this command. Unique across the table.</summary>
    public Guid ClientCommandId { get; private set; }

    /// <summary>What was asked for, e.g. <c>table.seat-walk-in</c>. Recorded so a replay of a different command with a reused id is visible.</summary>
    public string CommandType { get; private set; } = null!;

    public ActorType ActorType { get; private set; }

    /// <summary>Who issued it. Null only for a System command.</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>The response body as it was first sent, serialised.</summary>
    public string ResponseJson { get; private set; } = null!;

    /// <summary>The HTTP status that went with it, so a replay is answered identically.</summary>
    public int StatusCode { get; private set; }

    private ProcessedCommand()
    {
    }

    public ProcessedCommand(
        Guid clientCommandId,
        string commandType,
        ActorType actorType,
        Guid? actorId,
        string responseJson,
        int statusCode,
        DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        ClientCommandId = Guard.NotEmpty(clientCommandId, nameof(clientCommandId));
        CommandType = Guard.NotBlank(commandType, nameof(commandType), FieldLengths.CommandType);
        ActorType = Guard.Defined(actorType, nameof(actorType));
        ActorId = actorId;
        ResponseJson = Guard.NotBlank(responseJson, nameof(responseJson), int.MaxValue);
        StatusCode = statusCode;
        StampCreatedAt(atUtc);

        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode), statusCode, "That is not an HTTP status code.");
        }
    }
}
