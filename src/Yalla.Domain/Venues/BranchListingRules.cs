using Yalla.Domain.Common;

namespace Yalla.Domain.Venues;

/// <summary>
/// What a branch says about itself on the diner app's browse screens, and the rules it is held to.
/// </summary>
public static class BranchListingRules
{
    public const int MinPriceLevel = 1;

    public const int MaxPriceLevel = 4;

    /// <summary>
    /// The amenity keys the diner app has words for, under <c>place.amenity.*</c>. A closed list: a
    /// key the app cannot translate would render as a raw string in three languages.
    /// </summary>
    public static readonly IReadOnlyList<string> AmenityKeys =
        ["outdoorSeating", "wifi", "parking", "cardPayment", "vegan"];

    /// <summary>Checks and normalises every listing field at once.</summary>
    /// <exception cref="FieldValidationException">Every field that broke a rule, named.</exception>
    public static BranchListing Normalise(
        string? cuisine,
        string? about,
        int? priceLevel,
        string? websiteUrl,
        IEnumerable<string>? amenities)
    {
        var violations = new List<FieldViolation>();

        var cuisineText = Optional(cuisine, "cuisine", FieldLengths.Cuisine, violations);
        var aboutText = Optional(about, "about", FieldLengths.Description, violations);

        if (priceLevel is < MinPriceLevel or > MaxPriceLevel)
        {
            violations.Add(new FieldViolation(
                "priceLevel",
                $"The price level is {MinPriceLevel} to {MaxPriceLevel}.",
                FieldBounds.Range,
                MinPriceLevel,
                MaxPriceLevel,
                priceLevel));
        }

        var website = Optional(websiteUrl, "websiteUrl", FieldLengths.Url, violations);

        if (website is not null
            && (!Uri.TryCreate(website, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            violations.Add(new FieldViolation("websiteUrl", "The website must be an http or https address."));
        }

        var keys = new List<string>();

        foreach (var raw in amenities ?? [])
        {
            var known = AmenityKeys.FirstOrDefault(k => string.Equals(k, raw?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (known is null)
            {
                violations.Add(new FieldViolation(
                    "amenities", $"'{raw}' is not an amenity. Known: {string.Join(", ", AmenityKeys)}.", Value: raw));
            }
            else if (!keys.Contains(known))
            {
                keys.Add(known);
            }
        }

        return violations.Count > 0
            ? throw new FieldValidationException(violations)
            : new BranchListing(cuisineText, aboutText, priceLevel, website, keys);
    }

    /// <summary>
    /// Where a table sits on the branch's hero photo, as fractions of its width and height.
    /// </summary>
    /// <remarks>
    /// Both or neither: a table with one coordinate has no place on the photo, and a marker drawn at
    /// the left edge because the other half was forgotten is a table the diner taps and cannot find.
    /// </remarks>
    /// <exception cref="FieldValidationException">One without the other, or a value outside 0-1.</exception>
    public static (double? X, double? Y) PhotoPosition(double? photoX, double? photoY, string label)
    {
        var violations = new List<FieldViolation>();

        if (photoX.HasValue != photoY.HasValue)
        {
            violations.Add(new FieldViolation(
                photoX.HasValue ? "photoY" : "photoX",
                $"Table {label} needs both photo coordinates or neither.",
                FieldBounds.Required));
        }

        foreach (var (field, value) in new[] { ("photoX", photoX), ("photoY", photoY) })
        {
            if (value is { } v && (double.IsNaN(v) || v < 0d || v > 1d))
            {
                violations.Add(new FieldViolation(
                    field, $"Table {label}'s photo coordinates are fractions from 0 to 1.", FieldBounds.Range, 0d, 1d, v));
            }
        }

        return violations.Count > 0
            ? throw new FieldValidationException(violations)
            : (photoX, photoY);
    }

    /// <summary>Great-circle distance in kilometres, rounded to 0.1.</summary>
    public static double DistanceKm(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        const double earthRadiusKm = 6371.0088;

        static double Radians(double degrees) => degrees * Math.PI / 180d;

        var dLat = Radians(toLatitude - fromLatitude);
        var dLon = Radians(toLongitude - fromLongitude);
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
                + Math.Cos(Radians(fromLatitude)) * Math.Cos(Radians(toLatitude)) * Math.Pow(Math.Sin(dLon / 2), 2);

        return Math.Round(2 * earthRadiusKm * Math.Asin(Math.Min(1d, Math.Sqrt(a))), 1);
    }

    private static string? Optional(string? value, string field, int maxLength, List<FieldViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            violations.Add(new FieldViolation(
                field, $"At most {maxLength} characters.", FieldBounds.Max, Max: maxLength, Value: trimmed.Length));
        }

        return trimmed;
    }
}

/// <summary>A branch's listing fields after <see cref="BranchListingRules.Normalise"/>.</summary>
public sealed record BranchListing(
    string? Cuisine,
    string? About,
    int? PriceLevel,
    string? WebsiteUrl,
    IReadOnlyList<string> Amenities);

/// <summary>
/// The two content badges on a browse card, derived from data and never stored.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>new</c></b>: the branch row was created in the last <see cref="NewForDays"/> days.
/// </para>
/// <para>
/// <b><c>popular</c></b>: in the last <see cref="PopularWindowDays"/> days the branch seated at least
/// <see cref="PopularMinSittings"/> parties (walk-ins and bookings alike - every <c>TableSession</c>),
/// <b>or</b> it has at least <see cref="PopularMinReviews"/> reviews averaging
/// <see cref="PopularMinAverageRating"/> or better. Sittings are the room actually filling; the
/// review arm lets a small place that people love earn it without a big floor.
/// </para>
/// <para>
/// Absolute thresholds rather than "top N of the city", so a branch's badge does not flicker because
/// a different venue had a busy weekend.
/// </para>
/// </remarks>
public static class BranchBadgeRules
{
    public const string Popular = "popular";

    public const string New = "new";

    public const int NewForDays = 30;

    public const int PopularWindowDays = 30;

    public const int PopularMinSittings = 20;

    public const int PopularMinReviews = 5;

    public const double PopularMinAverageRating = 4.5;

    public static bool IsNew(DateTime createdAtUtc, DateTime nowUtc) =>
        createdAtUtc <= nowUtc && nowUtc - createdAtUtc < TimeSpan.FromDays(NewForDays);

    public static bool IsPopular(int sittingsInWindow, int reviewCount, double? averageRating) =>
        sittingsInWindow >= PopularMinSittings
        || (reviewCount >= PopularMinReviews && averageRating >= PopularMinAverageRating);

    /// <summary>The badges in display order: popular first.</summary>
    public static IReadOnlyList<string> For(
        DateTime createdAtUtc, DateTime nowUtc, int sittingsInWindow, int reviewCount, double? averageRating)
    {
        var badges = new List<string>(2);

        if (IsPopular(sittingsInWindow, reviewCount, averageRating))
        {
            badges.Add(Popular);
        }

        if (IsNew(createdAtUtc, nowUtc))
        {
            badges.Add(New);
        }

        return badges;
    }

    /// <summary>Average to one decimal, or null when nobody has rated it - not zero, which reads as terrible.</summary>
    public static double? AverageRating(int reviewCount, long ratingSum) =>
        reviewCount <= 0 ? null : Math.Round((double)ratingSum / reviewCount, 1, MidpointRounding.AwayFromZero);
}
