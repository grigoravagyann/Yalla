using System.Text;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
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
/// <para>
/// <b>One public reply from the venue</b>, written by its owner, general manager or the branch's
/// manager and edited by whoever is on duty - the last writer signs it. The signature is kept as it was
/// written (<see cref="ReplyAuthorName"/>, <see cref="ReplyAuthorRole"/>), so a reply reads the same
/// after its writer is renamed or leaves. A review that is taken down is not answered; a reply already
/// on it goes out of sight with it, and can still be removed.
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

    /// <summary>The venue's reply, or null when it has not answered. At most <see cref="MaxReplyLength"/> characters.</summary>
    public string? ReplyText { get; private set; }

    /// <summary>The full name of the staff member who last wrote the reply, as it was then.</summary>
    public string? ReplyAuthorName { get; private set; }

    /// <summary>Their role when they wrote it: owner, general manager or branch manager.</summary>
    public StaffRole? ReplyAuthorRole { get; private set; }

    /// <summary>Who last wrote the reply. The audit log is the record; this says whose words they are now.</summary>
    public Guid? ReplyByStaffMemberId { get; private set; }

    /// <summary>When the reply was first published. A reply removed and written again starts over.</summary>
    public DateTime? RepliedAtUtc { get; private set; }

    /// <summary>When the reply's text last changed; equal to <see cref="RepliedAtUtc"/> until an edit.</summary>
    public DateTime? ReplyUpdatedAtUtc { get; private set; }

    /// <summary>Whether it is taken down.</summary>
    public bool IsHidden => HiddenAtUtc is not null;

    /// <summary>Whether the venue has answered it.</summary>
    public bool HasReply => ReplyText is not null;

    /// <summary>Whether the reply was changed after it was first published.</summary>
    public bool IsReplyEdited => HasReply && ReplyUpdatedAtUtc != RepliedAtUtc;

    /// <summary>The longest reply, in characters after trimming.</summary>
    public const int MaxReplyLength = FieldLengths.ReviewReply;

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

    /// <summary>
    /// Writes the venue's reply, or replaces its text. The writer signs it, so an edit by somebody else
    /// moves the signature to them.
    /// </summary>
    /// <returns>
    /// False, and nothing touched, when the trimmed text is what is already published - so a form that
    /// re-sends an unchanged reply neither marks it edited nor moves the signature.
    /// </returns>
    /// <exception cref="FieldValidationException">The text is blank or too long, naming <c>text</c>.</exception>
    /// <exception cref="DomainStateException">The review is taken down.</exception>
    public bool Reply(Guid staffMemberId, string authorName, StaffRole authorRole, string? text, DateTime atUtc)
    {
        var checkedText = CheckReply(text);

        if (IsHidden)
        {
            throw new DomainStateException(
                "A hidden review cannot be answered. Put it back first, if it was the venue that hid it.");
        }

        if (string.Equals(checkedText, ReplyText, StringComparison.Ordinal))
        {
            return false;
        }

        atUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

        if (!HasReply)
        {
            RepliedAtUtc = atUtc;
        }

        ReplyText = checkedText;
        ReplyAuthorName = Guard.NotBlank(authorName, nameof(authorName), FieldLengths.PersonName);
        ReplyAuthorRole = authorRole;
        ReplyByStaffMemberId = Guard.NotEmpty(staffMemberId, nameof(staffMemberId));
        ReplyUpdatedAtUtc = atUtc;

        return true;
    }

    /// <summary>Takes the reply down, whether or not the review is shown.</summary>
    /// <returns>False when there was none.</returns>
    public bool RemoveReply()
    {
        if (!HasReply)
        {
            return false;
        }

        ReplyText = null;
        ReplyAuthorName = null;
        ReplyAuthorRole = null;
        ReplyByStaffMemberId = null;
        RepliedAtUtc = null;
        ReplyUpdatedAtUtc = null;

        return true;
    }

    /// <summary>A reply: required, trimmed, at most <see cref="MaxReplyLength"/> characters.</summary>
    /// <exception cref="FieldValidationException">Naming <c>text</c>.</exception>
    public static string CheckReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FieldValidationException(new FieldViolation(
                "text", "Write the reply.", FieldBounds.Required));
        }

        var trimmed = text.Trim();

        return trimmed.Length > MaxReplyLength
            ? throw new FieldValidationException(new FieldViolation(
                "text",
                $"A reply is at most {MaxReplyLength} characters.",
                FieldBounds.Max,
                Max: MaxReplyLength,
                Value: trimmed.Length))
            : trimmed;
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
