using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// Staff approved or declined a booking that was waiting - or nobody answered a short-notice one in
/// time and it was declined for them.
/// </summary>
/// <remarks>
/// <para>
/// <c>expired</c> in the data tells the app which decline it is, so it can apologise rather than say
/// the venue turned the diner down. A message written before the flag existed reads as not expired.
/// </para>
/// <para>
/// <c>wasChange</c> and <c>originalKept</c> say what a declined <i>change</i> of a confirmed booking
/// came to: the booking stands at the slot it had - the body names its time - or that slot was gone
/// too and the booking with it.
/// </para>
/// </remarks>
internal sealed class ReservationDecidedHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationDecided;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = notice switch
        {
            { Approved: true } => NotificationText.ReservationApproved(locale, notice.VenueName, notice.LocalStartTime),
            { WasChange: true, OriginalKept: true } =>
                NotificationText.ReservationChangeDeclined(locale, notice.VenueName, notice.LocalStartTime, notice.Expired),
            { WasChange: true } => NotificationText.ReservationChangeDeclinedOriginalGone(locale, notice.VenueName, notice.Expired),
            { DeclinedBecause: DeclineReason.EarlierGuestDeclined } =>
                NotificationText.ReservationEarlierGuestDeclined(locale, notice.VenueName, notice.LocalStartTime),
            { DeclinedBecause: DeclineReason.EarlierGuestNoAnswer } =>
                NotificationText.ReservationEarlierGuestNoAnswer(locale, notice.VenueName, notice.LocalStartTime),
            { Expired: true } => NotificationText.ReservationExpired(locale, notice.VenueName),
            _ => NotificationText.ReservationRejected(locale, notice.VenueName),
        };

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-decided",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["approved"] = notice.Approved ? "true" : "false",
                    ["expired"] = notice.Expired ? "true" : "false",
                    ["wasChange"] = notice.WasChange ? "true" : "false",
                    ["originalKept"] = notice.OriginalKept ? "true" : "false",

                    // Why a decline happened, as the DeclineReason number - "3" the guest before said no,
                    // "4" they did not answer - absent for an approval and for a decline written before.
                    ["declinedBecause"] = notice.DeclinedBecause is { } because
                        ? ((int)because).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty,
                }),
            cancellationToken);
    }
}
