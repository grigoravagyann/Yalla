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
/// Constants in one file rather than .resx, because these are a few dozen strings that three languages
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

    /// <summary>
    /// Nobody at the venue answered a short-notice booking within the fifteen minutes, so it was
    /// declined for them.
    /// </summary>
    /// <remarks>
    /// An apology, and not the venue's "no": the venue did not turn this diner down, it did not look in
    /// time. The body sends them on - another time, or another place - while there is still an evening
    /// to book.
    /// </remarks>
    public static (string Title, string Body) ReservationExpired(string locale, string venue) =>
        Normalise(locale) switch
        {
            "ru" => ("Бронь не подтверждена вовремя",
                     $"Извините, {venue} не успел подтвердить бронь. Попробуйте другое время или другое место."),
            "en" => ("Booking not confirmed in time",
                     $"Sorry, {venue} could not confirm in time. Try another time or place."),
            _ => ("Ամրագրումը ժամանակին չհաստատվեց",
                  $"Ներողություն, {venue}-ը չհասցրեց հաստատել ամրագրումը։ Փորձեք այլ ժամ կամ այլ վայր։"),
        };

    /// <summary>
    /// A change of a confirmed booking was declined - by the venue, or for nobody answering in time -
    /// and the booking stands as it was, at <paramref name="at"/>.
    /// </summary>
    /// <remarks>
    /// The second sentence is the point: the diner asked to move, the move did not happen, and they
    /// still have the table they had. Without it "not confirmed" reads as the evening being off.
    /// </remarks>
    public static (string Title, string Body) ReservationChangeDeclined(
        string locale, string venue, TimeOnly at, bool expired) =>
        Normalise(locale) switch
        {
            "ru" => ("Новое время не подтверждено",
                     (expired
                         ? $"Извините, {venue} не успел подтвердить изменение. "
                         : $"{venue} не смог принять изменение. ")
                     + $"Ваша бронь на {Time(at)} остаётся в силе."),
            "en" => ("Change not confirmed",
                     (expired
                         ? $"Sorry, {venue} could not confirm the change in time. "
                         : $"{venue} could not take the change. ")
                     + $"Your booking at {Time(at)} stands."),
            _ => ("Նոր ժամը չհաստատվեց",
                  (expired
                      ? $"Ներողություն, {venue}-ը չհասցրեց հաստատել փոփոխությունը։ "
                      : $"{venue}-ը չկարողացավ ընդունել փոփոխությունը։ ")
                  + $"Ձեր {Time(at)}-ի ամրագրումը մնում է ուժի մեջ։"),
        };

    /// <summary>
    /// A change of a confirmed booking was declined, and the slot it had was gone too - taken by
    /// another diner, or over - so the booking ended with the change.
    /// </summary>
    public static (string Title, string Body) ReservationChangeDeclinedOriginalGone(
        string locale, string venue, bool expired) =>
        Normalise(locale) switch
        {
            "ru" => ("Бронь не подтверждена",
                     (expired
                         ? $"Извините, {venue} не успел подтвердить изменение"
                         : $"{venue} не смог принять изменение")
                     + ", а прежнее время уже занято. Попробуйте другое время или другое место."),
            "en" => ("Booking not confirmed",
                     (expired
                         ? $"Sorry, {venue} could not confirm the change in time"
                         : $"{venue} could not take the change")
                     + ", and your earlier time has been taken since. Try another time or place."),
            _ => ("Ամրագրումը չհաստատվեց",
                  (expired
                      ? $"Ներողություն, {venue}-ը չհասցրեց հաստատել փոփոխությունը"
                      : $"{venue}-ը չկարողացավ ընդունել փոփոխությունը")
                  + ", իսկ նախկին ժամն արդեն զբաղված է։ Փորձեք այլ ժամ կամ այլ վայր։"),
        };

    /// <summary>
    /// The guest with the table before this booking said they would not have left in time, so the
    /// booking was declined.
    /// </summary>
    /// <remarks>
    /// The other guest stays anonymous - "the guest before you" - and the body sends the diner on to
    /// another time or table, which is what they can do about it.
    /// </remarks>
    public static (string Title, string Body) ReservationEarlierGuestDeclined(string locale, string venue, TimeOnly at) =>
        Normalise(locale) switch
        {
            "ru" => ("Столик в это время занят",
                     $"{venue}: гости перед вами не освободят столик к {Time(at)}. Выберите другое время или столик."),
            "en" => ("That table is not free then",
                     $"{venue}: the guests before you will not have left by {Time(at)}. Try another time or table."),
            _ => ("Սեղանն այդ ժամին զբաղված է",
                  $"{venue}․ Ձեզնից առաջ եկած հյուրերը չեն ազատի սեղանը մինչև {Time(at)}-ը։ Ընտրեք այլ ժամ կամ սեղան։"),
        };

    /// <summary>
    /// The guest with the table before this booking did not answer within the two hours, so the booking
    /// was declined.
    /// </summary>
    public static (string Title, string Body) ReservationEarlierGuestNoAnswer(string locale, string venue, TimeOnly at) =>
        Normalise(locale) switch
        {
            "ru" => ("Бронь не подтверждена вовремя",
                     $"{venue}: не удалось вовремя подтвердить столик на {Time(at)}. Выберите другое время или столик."),
            "en" => ("Booking not confirmed in time",
                     $"{venue}: we could not confirm the table for {Time(at)} in time. Try another time or table."),
            _ => ("Ամրագրումը ժամանակին չհաստատվեց",
                  $"{venue}․ չհաջողվեց ժամանակին հաստատել սեղանը {Time(at)}-ի համար։ Ընտրեք այլ ժամ կամ սեղան։"),
        };

    // ---------------------------------------------------------------- the table after yours

    /// <summary>
    /// "Someone would like your table at 19:45. Will you have left by 19:40?" - to the guest with the
    /// table first, with Yes and No on the notification.
    /// </summary>
    /// <remarks>
    /// The other guest stays anonymous. The question is the whole message, because it is answered from
    /// the lock screen.
    /// </remarks>
    public static (string Title, string Body) TableRequested(
        string locale, string venue, TimeOnly requestedAt, TimeOnly freeBy) =>
        Normalise(locale) switch
        {
            "ru" => ($"Ваш столик хотят занять в {Time(requestedAt)}",
                     $"{venue}: вы освободите столик к {Time(freeBy)}?"),
            "en" => ($"Someone would like your table at {Time(requestedAt)}",
                     $"{venue}: will you have left by {Time(freeBy)}?"),
            _ => ($"Ձեր սեղանը ցանկանում են ամրագրել {Time(requestedAt)}-ին",
                  $"{venue}․ կազատե՞ք սեղանը մինչև {Time(freeBy)}-ը։"),
        };

    /// <summary>
    /// The guest said yes - or the venue said it for them after calling - and is now held to the time:
    /// "please free the table by 19:40".
    /// </summary>
    public static (string Title, string Body) TableRequestAccepted(string locale, string venue, TimeOnly freeBy) =>
        Normalise(locale) switch
        {
            "ru" => ("Спасибо!",
                     $"{venue}: пожалуйста, освободите столик к {Time(freeBy)} — после вас придут гости."),
            "en" => ("Thank you",
                     $"{venue}: please free the table by {Time(freeBy)} - the next guests are coming after you."),
            _ => ("Շնորհակալություն",
                  $"{venue}․ խնդրում ենք ազատել սեղանը մինչև {Time(freeBy)}-ը — Ձեզնից հետո հյուրեր են գալու։"),
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

    /// <summary>
    /// "Anna wants to join your tab", to the host.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="ParticipantApproved"/>: the joiner is on "waiting for the host",
    /// and the host has no reason to open the People screen. The table is the title because a host
    /// knows their table, and the name is the joiner's own - "a guest" when they gave none, never the
    /// roster's English "Guest 3".
    /// </remarks>
    public static (string Title, string Body) JoinRequested(string locale, string tableLabel, string? guestName)
    {
        var name = string.IsNullOrWhiteSpace(guestName) ? null : guestName.Trim();

        return Normalise(locale) switch
        {
            "ru" => ($"Столик {tableLabel}",
                     $"{name ?? "Гость"} хочет присоединиться к вашему счёту."),
            "en" => ($"Table {tableLabel}",
                     $"{name ?? "A guest"} wants to join your tab."),
            _ => ($"Սեղան {tableLabel}",
                  name is null
                      ? "Մի հյուր ցանկանում է միանալ Ձեր հաշվին։"
                      // The name first, set off with the Armenian pause as the app's own banner
                      // reads it, so a name in any script needs no article.
                      : $"{name}՝ ցանկանում է միանալ Ձեր հաշվին։"),
        };
    }

    public static (string Title, string Body) OrderReady(string locale, string tableLabel) =>
        Normalise(locale) switch
        {
            "ru" => ("Заказ готов", $"Столик {tableLabel}."),
            "en" => ("Your order is ready", $"Table {tableLabel}."),
            _ => ("Պատվերը պատրաստ է", $"Սեղան {tableLabel}։"),
        };

    private static string Time(TimeOnly at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);
}
