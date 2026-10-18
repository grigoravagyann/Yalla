using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// The earlier guest answered a request for their table that is no longer waiting: it was answered
/// another way already, it ran out of time, or it went away because the other booking moved or ended.
/// </summary>
/// <remarks>
/// Its own code, <c>table-request-not-pending</c>, so the app drops the card and says why rather than
/// offering Yes and No again. Answering the same way twice is not this - that is answered with the
/// booking as it stands. Still a <see cref="DomainStateException"/>; the API maps it first.
/// </remarks>
public sealed class TableRequestNotPendingException(
    Guid reservationId,
    Guid requestId,
    TableShareRequestStatus status,
    DateTime expiresAtUtc)
    : DomainStateException(
        status == TableShareRequestStatus.Pending
            ? "The time to answer this request ran out, so it no longer needs an answer. Your booking is unchanged."
            : $"This request for your table was already {status.ToString().ToLowerInvariant()}, so it no longer "
              + "needs an answer. Your booking is unchanged.")
{
    /// <summary>The earlier guest's booking.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>The request.</summary>
    public Guid RequestId { get; } = requestId;

    /// <summary>Where it stands now. Pending when it is only the clock that ended it and the sweep has not run yet.</summary>
    public TableShareRequestStatus Status { get; } = status;

    /// <summary>When it ran out, or would have.</summary>
    public DateTime ExpiresAtUtc { get; } = expiresAtUtc;
}
