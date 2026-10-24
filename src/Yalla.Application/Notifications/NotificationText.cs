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

    // ---------------------------------------------------------------- "on your way?"

    /// <summary>
    /// "Are you on your way?" 45 minutes before a booking with a pre-order: yes tells the kitchen to go ahead, no
    /// cancels. Formal, as every message here.
    /// </summary>
    public static (string Title, string Body) OnYourWay(string locale, string venue, TimeOnly at) =>
        Normalise(locale) switch
        {
            "ru" => ("Вы в пути?",
                     $"{venue} ждёт вас в {Time(at)}, ваш предзаказ готов уйти на кухню. Подтвердите, что придёте, "
                     + "или отмените бронь."),
            "en" => ("Are you on your way?",
                     $"{venue} expects you at {Time(at)} and your pre-order is ready to go to the kitchen. Confirm "
                     + "you are coming, or cancel the booking."),
            _ => ("Ճանապարհի՞ն եք",
                  $"{venue}-ը սպասում է Ձեզ {Time(at)}-ին, և Ձեր նախապատվերը պատրաստ է խոհանոց ուղարկվելու։ "
                  + "Հաստատեք, որ գալիս եք, կամ չեղարկեք ամրագրումը։"),
        };

    /// <summary>
    /// "Are you on your way?" 30 minutes before every other booking: yes tells the waiter, who then does not call; no
    /// cancels. Formal, as every message here.
    /// </summary>
    public static (string Title, string Body) OnYourWayWithoutPreOrder(string locale, string venue, TimeOnly at) =>
        Normalise(locale) switch
        {
            "ru" => ("Вы в пути?",
                     $"{venue} ждёт вас в {Time(at)}. Подтвердите, что придёте, и вам не будут звонить, "
                     + "или отмените бронь."),
            "en" => ("Are you on your way?",
                     $"{venue} expects you at {Time(at)}. Confirm you are coming and nobody will need to call you, "
                     + "or cancel the booking."),
            _ => ("Ճանապարհի՞ն եք",
                  $"{venue}-ը սպասում է Ձեզ {Time(at)}-ին։ Հաստատեք, որ գալիս եք, և Ձեզ չեն զանգի, "
                  + "կամ չեղարկեք ամրագրումը։"),
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

    // ---------------------------------------------------------------- done from the manage link

    /// <summary>
    /// "Your booking was changed" - to the account holder, after somebody holding the booking's link
    /// moved it.
    /// </summary>
    /// <remarks>
    /// The link goes round group chats, so the person who moved it may not be the diner. The body says
    /// where the booking stands now - and that it is waiting for the venue, when it is - and how it was
    /// changed, so a diner who did not do it knows to look before they turn up at the old time.
    /// </remarks>
    public static (string Title, string Body) ReservationChangedByLink(
        string locale, string venue, DateOnly date, TimeOnly at, int partySize, bool waiting) =>
        Normalise(locale) switch
        {
            "ru" => ("Ваша бронь изменена",
                     $"{venue}: теперь {Date(date)} в {Time(at)}, гостей: {partySize}"
                     + (waiting ? ", ждёт ответа заведения" : string.Empty)
                     + ". Изменено по ссылке на бронь — если это были не вы, проверьте бронь."),
            "en" => ("Your booking was changed",
                     $"{venue}: now {Date(date)} at {Time(at)}, {partySize} guests"
                     + (waiting ? ", waiting for the venue's answer" : string.Empty)
                     + ". Changed using the booking link - if that wasn't you, check your booking."),
            _ => ("Ձեր ամրագրումը փոխվել է",
                  $"{venue}՝ այժմ {Date(date)} {Time(at)}-ին, {partySize} հյուր"
                  + (waiting ? ", սպասում է վայրի պատասխանին" : string.Empty)
                  + "։ Փոխվել է ամրագրման հղումով — եթե դա Դուք չէիք, ստուգեք Ձեր ամրագրումը։"),
        };

    /// <summary>
    /// "You can't book for a week" - to a guest who missed three bookings in three days. Says what happened to the
    /// bookings they had, and the day they can book again.
    /// </summary>
    /// <summary>"Marked as a no-show" - to the guest, every time, with what to do if it is wrong.</summary>
    public static (string Title, string Body) NoShowMarked(string locale, string venue, DateOnly date, TimeOnly at) =>
        Normalise(locale) switch
        {
            "ru" => ("Отмечено: вы не пришли",
                     $"{venue}, {Date(date)} в {Time(at)}. Если это ошибка, свяжитесь с заведением."),
            "en" => ("Marked as a no-show",
                     $"{venue}, {Date(date)} at {Time(at)}. If this is wrong, contact the venue."),
            _ => ("Նշվել է՝ չեք եկել",
                  $"{venue}, {Date(date)} {Time(at)}։ Եթե սա սխալ է, կապվեք վայրի հետ։"),
        };

    /// <summary>"You can book again" - to a guest whose block was lifted early. What it cancelled stays cancelled.</summary>
    public static (string Title, string Body) BookingsUnblocked(string locale) =>
        Normalise(locale) switch
        {
            "ru" => ("Бронирование снова доступно",
                     "Ограничение снято. Отменённые брони не восстановлены — забронируйте заново."),
            "en" => ("You can book again",
                     "Your booking pause was lifted. Bookings it cancelled were not restored - book them again."),
            _ => ("Կրկին կարող եք ամրագրել",
                  "Սահմանափակումը հանված է։ Չեղարկված ամրագրումները չեն վերականգնվել — ամրագրեք կրկին։"),
        };

    public static (string Title, string Body) BookingsBlocked(string locale, DateOnly until, int cancelledCount) =>
        Normalise(locale) switch
        {
            "ru" => ("Бронирование приостановлено",
                     $"Три пропущенные брони за три дня. Отменено предстоящих броней: {cancelledCount}. "
                     + $"Снова бронировать можно с {Date(until)}."),
            "en" => ("Booking paused for a week",
                     $"Three missed bookings in three days. {cancelledCount} upcoming bookings were cancelled. "
                     + $"You can book again from {Date(until)}."),
            _ => ("Ամրագրումը դադարեցված է",
                  $"Երեք օրում երեք բաց թողնված ամրագրում։ Չեղարկվել է առաջիկա {cancelledCount} ամրագրում։ "
                  + $"Կրկին ամրագրել կարող եք {Date(until)}-ից։"),
        };

    /// <summary>"Your booking was cancelled" - to the account holder, after somebody holding the link cancelled it.</summary>
    public static (string Title, string Body) ReservationCancelledByLink(
        string locale, string venue, DateOnly date, TimeOnly at) =>
        Normalise(locale) switch
        {
            "ru" => ("Ваша бронь отменена",
                     $"{venue}, {Date(date)} в {Time(at)}. Отменено по ссылке на бронь — если это были не вы, "
                     + "забронируйте снова или позвоните в заведение."),
            "en" => ("Your booking was cancelled",
                     $"{venue}, {Date(date)} at {Time(at)}. Cancelled using the booking link - if that wasn't you, "
                     + "book again or call the venue."),
            _ => ("Ձեր ամրագրումը չեղարկվել է",
                  $"{venue}, {Date(date)} {Time(at)}։ Չեղարկվել է ամրագրման հղումով — եթե դա Դուք չէիք, "
                  + "ամրագրեք կրկին կամ զանգահարեք վայր։"),
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

    // ---------------------------------------------------------------- takeaway

    /// <summary>"Order accepted - ready at 19:10": the venue took the guest's takeaway order. Pay at pickup.</summary>
    public static (string Title, string Body) TakeawayAccepted(string locale, string venue, string code, TimeOnly readyAt) =>
        Normalise(locale) switch
        {
            "ru" => ("Заказ принят", $"{venue}: заказ {code} будет готов к {Time(readyAt)}. Оплата при получении."),
            "en" => ("Order accepted", $"{venue}: {code} will be ready at {Time(readyAt)}. Pay when you pick it up."),
            _ => ("Պատվերն ընդունված է", $"{venue}. {code} պատվերը պատրաստ կլինի {Time(readyAt)}-ին։ Վճարում եք վերցնելիս։"),
        };

    /// <summary>"Your order is ready - come and pick it up."</summary>
    public static (string Title, string Body) TakeawayReady(string locale, string venue, string code) =>
        Normalise(locale) switch
        {
            "ru" => ("Заказ готов", $"{venue}: заказ {code} готов, можно забирать."),
            "en" => ("Your order is ready", $"{venue}: {code} is ready - come and pick it up."),
            _ => ("Պատվերը պատրաստ է", $"{venue}. {code} պատվերը պատրաստ է, կարող եք գալ և վերցնել։"),
        };

    /// <summary>
    /// A takeaway order declined: by the venue, with the reason it gave, or because nobody answered in time - an apology,
    /// not the venue's "no".
    /// </summary>
    public static (string Title, string Body) TakeawayDeclined(string locale, string venue, string code, string? reason, bool noAnswer) =>
        (Normalise(locale), noAnswer, string.IsNullOrWhiteSpace(reason)) switch
        {
            ("ru", true, _) => ("Заказ не принят", $"{venue} не ответил вовремя, заказ {code} отменён. Ничего не списано."),
            ("ru", false, true) => ("Заказ отклонён", $"{venue} не может принять заказ {code}. Попробуйте другое время."),
            ("ru", false, false) => ("Заказ отклонён", $"{venue} не может принять заказ {code}: {reason}"),
            ("en", true, _) => ("Order not accepted", $"{venue} did not answer in time, so {code} was cancelled. Nothing was charged."),
            ("en", false, true) => ("Order declined", $"{venue} could not take {code}. Try another time."),
            ("en", false, false) => ("Order declined", $"{venue} could not take {code}: {reason}"),
            (_, true, _) => ("Պատվերը չի ընդունվել", $"{venue}-ը ժամանակին չպատասխանեց, {code} պատվերը չեղարկվեց։ Ոչինչ չի գանձվել։"),
            (_, false, true) => ("Պատվերը մերժվել է", $"{venue}-ը չի կարող ընդունել {code} պատվերը։ Փորձեք այլ ժամի։"),
            _ => ("Պատվերը մերժվել է", $"{venue}-ը չի կարող ընդունել {code} պատվերը՝ {reason}"),
        };

    // ---------------------------------------------------------------- guest messages

    /// <summary>
    /// A venue message in a booking's or an order's thread: the venue's name over the message itself (at most 140
    /// characters, already cut), or - for the venue's "are you coming?" - the question with the booking's time. Never who
    /// at the venue wrote it.
    /// </summary>
    public static (string Title, string Body) VenueMessage(string locale, string venue, string? text, bool ask, TimeOnly? at) =>
        (ask, at) switch
        {
            (true, { } time) => (venue, Normalise(locale) switch
            {
                "ru" => $"Вы придёте к {Time(time)}?",
                "en" => $"Are you coming at {Time(time)}?",
                _ => $"Գալի՞ս եք {Time(time)}-ին։",
            }),
            (true, null) => (venue, Normalise(locale) switch
            {
                "ru" => "Вы придёте?",
                "en" => "Are you coming?",
                _ => "Գալի՞ս եք։",
            }),
            _ => (venue, text ?? string.Empty),
        };

    private static string Time(TimeOnly at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Day and month, as all three languages write them on a phone: 24.10.</summary>
    private static string Date(DateOnly date) => date.ToString("dd.MM", CultureInfo.InvariantCulture);
}
