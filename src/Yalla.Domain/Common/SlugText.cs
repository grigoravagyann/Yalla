namespace Yalla.Domain.Common;

/// <summary>URL segment used to address a venue or branch from a client.</summary>
internal static class SlugText
{
    /// <summary>
    /// Words a venue or branch may not be called, because the web app already answers to them.
    /// </summary>
    /// <remarks>
    /// The web app decides which bundle to boot from the first path segment: these are the
    /// console's own pages and asset folders, and everything else is taken to be a venue. A venue
    /// slugged <c>reset-password</c> would be a public page nobody could ever reach, sitting under
    /// the address the password-reset link points at. The list mirrors
    /// <c>RESERVED_FIRST_SEGMENTS</c> in <c>apps/web/src/publicRoutes.ts</c>; a word added there
    /// is added here, or the two sides disagree about who owns the address.
    /// <para>
    /// <b>One word is the public page's, not the console's</b>, and so is the one exception to that
    /// mirror: <c>booking</c>, the first segment of the manage link <c>/booking/{token}</c> that
    /// <c>PublicWeb:ManageBookingUrlTemplate</c> writes into every web booking's reminder. The public
    /// bundle serves it - so it must <i>not</i> join <c>RESERVED_FIRST_SEGMENTS</c>, which hands a path
    /// to the console - but no venue may take it either.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.Ordinal)
    {
        "assets",

        // The public manage link's path (/booking/{token}), served by the public page, not the console.
        // A venue slugged "booking" would sit under every reminder's link and could never be reached.
        "booking",
        "dev",
        "fonts",
        "platform",
        "reset-password",
        "sign-in",
        "staff",
        "venue",
    };

    public static string Normalise(string? value, string paramName)
    {
        var slug = Guard.NotBlank(value, paramName, FieldLengths.Slug).ToLowerInvariant();

        foreach (var c in slug)
        {
            var allowed = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-';
            if (!allowed)
            {
                throw new ArgumentException(
                    "A slug may contain only lowercase ASCII letters, digits and hyphens.",
                    paramName);
            }
        }

        if (ReservedSlugs.Contains(slug))
        {
            throw new ArgumentException(
                $"'{slug}' is an address the web app itself answers to, so it cannot name a venue or branch.",
                paramName);
        }

        return slug;
    }
}
