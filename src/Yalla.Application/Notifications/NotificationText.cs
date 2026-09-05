using System.Globalization;

namespace Yalla.Application.Notifications;

/// <summary>
/// Every word a diner is sent, in Armenian, Russian and English.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selected by the diner's stored locale, never by the branch's.</b> A message is written for the
/// person reading it. A Russian-speaking regular at an Armenian venue gets Russian, and a venue that
/// serves three languages does not have to pick one on their diners' behalf.
/// </para>
/// <para>
/// Constants in one file rather than .resx, because these are eleven strings that three languages
/// share and the translator is a person reading this file, not a tool. Anything unknown falls back to
/// Armenian - the market's language, and the one most likely to be understood by someone whose phone
/// is set to something we do not have.
/// </para>
/// <para>
/// Kept short on purpose. A push notification is read on a lock screen at a glance, and the second
/// sentence is never read at all.
/// </para>
/// </remarks>
public static class NotificationText
{
    /// <summary>The languages there are words for.</summary>
    public static readonly string[] Supported = ["hy", "ru", "en"];

    /// <summary>
    /// Narrows a stored locale to one that has words, defaulting to Armenian.
    /// </summary>
    /// <remarks>
    /// Matches on the language subtag, so <c>ru-RU</c> and <c>ru</c> are the same thing - a phone
    /// reports a region and nothing here varies by one.
    /// </remarks>
    public static string Normalise(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return "hy";
        }

        var language = locale.Split('-', '_')[0].ToLowerInvariant();

        return Supported.Contains(language) ? language : "hy";
    }

    // ---------------------------------------------------------------- the reminder

    /// <summary>
    /// "Your table at Kavkaz is at 20:00."
    /// </summary>
    /// <remarks>
    /// The point of the whole feature. <b>Cancelling has to be easier than not showing up</b>, so the
    /// body says so and the notification carries the action: the diner cancels from the lock screen
    /// without opening the app and hunting for a screen.
    /// </remarks>
    public static (string Title, string Body) Reminder(
        string locale, string venue, string branch, string tableLabel, TimeOnly at) =>
        Normalise(locale) switch
        {
            "ru" => ($"Столик в {venue} в {Time(at)}",
                     $"{branch}, столик {tableLabel}. Не получается? Отмените — это займёт секунду и "
                     + "освободит столик для других."),
            "en" => ($"Your table at {venue} is at {Time(at)}",
                     $"{branch}, table {tableLabel}. Cannot make it? Cancel here - it takes a second and "
                     + "frees the table for someone else."),
            _ => ($"Ձեր սեղանը {venue}-ում {Time(at)}-ին է",
                  $"{branch}, սեղան {tableLabel}։ Չե՞ք հասցնում։ Չեղարկեք այստեղ — վայրկյան է տևում և "
                  + "սեղանը կազատվի ուրիշի համար։"),
        };

    // ---------------------------------------------------------------- the late nudge

    /// <summary>"Still coming?" with the one-tap hold extension behind it.</summary>
    public static (string Title, string Body) LateNudge(
        string locale, string venue, string tableLabel, int extensionMinutes) =>
        Normalise(locale) switch
        {
            "ru" => ("Всё ещё в пути?",
                     $"{venue} держит столик {tableLabel}. Нажмите, чтобы продлить бронь на "
                     + $"{extensionMinutes} минут."),
            "en" => ("Still coming?",
                     $"{venue} is holding table {tableLabel}. Tap to keep it for another "
                     + $"{extensionMinutes} minutes."),
            _ => ("Դեռ ճանապարհի՞ն եք",
                  $"{venue}-ը պահում է {tableLabel} սեղանը։ Սեղմեք՝ ևս {extensionMinutes} րոպեով "
                  + "պահելու համար։"),
        };

    // ---------------------------------------------------------------- the manager's decision

    public static (string Title, string Body) ReservationApproved(
        string locale, string venue, TimeOnly at) =>
        Normalise(locale) switch
        {
            "ru" => ("Бронь подтверждена", $"{venue} ждёт вас в {Time(at)}."),
            "en" => ("Booking confirmed", $"{venue} is expecting you at {Time(at)}."),
            _ => ("Ամրագրումը հաստատված է", $"{venue}-ը սպասում է ձեզ {Time(at)}-ին։"),
        };

    public static (string Title, string Body) ReservationRejected(string locale, string venue) =>
        Normalise(locale) switch
        {
            "ru" => ("Бронь не подтверждена",
                     $"{venue} не смог принять эту бронь. Попробуйте другое время."),
            "en" => ("Booking not confirmed",
                     $"{venue} could not take this booking. Try another time."),
            _ => ("Ամրագրումը չհաստատվեց",
                  $"{venue}-ը չկարողացավ ընդունել այս ամրագրումը։ Փորձեք այլ ժամ։"),
        };

    // ---------------------------------------------------------------- the tab

    /// <summary>
    /// The host let a pending joiner on.
    /// </summary>
    /// <remarks>
    /// Worth a push precisely because the guest is not looking: they are sitting at the table with
    /// the phone in their pocket, on a screen that says "waiting for the host".
    /// </remarks>
    public static (string Title, string Body) ParticipantApproved(string locale, string tableLabel) =>
        Normalise(locale) switch
        {
            "ru" => ("Вы в счёте", $"Столик {tableLabel} — теперь можно заказывать."),
            "en" => ("You are on the tab", $"Table {tableLabel} - you can order now."),
            _ => ("Դուք միացաք հաշվին", $"Սեղան {tableLabel} — այժմ կարող եք պատվիրել։"),
        };

    public static (string Title, string Body) OrderReady(string locale, string tableLabel) =>
        Normalise(locale) switch
        {
            "ru" => ("Заказ готов", $"Столик {tableLabel}."),
            "en" => ("Your order is ready", $"Table {tableLabel}."),
            _ => ("Պատվերը պատրաստ է", $"Սեղան {tableLabel}։"),
        };

    private static string Time(TimeOnly at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);
}
