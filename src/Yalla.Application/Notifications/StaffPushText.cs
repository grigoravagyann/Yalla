using System.Globalization;

namespace Yalla.Application.Notifications;

/// <summary>
/// Every word a member of staff is pushed, in Armenian, Russian and English.
/// </summary>
/// <remarks>
/// <para>
/// <b>The device's language</b>, as the staff app last said (<c>StaffDevice.Locale</c>) - on a phone that is its
/// person's choice in More. Unknown or unset falls back to Armenian, the branch's working language, exactly as
/// <see cref="NotificationText.Normalise"/> does for a diner.
/// </para>
/// <para>
/// Written for a glance at a lock screen in a pocket: the title says what needs doing and where, the body the one
/// fact that decides whether to go now. Never a guest's phone number, and never the name of the colleague who
/// caused it.
/// </para>
/// </remarks>
public static class StaffPushText
{
    /// <summary>The longest guest message quoted in a push; the rest is in the thread.</summary>
    public const int QuoteLength = 140;

    /// <summary>"Booking to approve" - table, time, party and guest.</summary>
    public static (string Title, string Body) Approval(
        string locale, string? tableLabel, DateOnly date, TimeOnly at, int partySize, string guestName) =>
        NotificationText.Normalise(locale) switch
        {
            "ru" => ("Бронь ждёт подтверждения",
                     $"{TablePart("ru", tableLabel)} · {Day(date)} {Time(at)} · {partySize} чел. · {guestName}"),
            "en" => ("Booking to approve",
                     $"{TablePart("en", tableLabel)} · {Day(date)} {Time(at)} · {partySize} {(partySize == 1 ? "guest" : "guests")} · {guestName}"),
            _ => ("Ամրագրում՝ հաստատման",
                  $"{TablePart("hy", tableLabel)} · {Day(date)} {Time(at)} · {partySize} հոգի · {guestName}"),
        };

    /// <summary>
    /// "Table 6", or - for a booking made by time only that nobody has seated - that it has no table yet.
    /// </summary>
    private static string TablePart(string locale, string? tableLabel) =>
        (locale, string.IsNullOrWhiteSpace(tableLabel)) switch
        {
            ("ru", true) => "Без столика",
            ("ru", false) => $"Столик {tableLabel}",
            ("en", true) => "No table yet",
            ("en", false) => $"Table {tableLabel}",
            (_, true) => "Առանց սեղանի",
            _ => $"Սեղան {tableLabel}",
        };

    /// <summary>A guest wrote: who and about what, then the words.</summary>
    /// <param name="locale">The device's language.</param>
    /// <param name="subject">"Table 6 · Ani M. · 19:30" or "T-482 · Ani M." - built by the caller.</param>
    /// <param name="text">What they wrote, cut to <see cref="QuoteLength"/>.</param>
    public static (string Title, string Body) GuestMessage(string locale, string subject, string text) =>
        NotificationText.Normalise(locale) switch
        {
            "ru" => ($"Сообщение · {subject}", Quote(text)),
            "en" => ($"Message · {subject}", Quote(text)),
            _ => ($"Նամակ · {subject}", Quote(text)),
        };

    /// <summary>The guest's answer to "are you coming?", in the device's language.</summary>
    /// <param name="locale">The device's language.</param>
    /// <param name="answer">1 Coming, 2 Late, 3 Not coming.</param>
    /// <param name="lateMinutes">With a late answer: by how much.</param>
    public static string Answer(string locale, int answer, int? lateMinutes) =>
        (NotificationText.Normalise(locale), answer) switch
        {
            ("ru", 1) => "Иду",
            ("ru", 2) => $"Опаздываю на {lateMinutes ?? 0} мин",
            ("ru", _) => "Не приду",
            ("en", 1) => "I'm coming",
            ("en", 2) => $"Running {lateMinutes ?? 0} min late",
            ("en", _) => "Not coming",
            (_, 1) => "Գալիս եմ",
            (_, 2) => $"Ուշանում եմ {lateMinutes ?? 0} րոպե",
            _ => "Չեմ գալու",
        };

    /// <summary>The thread's subject for a booking: "Table 6 · Ani M. · 19:30".</summary>
    public static string BookingSubject(string locale, string? tableLabel, string guestName, TimeOnly at) =>
        NotificationText.Normalise(locale) switch
        {
            "ru" => $"{TablePart("ru", tableLabel)} · {guestName} · {Time(at)}",
            "en" => $"{TablePart("en", tableLabel)} · {guestName} · {Time(at)}",
            _ => $"{TablePart("hy", tableLabel)} · {guestName} · {Time(at)}",
        };

    /// <summary>A table is calling: the bill, or a waiter for something.</summary>
    /// <param name="locale">The device's language.</param>
    /// <param name="tableLabel">The table.</param>
    /// <param name="preset">1 Napkins, 2 Water, 3 The bill, 4 Other.</param>
    /// <param name="note">What the guest typed, when they typed something.</param>
    public static (string Title, string Body) TableCall(string locale, string tableLabel, int preset, string? note)
    {
        var language = NotificationText.Normalise(locale);

        var title = (language, preset) switch
        {
            ("ru", 3) => $"Столик {tableLabel} · просит счёт",
            ("ru", _) => $"Столик {tableLabel} · зовёт официанта",
            ("en", 3) => $"Table {tableLabel} · asking for the bill",
            ("en", _) => $"Table {tableLabel} · calling a waiter",
            (_, 3) => $"Սեղան {tableLabel} · հաշիվ",
            _ => $"Սեղան {tableLabel} · կանչում է",
        };

        var body = !string.IsNullOrWhiteSpace(note)
            ? Quote(note)
            : (language, preset) switch
            {
                ("ru", 1) => "Нужны салфетки",
                ("ru", 2) => "Нужна вода",
                ("ru", 3) => "Гости готовы оплатить",
                ("ru", _) => "Гости просят подойти",
                ("en", 1) => "Napkins, please",
                ("en", 2) => "Water, please",
                ("en", 3) => "The guests are ready to pay",
                ("en", _) => "The guests would like a waiter",
                (_, 1) => "Անձեռոցիկներ են խնդրում",
                (_, 2) => "Ջուր են խնդրում",
                (_, 3) => "Հյուրերը պատրաստ են վճարել",
                _ => "Հյուրերը խնդրում են մոտենալ",
            };

        return (title, body);
    }

    /// <summary>A new takeaway order to accept.</summary>
    /// <param name="locale">The device's language.</param>
    /// <param name="code">"T-482".</param>
    /// <param name="guestName">Whose.</param>
    /// <param name="items">How many dishes, counting quantities.</param>
    /// <param name="totalAmd">What it comes to.</param>
    /// <param name="pickupAt">The pickup time asked for; null for "as soon as possible".</param>
    public static (string Title, string Body) Takeaway(
        string locale, string code, string guestName, int items, long totalAmd, TimeOnly? pickupAt) =>
        NotificationText.Normalise(locale) switch
        {
            "ru" => ($"Новый заказ на вынос {code}",
                     $"{guestName} · {items} поз. · {Money(totalAmd)} · {(pickupAt is { } ru ? $"к {Time(ru)}" : "как можно скорее")}"),
            "en" => ($"New takeaway order {code}",
                     $"{guestName} · {items} {(items == 1 ? "item" : "items")} · {Money(totalAmd)} · {(pickupAt is { } en ? $"pickup {Time(en)}" : "as soon as possible")}"),
            _ => ($"Նոր տանելու պատվեր {code}",
                  $"{guestName} · {items} ուտեստ · {Money(totalAmd)} · {(pickupAt is { } hy ? $"{Time(hy)}-ին" : "հնարավորինս շուտ")}"),
        };

    /// <summary>
    /// "Call the guest" - a booking the person took by hand starts in thirty minutes: who, when, how many, where. Never
    /// the number: that is on the card, behind the tap.
    /// </summary>
    public static (string Title, string Body) BookingToCall(
        string locale, string guestName, TimeOnly at, int partySize, string? tableLabel) =>
        NotificationText.Normalise(locale) switch
        {
            "ru" => ($"Позвоните гостю · {guestName}", $"{Time(at)} · {partySize} чел. · {TablePart("ru", tableLabel)}"),
            "en" => ($"Call the guest · {guestName}",
                     $"{Time(at)} · {partySize} {(partySize == 1 ? "guest" : "guests")} · {TablePart("en", tableLabel)}"),
            _ => ($"Զանգեք հյուրին · {guestName}", $"{Time(at)} · {partySize} հոգի · {TablePart("hy", tableLabel)}"),
        };

    /// <summary>The kitchen has an order up for a table.</summary>
    public static (string Title, string Body) KitchenReady(string locale, string tableLabel, int dishes) =>
        NotificationText.Normalise(locale) switch
        {
            "ru" => ($"Готово · столик {tableLabel}", $"{dishes} блюд(а) можно подавать"),
            "en" => ($"Ready · table {tableLabel}", $"{dishes} {(dishes == 1 ? "dish" : "dishes")} ready to serve"),
            _ => ($"Պատրաստ է · սեղան {tableLabel}", $"{dishes} ուտեստ պատրաստ է մատուցման"),
        };

    /// <summary>Cuts a guest's words to <see cref="QuoteLength"/>, with an ellipsis for the cut.</summary>
    public static string Quote(string text)
    {
        var trimmed = text.Trim();

        return trimmed.Length <= QuoteLength ? trimmed : string.Concat(trimmed.AsSpan(0, QuoteLength - 1), "…");
    }

    private static string Time(TimeOnly at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Day(DateOnly date) => date.ToString("dd.MM", CultureInfo.InvariantCulture);

    private static string Money(long amd) =>
        amd.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ') + " ֏";
}
