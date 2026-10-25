using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Who a table's change-stream row and stored answer name for the request acting: a staff member, a tab participant, or
/// a diner with an account is named; a request with no token has nobody and is recorded as the system - which is what
/// the row's invariant allows a null actor for.
/// </summary>
/// <remarks>
/// The participant is preferred over the account for the same reason as on the tab's event stream: a participant has no
/// account by design, so naming only account holders left the row anonymous for very nearly every diner-driven
/// transition. A scan that seats a table arrives with no token at all and is still the system, correctly - the
/// participant row does not exist until that request creates it.
/// </remarks>
internal static class TableStateActors
{
    public static (ActorType Type, Guid? Id) Resolve(ICurrentActor actor) => actor.Type switch
    {
        ActorType.Staff when actor.StaffMemberId is { } staffId => (ActorType.Staff, staffId),
        ActorType.Diner when actor.ParticipantId is { } participantId => (ActorType.Diner, participantId),
        ActorType.Diner when actor.DinerUserId is { } dinerId => (ActorType.Diner, dinerId),
        _ => (ActorType.System, null),
    };
}
