namespace Yalla.Domain.Occupancy;

/// <summary>
/// "Keep my table" was refused, and which of three refusals it was.
/// </summary>
/// <remarks>
/// <para>
/// The app used to tell the three apart by guessing from a bare 409, and guessed wrong: a branch
/// that offers no extension answered exactly like one already spent, so a diner there was told they
/// had already let the venue know. Each refusal now has its own code on the wire.
/// </para>
/// <para>
/// Still a <see cref="DomainStateException"/>, so everything that handled the old refusal still
/// does; the API maps this one first, with its own code.
/// </para>
/// </remarks>
public sealed class HoldExtensionRefusedException(
    string code,
    Guid reservationId,
    DateTime startUtc,
    string message)
    : DomainStateException(message)
{
    /// <summary>
    /// The booking has not started, so nothing is held yet - or it is not a confirmed booking at
    /// all. Ask again once running late.
    /// </summary>
    public const string NotActive = "hold-not-active";

    /// <summary>The booking's one extension is already used. A waiter decides what happens next.</summary>
    public const string AlreadyExtended = "hold-already-extended";

    /// <summary>The branch offers no extensions at all. Nothing the diner did: speak to the venue.</summary>
    public const string NotOffered = "extensions-not-offered";

    /// <summary>Which refusal: one of the three constants above. Reaches the wire as the error code.</summary>
    public string Code { get; } = code;

    /// <summary>The booking.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>When it starts, so a refusal before then can say when to ask again.</summary>
    public DateTime StartUtc { get; } = startUtc;
}
