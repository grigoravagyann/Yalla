namespace Yalla.Domain.Common;

/// <summary>URL segment used to address a venue or branch from a client.</summary>
internal static class SlugText
{
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

        return slug;
    }
}
