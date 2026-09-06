using System.Globalization;
using System.Security.Claims;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Identity;

/// <summary>
/// The real <see cref="ICurrentActor"/>: who the bearer token says is acting.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place identity crosses from HTTP into the domain. Everything that changes
/// state takes <see cref="ICurrentActor"/> by injection and writes it into
/// <c>TableStateChange</c>, so replacing the development stub with this class is the whole of
/// "the audit log now names real people" - no service changed.
/// </para>
/// <para>
/// It reports what the token says and nothing more. It does not decide whether the caller is
/// <i>allowed</i> to do anything: that is the authorisation policies' job, and doing it in two
/// places is how the two end up disagreeing.
/// </para>
/// </remarks>
internal sealed class ClaimsCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    /// <summary>
    /// A background job, or an unauthenticated request, is <see cref="ActorType.System"/>. The
    /// audit log allows a null actor only for System, so an anonymous caller that somehow reached
    /// a state change would be recorded honestly rather than attributed to a person.
    /// </summary>
    public ActorType Type => PrincipalType switch
    {
        Domain.Enums.PrincipalType.StaffSession or Domain.Enums.PrincipalType.VenueUser => ActorType.Staff,
        Domain.Enums.PrincipalType.Diner or Domain.Enums.PrincipalType.TabParticipant => ActorType.Diner,
        _ => ActorType.System,
    };

    public Guid? StaffMemberId =>
        Type == ActorType.Staff ? ReadGuid(YallaClaims.StaffMemberId) : null;

    /// <summary>
    /// The diner's account. Null for a tab participant, who has no account by design - see
    /// <c>docs/auth.md</c>. A participant is identified by
    /// <see cref="YallaClaims.ParticipantId"/>, which is a row on a tab, not a user.
    /// </summary>
    public Guid? DinerUserId =>
        PrincipalType == Domain.Enums.PrincipalType.Diner ? ReadGuid(YallaClaims.DinerUserId) : null;

    /// <summary>
    /// The tab participant this token is for. Null for everyone else.
    /// </summary>
    /// <remarks>
    /// Gated on the principal type rather than merely reading the claim, for the same reason
    /// <see cref="DinerUserId"/> is: a handler that decides what a token is from which claims
    /// happen to be present is one forged claim away from treating something else as a participant.
    /// </remarks>
    public Guid? ParticipantId =>
        PrincipalType == Domain.Enums.PrincipalType.TabParticipant ? ReadGuid(YallaClaims.ParticipantId) : null;

    public StaffRole? Role =>
        Type == ActorType.Staff
        && Enum.TryParse<StaffRole>(Principal?.FindFirstValue(YallaClaims.Role), out var role)
            ? role
            : null;

    private PrincipalType? PrincipalType =>
        int.TryParse(
            Principal?.FindFirstValue(YallaClaims.PrincipalType),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
        && Enum.IsDefined(typeof(PrincipalType), value)
            ? (PrincipalType)value
            : null;

    private Guid? ReadGuid(string claimType) =>
        Guid.TryParse(Principal?.FindFirstValue(claimType), out var value) ? value : null;
}
