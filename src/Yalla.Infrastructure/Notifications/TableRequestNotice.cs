namespace Yalla.Infrastructure.Notifications;

/// <summary>A later guest's request for a booking's table, as the earlier guest is told about it.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="ReservationId">The earlier guest's booking - where a tap lands.</param>
/// <param name="DinerUserId">The earlier guest.</param>
/// <param name="VenueName">The venue, as it was named when the request was made.</param>
/// <param name="TableLabel">The table.</param>
/// <param name="RequestedLocalTime">When the later guest would sit down, on the branch's wall clock.</param>
/// <param name="FreeByLocalTime">The time to have left by, on the branch's wall clock.</param>
/// <param name="FreeByUtc">The same instant, for the app's own countdown and text.</param>
internal sealed record TableRequestNotice(
    Guid RequestId,
    Guid ReservationId,
    Guid DinerUserId,
    string VenueName,
    string TableLabel,
    TimeOnly RequestedLocalTime,
    TimeOnly FreeByLocalTime,
    DateTime FreeByUtc);
