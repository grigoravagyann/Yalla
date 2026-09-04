using System.Text.Json.Serialization;

namespace Yalla.Api.Errors;

/// <summary>
/// The single failure shape every endpoint answers with, whatever went wrong.
/// </summary>
/// <remarks>
/// <para>
/// An RFC 7807 <c>application/problem+json</c> document with four extension members. Three
/// clients consume this API; if each status code carried its own ad-hoc body, all three would
/// grow their own guesswork for reading errors.
/// </para>
/// <para>
/// The standard members - <see cref="Type"/>, <see cref="Title"/>, <see cref="Status"/>,
/// <see cref="Detail"/>, <see cref="Instance"/> - are there so generic tooling understands the
/// response without knowing anything about Yalla. The extensions are what our own clients
/// actually branch on:
/// </para>
/// <list type="bullet">
/// <item><see cref="Code"/> - a stable kebab-case slug that survives message rewording and
/// translation, so a frontend never matches on English prose.</item>
/// <item><see cref="TraceId"/> - correlates the response with the one log entry written for it.</item>
/// <item><see cref="Errors"/> - field-level complaints.</item>
/// <item><see cref="Context"/> - machine-readable facts about this particular failure.</item>
/// </list>
/// </remarks>
public sealed record UnifiedErrorEnvelope
{
    /// <summary>
    /// A URI naming the problem type. Built from <see cref="Code"/>, so it is stable and
    /// dereferenceable to documentation rather than being <c>about:blank</c> for everything.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Short, stable summary of the <i>kind</i> of problem. Same wording for every occurrence -
    /// the specifics go in <see cref="Detail"/>.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>The HTTP status code, repeated in the body so a client logging only bodies keeps it.</summary>
    public required int Status { get; init; }

    /// <summary>
    /// What went wrong this time, in words. Safe to show a developer, not necessarily a diner.
    /// </summary>
    public required string Detail { get; init; }

    /// <summary>The request path this happened on.</summary>
    public string? Instance { get; init; }

    /// <summary>Stable kebab-case slug identifying the failure. This is what clients branch on.</summary>
    public required string Code { get; init; }

    /// <summary>Correlates this response with the single log entry written for it.</summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// Field-level complaints, keyed by field name, when the failure was about the payload.
    /// Omitted entirely when there are none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }

    /// <summary>
    /// Machine-readable facts about this particular failure, for the cases where the client has
    /// to act on more than the code.
    /// </summary>
    /// <remarks>
    /// The case this exists for is the lost table race: a 409 on seating carries the table's
    /// <c>currentStatus</c> here, so the tablet can redraw table 7 as occupied and tell the
    /// waiter what happened instead of showing a generic failure and leaving the floor stale.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Context { get; init; }
}
