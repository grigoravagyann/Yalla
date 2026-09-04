using System.Security.Cryptography;
using Yalla.Domain.Common;

namespace Yalla.Domain.Tabs;

/// <summary>
/// A short-lived invitation to join an existing tab, handed round the table as a link or code.
/// </summary>
/// <remarks>
/// Tokens expire after <see cref="Lifetime"/> so that a screenshot from last Tuesday cannot get
/// a stranger onto a live tab. The table's QR code is stable and opens a <i>new</i> tab; this
/// token is the one that joins an <i>open</i> one, which is why it must be ephemeral.
/// </remarks>
public sealed class TabJoinToken : Entity
{
    /// <summary>How long a freshly issued token stays usable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public Guid TabId { get; private set; }

    public Tab Tab { get; private set; } = null!;

    /// <summary>The opaque token itself. Unique across the system.</summary>
    public string Token { get; private set; } = null!;

    /// <summary>The participant who invited. Not a foreign key, for the reason given on <c>Tab.HostParticipantId</c>.</summary>
    public Guid CreatedByParticipantId { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Set when the token was withdrawn before it expired.</summary>
    public DateTime? RevokedAtUtc { get; private set; }

    private TabJoinToken()
    {
    }

    public TabJoinToken(Guid tabId, Guid createdByParticipantId, DateTime createdAtUtc, string? token = null)
        : base(Guid.CreateVersion7())
    {
        TabId = Guard.NotEmpty(tabId, nameof(tabId));
        CreatedByParticipantId = Guard.NotEmpty(createdByParticipantId, nameof(createdByParticipantId));
        StampCreatedAt(createdAtUtc);
        ExpiresAtUtc = createdAtUtc.Add(Lifetime);
        Token = token is null
            ? GenerateToken()
            : Guard.NotBlank(token, nameof(token), FieldLengths.JoinToken);
    }

    /// <summary>A fresh, unguessable, URL-safe token.</summary>
    public static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public bool IsUsableAt(DateTime atUtc) => RevokedAtUtc is null && atUtc < ExpiresAtUtc;

    public void Revoke(DateTime revokedAtUtc) =>
        RevokedAtUtc ??= Guard.NotLocalTime(revokedAtUtc, nameof(revokedAtUtc));
}
