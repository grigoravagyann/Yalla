using Yalla.Domain.Common;

namespace Yalla.Domain.Venues;

/// <summary>
/// One diner flagging somebody else's review for the venue and the platform to look at.
/// </summary>
/// <remarks>
/// <para>
/// <b>A report changes nothing on its own.</b> It is a signal: the venue's moderation list counts
/// them and can filter to the reported reviews, and a person decides whether to hide one. Letting a
/// count of reports take a review down would hand every competitor a button that deletes bad news.
/// </para>
/// <para>
/// <b>One per diner per review</b>, by a unique index on <c>(ReviewId, DinerUserId)</c>, so the
/// count is a count of people rather than of taps. A repeat is answered as success and writes
/// nothing. The reviews are the diner's own statements about a place, so any diner account may
/// report one - a proved number is not needed to say "this looks like spam".
/// </para>
/// </remarks>
public sealed class BranchReviewReport : Entity
{
    public Guid ReviewId { get; private set; }

    public BranchReview Review { get; private set; } = null!;

    /// <summary>The diner who reported it.</summary>
    public Guid DinerUserId { get; private set; }

    /// <summary>One of <see cref="ReviewReportReasons"/>.</summary>
    public string Reason { get; private set; } = null!;

    /// <summary>Optional detail, at most <see cref="FieldLengths.Reason"/> characters.</summary>
    public string? Note { get; private set; }

    private BranchReviewReport()
    {
    }

    public BranchReviewReport(Guid reviewId, Guid dinerUserId, string? reason, string? note, DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        ReviewId = Guard.NotEmpty(reviewId, nameof(reviewId));
        DinerUserId = Guard.NotEmpty(dinerUserId, nameof(dinerUserId));
        (Reason, Note) = Check(reason, note);
        StampCreatedAt(atUtc);
    }

    /// <summary>Both fields checked at once, so a request breaking both hears about both.</summary>
    /// <exception cref="FieldValidationException">
    /// <c>reason</c> missing (bound <c>required</c>) or not one of the five (bound <c>range</c>);
    /// <c>note</c> over the limit (bound <c>max</c>).
    /// </exception>
    public static (string Reason, string? Note) Check(string? reason, string? note)
    {
        var violations = new List<FieldViolation>();
        var normalised = reason?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(normalised))
        {
            violations.Add(new FieldViolation(
                "reason", "Say why the review is being reported.", FieldBounds.Required));
        }
        else if (!ReviewReportReasons.All.Contains(normalised, StringComparer.Ordinal))
        {
            violations.Add(new FieldViolation(
                "reason",
                $"A report reason is one of: {string.Join(", ", ReviewReportReasons.All)}.",
                FieldBounds.Range,
                Value: reason));
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        if (trimmedNote is { Length: > FieldLengths.Reason })
        {
            violations.Add(new FieldViolation(
                "note",
                $"A note is at most {FieldLengths.Reason} characters.",
                FieldBounds.Max,
                Max: FieldLengths.Reason,
                Value: trimmedNote.Length));
        }

        return violations.Count > 0
            ? throw new FieldValidationException(violations)
            : (normalised!, trimmedNote);
    }
}

/// <summary>Why a diner reported a review. Stable slugs; the app sends them and shows its own words.</summary>
public static class ReviewReportReasons
{
    public const string Spam = "spam";

    public const string Offensive = "offensive";

    public const string NotAVisit = "not-a-visit";

    public const string PersonalInfo = "personal-info";

    public const string Other = "other";

    /// <summary>The column width. The longest slug is well inside it.</summary>
    public const int MaxLength = 32;

    /// <summary>Every accepted reason, in the order the app lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Spam, Offensive, NotAVisit, PersonalInfo, Other];
}
