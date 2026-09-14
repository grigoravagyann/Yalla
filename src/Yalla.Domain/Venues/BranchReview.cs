using System.Text;
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
/// row, the same gate a booking has - and only after a visit to the branch in the last
/// <see cref="VisitWindowDays"/> days (K8), so a throwaway registration cannot flood a competitor
/// with one-star reviews of a place it never sat in.
/// </para>
/// <para>
/// <b>Hidden, never deleted, by moderation.</b> A platform admin or the venue's own manager can take
/// a review down with a reason. A hidden review stays on the row - the diner still sees their own,
/// marked hidden - and is left out of every public list and every derived number. Which tier hid it
/// is kept, because a venue may not put back what the platform took down.
/// </para>
/// </remarks>
public sealed class BranchReview : Entity
{
    public const int MinRating = 1;

    public const int MaxRating = 5;

    /// <summary>How far back a visit counts toward being allowed to write a first review.</summary>
    public const int VisitWindowDays = 180;

    /// <summary>What a review is published under when the name cannot be shown.</summary>
    public const string AnonymousAuthorName = "Yalla diner";

    /// <summary>The longest first name a review is published under.</summary>
    public const int MaxAuthorNameLength = 24;

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public Guid DinerUserId { get; private set; }

    public DinerUser DinerUser { get; private set; } = null!;

    /// <summary>Whole stars, <see cref="MinRating"/> to <see cref="MaxRating"/>.</summary>
    public int Rating { get; private set; }

    /// <summary>Optional, at most <see cref="FieldLengths.ReviewText"/> characters. Blank is stored as null.</summary>
    public string? Text { get; private set; }

    /// <summary>
    /// When the rating or the text last changed. Equal to <see cref="Entity.CreatedAtUtc"/> until a
    /// revision actually changes something - re-sending the same review does not move it.
    /// </summary>
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>When it was taken down, or null while it is published.</summary>
    public DateTime? HiddenAtUtc { get; private set; }

    /// <summary>Why, in the moderator's words. At most <see cref="FieldLengths.Reason"/> characters.</summary>
    public string? HiddenReason { get; private set; }

    /// <summary>The staff member who took it down: a platform admin, or a manager or owner of the venue.</summary>
    public Guid? HiddenByStaffMemberId { get; private set; }

    /// <summary>
    /// Whether the platform took it down rather than the venue. A venue cannot put back a review the
    /// platform hid; the platform can put back one the venue hid.
    /// </summary>
    public bool HiddenByPlatform { get; private set; }

    /// <summary>Whether it is taken down.</summary>
    public bool IsHidden => HiddenAtUtc is not null;

    /// <summary>Whether a revision has changed it since it was first written.</summary>
    public bool IsEdited => UpdatedAtUtc != CreatedAtUtc;

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

    /// <summary>
    /// Replaces the rating and the text together. Blank text clears it.
    /// </summary>
    /// <returns>
    /// False, and nothing touched, when the rating and the trimmed text are what is already stored -
    /// so a form that re-sends an unchanged review neither marks it edited nor moves its date.
    /// </returns>
    public bool Revise(int rating, string? text, DateTime atUtc)
    {
        var (checkedRating, checkedText) = Check(rating, text);

        if (checkedRating == Rating && string.Equals(checkedText, Text, StringComparison.Ordinal))
        {
            return false;
        }

        Rating = checkedRating;
        Text = checkedText;
        UpdatedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

        return true;
    }

    /// <summary>Takes the review down, recording who, which tier and why.</summary>
    /// <exception cref="FieldValidationException">The reason is blank or too long.</exception>
    public void Hide(Guid staffMemberId, bool byPlatform, string? reason, DateTime atUtc)
    {
        HiddenReason = CheckHideReason(reason);
        HiddenByStaffMemberId = Guard.NotEmpty(staffMemberId, nameof(staffMemberId));
        HiddenByPlatform = byPlatform;
        HiddenAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
    }

    /// <summary>Puts the review back. Who may is the service's question, not this one's.</summary>
    public void Unhide()
    {
        HiddenAtUtc = null;
        HiddenReason = null;
        HiddenByStaffMemberId = null;
        HiddenByPlatform = false;
    }

    /// <summary>A takedown reason: required, trimmed, at most <see cref="FieldLengths.Reason"/> characters.</summary>
    /// <exception cref="FieldValidationException">Naming <c>reason</c>.</exception>
    public static string CheckHideReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new FieldValidationException(new FieldViolation(
                "reason", "Say why the review is being hidden.", FieldBounds.Required));
        }

        var trimmed = reason.Trim();

        return trimmed.Length > FieldLengths.Reason
            ? throw new FieldValidationException(new FieldViolation(
                "reason",
                $"A reason is at most {FieldLengths.Reason} characters.",
                FieldBounds.Max,
                Max: FieldLengths.Reason,
                Value: trimmed.Length))
            : trimmed;
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
    /// The name a review is published under: "Anahit S.", "Narek", or "Yalla diner".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A public page shows this to anybody. A full name next to "the waiter was rude" is more than a
    /// diner agreed to publish by leaving a star rating - and a display name is free text, so it is
    /// also where people type their email address or phone number.
    /// </para>
    /// <list type="number">
    /// <item>Take the first word of the display name.</item>
    /// <item>If it contains <c>@</c>, a digit, <c>/</c> or <c>www.</c>, publish "Yalla diner".</item>
    /// <item>Otherwise keep only letters (any script) and hyphens, at most
    /// <see cref="MaxAuthorNameLength"/> of them; nothing left is "Yalla diner" too.</item>
    /// <item>Add the second word's initial and a full stop, only when that word starts with a letter.</item>
    /// </list>
    /// </remarks>
    public static string PublicAuthorName(string? displayName)
    {
        var words = (displayName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return AnonymousAuthorName;
        }

        var first = words[0];

        if (first.Contains('@')
            || first.Contains('/')
            || first.Contains("www.", StringComparison.OrdinalIgnoreCase)
            || first.Any(char.IsDigit))
        {
            return AnonymousAuthorName;
        }

        var kept = new StringBuilder();
        var count = 0;

        foreach (var rune in first.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune) && rune.Value != '-')
            {
                continue;
            }

            if (count == MaxAuthorNameLength)
            {
                break;
            }

            kept.Append(rune.ToString());
            count++;
        }

        var name = kept.ToString().Trim('-');

        if (name.Length == 0)
        {
            return AnonymousAuthorName;
        }

        return words.Length > 1
               && Rune.TryGetRuneAt(words[1], 0, out var initial)
               && Rune.IsLetter(initial)
            ? $"{name} {Rune.ToUpperInvariant(initial)}."
            : name;
    }
}
