using Microsoft.EntityFrameworkCore;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The refresh endpoint's implementation for both identity types that have refresh tokens.
/// </summary>
internal sealed class TokenRefreshService(
    YallaDbContext db,
    TokenIssuer tokens,
    RefreshTokenStore refreshTokens) : ITokenRefreshService
{
    public async Task<RefreshResult> RefreshDinerAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var (successorEntity, successor, dinerUserId) =
            await refreshTokens.RotateAsync(refreshToken, RefreshTokenSubject.Diner, cancellationToken);

        var diner = await db.DinerUsers.FirstOrDefaultAsync(d => d.Id == dinerUserId, cancellationToken);

        // Deletion revokes every refresh token too; checked here as well so the tombstone can never
        // mint an access token, whatever happened to the chain.
        if (diner is null || !diner.IsActive || diner.IsDeleted)
        {
            throw new AuthenticationFailedException(
                "account-inactive", "This account is no longer active.");
        }

        // Named for the chain it continues, so a password change made with it keeps this sign-in.
        var (accessToken, _) = tokens.IssueDinerToken(diner.Id, diner.SessionGeneration, successorEntity.ChainId);

        await db.SaveChangesAsync(cancellationToken);

        return new RefreshResult(accessToken, successor, tokens.AccessTokenSeconds);
    }

    public async Task<RefreshResult> RefreshVenueUserAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var (_, successor, staffMemberId) =
            await refreshTokens.RotateAsync(refreshToken, RefreshTokenSubject.VenueUser, cancellationToken);

        var staff = await db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staffMemberId, cancellationToken);

        if (staff is null || !staff.IsActive)
        {
            throw new AuthenticationFailedException(
                "account-inactive", "This account is no longer active.");
        }

        var (accessToken, _) = tokens.IssueVenueUserToken(
            staff.Id, staff.VenueId, staff.BranchId, staff.Role);

        await db.SaveChangesAsync(cancellationToken);

        return new RefreshResult(accessToken, successor, tokens.AccessTokenSeconds);
    }

    public async Task SignOutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var existing = await refreshTokens.FindAsync(refreshToken, cancellationToken);

        if (existing is null)
        {
            // Signing out is never allowed to fail. An unknown token is already in the state the
            // caller asked for, and answering differently would say whether it was ever real.
            return;
        }

        await refreshTokens.RevokeChainAsync(existing.ChainId, "signed-out", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
