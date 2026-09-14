using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The one check between a diner account and anything that lands on its phone number: a booking,
/// a hold kept on one, a tab opened from one.
/// </summary>
/// <remarks>
/// <para>
/// The <c>VerifiedDiner</c> policy admits any diner token, including the one <c>register</c>
/// hands out before the number is proved. That is right for the profile and for reading one's own
/// bookings, and wrong for creating anything a reminder or a no-show will be sent to. So the
/// services that create those call this, and it reads <c>DinerUsers.PhoneVerifiedAtUtc</c> from
/// the database - the token cannot carry the fact, because the fact changes under a live token the
/// moment a code comes back.
/// </para>
/// <para>
/// Called from the services, not the endpoints, for the reason <see cref="VenueGate"/> is: a rule
/// that lives in the service holds for every caller, including the next one somebody adds. Reading
/// and cancelling are deliberately not gated - an account that cannot book has nothing to read, and
/// taking away the cancel button would only strand a booking made before this check existed.
/// </para>
/// </remarks>
internal static class DinerPhoneGate
{
    /// <exception cref="PhoneNotVerifiedException">
    /// The account's number has never been proved, the account is inactive or deleted, or the
    /// account row does not exist.
    /// </exception>
    public static async Task RequireVerifiedPhoneAsync(
        YallaDbContext db,
        Guid dinerUserId,
        string operation,
        CancellationToken cancellationToken)
    {
        // A missing row is refused as unverified rather than let through: a token for an account
        // that is not there proves nothing about any number. An inactive or deleted one is refused
        // the same way - the token check in front of every diner route already turns those away,
        // but it reads through a five-second cache, and this read does not.
        var verified = await db.DinerUsers
            .AsNoTracking()
            .Where(d => d.Id == dinerUserId)
            .Select(d => d.PhoneVerifiedAtUtc != null && d.IsActive && d.DeletedAtUtc == null)
            .FirstOrDefaultAsync(cancellationToken);

        if (!verified)
        {
            throw new PhoneNotVerifiedException(dinerUserId, operation);
        }
    }
}
