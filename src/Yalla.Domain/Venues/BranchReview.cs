using Yalla.Domain.Common;
using Yalla.Domain.Identity;

namespace Yalla.Domain.Venues;

/// <summary>
/// One diner's rating of one branch, with an optional line of text.
/// </summary>
/// <remarks>
/// <para>
/// <b>One per diner per branch, revised rather than repeated.</b> A unique index on
/// <c>(BranchId, DinerUserId)</c> holds it; a diner who changes their mind edits the review they
/// already wrote, so one enthusiastic regular cannot outweigh a room of quieter ones by posting
/// ten times.
/// </para>
/// <para>
/// Written only by an account whose phone number has been proved - the service checks the stored
/// row, the same gate a booking has - so a throwaway registration cannot flood a competitor with
/// one-star reviews.
/// </para>
/// </remarks>
public sealed class BranchReview : Entity
{
    public const int MinRating = 1;

    public const int MaxRating = 5;

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public Guid DinerUserId { get; private set; }

    public DinerUser DinerUser { get; private set; } = null!;

    /// <summary>Whole stars, <see cref="MinRating"/> to <see cref="MaxRating"/>.</summary>
    public int Rating { get; private set; }

    /// <summary>Optional, at most <see cref="FieldLengths.ReviewText"/> characters. Blank is stored as null.</summary>
    public string? Text { get; private set; }

    /// <summary>When it was last written. Equal to <see cref="Entity.CreatedAtUtc"/> until revised.</summary>
    public DateTime UpdatedAtUtc { get; private set; }

    private BranchReview()
    {
    }

    public BranchReview(Guid branchId, Guid dinerUserId, int rating, string? text, DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DinerUserId = Guard.NotEmpty(dinerUserId, nameof(dinerUserId));
        (Rating, Text) = Check(rating, text);
        UpdatedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        StampCreatedAt(atUtc);
    }

    /// <summary>Replaces the rating and the text together. Blank text clears it.</summary>
    public void Revise(int rating, string? text, DateTime atUtc)
    {
        (Rating, Text) = Check(rating, text);
        UpdatedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
    }

    /// <summary>Both fields checked at once, so a request breaking both hears about both.</summary>
    /// <exception cref="FieldValidationException">A rating outside 1-5 or text over the limit.</exception>
    public static (int Rating, string? Text) Check(int rating, string? text)
    {
        var violations = new List<FieldViolation>();

        if (rating is < MinRating or > MaxRating)
        {
            violations.Add(new FieldViolation(
                "rating", $"A rating is {MinRating} to {MaxRating} stars.", FieldBounds.Range, MinRating, MaxRating, rating));
        }

        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        if (trimmed is { Length: > FieldLengths.ReviewText })
        {
            violations.Add(new FieldViolation(
                "text",
                $"A review is at most {FieldLengths.ReviewText} characters.",
                FieldBounds.Max,
                Max: FieldLengths.ReviewText,
                Value: trimmed.Length));
        }

        return violations.Count > 0
            ? throw new FieldValidationException(violations)
            : (rating, trimmed);
    }

    /// <summary>
    /// The name a review is published under: the first name and the initial of the last,
    /// "Anahit S.", or "Yalla diner" when the account has no name.
    /// </summary>
    /// <remarks>
    /// A public page shows this to anybody. A full name next to "the waiter was rude" is more than a
    /// diner agreed to publish by leaving a star rating.
    /// </remarks>
    public static string PublicAuthorName(string? displayName)
    {
        var parts = (displayName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            0 => "Yalla diner",
            1 => parts[0],
            _ => $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}.",
        };
    }
}
