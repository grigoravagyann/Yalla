using System.Text.Json.Serialization;

namespace Yalla.Api.Errors;

/// <summary>
/// The single failure shape every endpoint answers with, whatever went wrong.
/// </summary>
/// <remarks>
/// Three clients consume this API. If each status code carried its own ad-hoc body, all three
/// would grow their own guesswork for reading errors. One envelope means one parser per client
/// and one place to add a field.
/// <para>
/// <see cref="Code"/> is the part clients branch on. It is a stable kebab-case slug that survives
/// message rewording and translation, so a frontend never has to match on English prose.
/// </para>
/// </remarks>
public sealed record UnifiedErrorEnvelope
{
    /// <summary>Correlates this response with the single log entry written for it.</summary>
    public required string TraceId { get; init; }

    /// <summary>The HTTP status code, repeated in the body so a client logging only bodies keeps it.</summary>
    public required int Status { get; init; }

    /// <summary>Stable kebab-case slug identifying the failure. This is what clients branch on.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable description. Safe to show a developer, not necessarily a diner.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// Field-level complaints, keyed by field name, when the failure was about the payload.
    /// Omitted entirely when there are none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
