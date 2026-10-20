namespace Yalla.Application.Abstractions;

/// <summary>
/// No one-time code can be delivered here: no SMS provider is configured outside Development.
/// </summary>
/// <remarks>
/// Refused out loud rather than accepted and dropped. A code that silently never arrives leaves a
/// guest waiting at a booking form for a message nobody sends; a refusal lets the app send them to
/// Telegram instead, which is how a phone is proved until a provider is wired up (decided by the
/// user on 2026-10-20). The answer is the same for every number, so it says nothing about who has
/// an account. <c>GET /api/auth/diner/phone-proof</c> lets a client know before it asks.
/// </remarks>
public sealed class PhoneCodesUnavailableException()
    : Exception("Codes by SMS are not available. Prove the number with Telegram instead.");
