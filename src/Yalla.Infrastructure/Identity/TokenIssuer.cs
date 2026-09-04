using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Mints the four kinds of access token. One class, so the claim set of each identity type is
/// written down in one readable place rather than assembled in four services.
/// </summary>
internal sealed class TokenIssuer(IOptions<JwtOptions> options, IClock clock)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>The signing credentials, built once from the configured key.</summary>
    private SigningCredentials Credentials => new(
        new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(_options.SigningKey)),
        SecurityAlgorithms.HmacSha256);

    /// <summary>
    /// A token for someone at a table with no account.
    /// </summary>
    /// <remarks>
    /// Three claims and nothing else. There is no user id to put in it because there is no user,
    /// and there is no branch-wide or venue-wide claim because a participant must be structurally
    /// unable to address anything but this one tab.
    /// </remarks>
    public (string Token, DateTime ExpiresAtUtc) IssueTabParticipantToken(
        Guid participantId,
        Guid tabId,
        Guid branchId,
        DateTime expiresAtUtc)
    {
        var claims = new List<Claim>
        {
            new(YallaClaims.PrincipalType, ((int)PrincipalType.TabParticipant).ToString(CultureInfo.InvariantCulture)),
            new(YallaClaims.ParticipantId, participantId.ToString()),
            new(YallaClaims.TabId, tabId.ToString()),
            new(YallaClaims.BranchId, branchId.ToString()),
        };

        return (Write(claims, expiresAtUtc), expiresAtUtc);
    }

    /// <summary>A token for a diner who verified a phone number.</summary>
    public (string Token, DateTime ExpiresAtUtc) IssueDinerToken(Guid dinerUserId)
    {
        var expiresAtUtc = clock.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(YallaClaims.PrincipalType, ((int)PrincipalType.Diner).ToString(CultureInfo.InvariantCulture)),
            new(YallaClaims.DinerUserId, dinerUserId.ToString()),
            new(JwtRegisteredClaimNames.Sub, dinerUserId.ToString()),
        };

        return (Write(claims, expiresAtUtc), expiresAtUtc);
    }

    /// <summary>
    /// A long-lived token for an enrolled tablet. It names a branch and a device, never a person -
    /// the tablet is the thing a PIN is typed into, not an identity that can act.
    /// </summary>
    public (string Token, DateTime ExpiresAtUtc) IssueDeviceToken(Guid deviceId, Guid branchId, Guid venueId)
    {
        var expiresAtUtc = clock.UtcNow.AddDays(_options.DeviceTokenDays);

        var claims = new List<Claim>
        {
            new(YallaClaims.PrincipalType, ((int)PrincipalType.StaffDevice).ToString(CultureInfo.InvariantCulture)),
            new(YallaClaims.DeviceId, deviceId.ToString()),
            new(YallaClaims.BranchId, branchId.ToString()),
            new(YallaClaims.VenueId, venueId.ToString()),
        };

        return (Write(claims, expiresAtUtc), expiresAtUtc);
    }

    /// <summary>
    /// A short-lived token for the person currently working on a tablet.
    /// </summary>
    /// <remarks>
    /// It carries the branch copied from the device, which is the claim that stops a waiter at
    /// branch A from acting on branch B however the request is addressed.
    /// </remarks>
    public (string Token, DateTime ExpiresAtUtc) IssueStaffSessionToken(
        Guid staffMemberId,
        Guid sessionId,
        Guid deviceId,
        Guid branchId,
        Guid venueId,
        StaffRole role)
    {
        var expiresAtUtc = clock.UtcNow.AddMinutes(_options.StaffSessionMinutes);

        var claims = new List<Claim>
        {
            new(YallaClaims.PrincipalType, ((int)PrincipalType.StaffSession).ToString(CultureInfo.InvariantCulture)),
            new(YallaClaims.StaffMemberId, staffMemberId.ToString()),
            new(YallaClaims.SessionId, sessionId.ToString()),
            new(YallaClaims.DeviceId, deviceId.ToString()),
            new(YallaClaims.BranchId, branchId.ToString()),
            new(YallaClaims.VenueId, venueId.ToString()),
            new(YallaClaims.Role, role.ToString()),
            new(JwtRegisteredClaimNames.Sub, staffMemberId.ToString()),
        };

        return (Write(claims, expiresAtUtc), expiresAtUtc);
    }

    /// <summary>
    /// A token for an owner or manager in the admin panel.
    /// </summary>
    /// <remarks>
    /// Venue-scoped, not branch-scoped: this is the account that manages every branch. When such a
    /// person carries a home branch it is included, so branch-scoped endpoints work for them
    /// without a second sign-in.
    /// </remarks>
    public (string Token, DateTime ExpiresAtUtc) IssueVenueUserToken(
        Guid staffMemberId,
        Guid venueId,
        Guid? branchId,
        StaffRole role)
    {
        var expiresAtUtc = clock.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(YallaClaims.PrincipalType, ((int)PrincipalType.VenueUser).ToString(CultureInfo.InvariantCulture)),
            new(YallaClaims.StaffMemberId, staffMemberId.ToString()),
            new(YallaClaims.VenueId, venueId.ToString()),
            new(YallaClaims.Role, role.ToString()),
            new(JwtRegisteredClaimNames.Sub, staffMemberId.ToString()),
        };

        if (branchId is { } branch)
        {
            claims.Add(new Claim(YallaClaims.BranchId, branch.ToString()));
        }

        return (Write(claims, expiresAtUtc), expiresAtUtc);
    }

    /// <summary>Lifetime of a normal access token, as the clients report it.</summary>
    public int AccessTokenSeconds => _options.AccessTokenMinutes * 60;

    /// <summary>Lifetime of a staff session token, as the tablet reports it.</summary>
    public int StaffSessionSeconds => _options.StaffSessionMinutes * 60;

    private string Write(List<Claim> claims, DateTime expiresAtUtc)
    {
        var nowUtc = clock.UtcNow;

        // A unique id per token, so an individual token can be denied later without inventing an
        // identifier for it after the fact.
        claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = nowUtc,
            NotBefore = nowUtc,
            Expires = expiresAtUtc,
            SigningCredentials = Credentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
