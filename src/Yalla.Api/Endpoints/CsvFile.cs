using System.Text;

namespace Yalla.Api.Endpoints;

/// <summary>
/// A CSV download, written one way for every export: the reports, the History, the staff results. See
/// <c>docs/reports.md</c> §7.
/// </summary>
/// <remarks>
/// <para>
/// A UTF-8 byte-order mark, deliberately. Excel opens a BOM-less UTF-8 CSV in the system code page, which turns every
/// Armenian name into mojibake - and the names are the part somebody reads.
/// </para>
/// <para>
/// Fields follow RFC 4180: quoted when they contain a comma, a quote or a newline, inner quotes doubled. A menu item
/// called "Khachapuri, Adjarian" is not hypothetical.
/// </para>
/// <para>
/// <b>Formula guard</b>, when asked: a text cell starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a
/// carriage return gets a leading <c>'</c>, so a reason or a dish name typed by staff is never run as a formula by the
/// spreadsheet that opens the file - the console's own CSV rule. Numbers are written by the caller as plain digits and
/// are never guarded; a negative amount is not text.
/// </para>
/// </remarks>
internal static class CsvFile
{
    private const string MediaType = "text/csv; charset=utf-8";

    public static IResult Result(
        string fileName, IReadOnlyList<string> header, IEnumerable<string[]> rows, bool guardFormulas = false)
    {
        var csv = new StringBuilder();

        csv.AppendLine(string.Join(',', header.Select(h => Escape(h, guard: false))));

        foreach (var row in rows)
        {
            csv.AppendLine(string.Join(',', row.Select(v => Escape(v, guardFormulas && !IsNumber(v)))));
        }

        var text = csv.ToString();
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = new byte[preamble.Length + Encoding.UTF8.GetByteCount(text)];

        preamble.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, preamble.Length);

        return Results.File(bytes, MediaType, $"{fileName}.csv");
    }

    /// <summary>One field, quoted when RFC 4180 says so, and guarded against formulas when asked.</summary>
    internal static string Escape(string value, bool guard)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (guard && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.Contains(',', StringComparison.Ordinal)
               || value.Contains('"', StringComparison.Ordinal)
               || value.Contains('\n', StringComparison.Ordinal)
               || value.Contains('\r', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static bool IsNumber(string value) =>
        long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _);
}
