namespace Yalla.Infrastructure.Notifications;

/// <summary>A tab's news - a joiner let on, food that is up, somebody waiting for the host - as its
/// diner is told it.</summary>
/// <param name="TabId">The tab, which is where a tap lands.</param>
/// <param name="ParticipantId">Who it is about: the approved joiner, whoever ordered, or the one waiting.</param>
/// <param name="DinerUserId">Who it is sent to.</param>
/// <param name="TableLabel">The table as it was labelled when the message was written.</param>
/// <param name="GuestName">The name a waiting joiner gave, for the host. Null when they gave none.</param>
internal sealed record TabNotice(
    Guid TabId,
    Guid ParticipantId,
    Guid DinerUserId,
    string TableLabel,
    string? GuestName = null);
