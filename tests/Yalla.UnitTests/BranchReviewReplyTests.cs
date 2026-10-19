using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The venue's one public reply to a review: written, edited by whoever is on duty, removed, and never
/// written to a review that is taken down.
/// </summary>
public class BranchReviewReplyTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid OwnerId = Guid.NewGuid();

    private static BranchReview Review() => new(Guid.NewGuid(), Guid.NewGuid(), 3, "Slow on Friday.", Now.AddDays(-1));

    [Fact]
    public void A_first_reply_is_trimmed_signed_and_dated()
    {
        var review = Review();

        Assert.True(review.Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, "  Thank you.  ", Now));

        Assert.True(review.HasReply);
        Assert.Equal("Thank you.", review.ReplyText);
        Assert.Equal("Aram Hakobyan", review.ReplyAuthorName);
        Assert.Equal(StaffRole.Owner, review.ReplyAuthorRole);
        Assert.Equal(OwnerId, review.ReplyByStaffMemberId);
        Assert.Equal(Now, review.RepliedAtUtc);
        Assert.Equal(Now, review.ReplyUpdatedAtUtc);
        Assert.False(review.IsReplyEdited);
    }

    [Fact]
    public void An_edit_keeps_the_first_date_and_the_last_writer_signs_it()
    {
        var review = Review();
        review.Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, "Thank you.", Now);

        var gmId = Guid.NewGuid();
        Assert.True(review.Reply(gmId, "Lilit Sargsyan", StaffRole.GeneralManager, "Thank you, Arthur.", Now.AddHours(1)));

        Assert.Equal("Thank you, Arthur.", review.ReplyText);
        Assert.Equal("Lilit Sargsyan", review.ReplyAuthorName);
        Assert.Equal(StaffRole.GeneralManager, review.ReplyAuthorRole);
        Assert.Equal(gmId, review.ReplyByStaffMemberId);
        Assert.Equal(Now, review.RepliedAtUtc);
        Assert.Equal(Now.AddHours(1), review.ReplyUpdatedAtUtc);
        Assert.True(review.IsReplyEdited);
    }

    [Fact]
    public void The_same_text_again_changes_nothing_not_even_the_signature()
    {
        var review = Review();
        review.Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, "Thank you.", Now);

        Assert.False(review.Reply(Guid.NewGuid(), "Davit Grigoryan", StaffRole.Manager, " Thank you. ", Now.AddHours(1)));

        Assert.Equal("Aram Hakobyan", review.ReplyAuthorName);
        Assert.Equal(Now, review.ReplyUpdatedAtUtc);
    }

    [Theory]
    [InlineData(null, FieldBounds.Required)]
    [InlineData("   ", FieldBounds.Required)]
    public void A_blank_reply_is_refused_as_required(string? text, string bound)
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => Review().Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, text, Now));

        Assert.Equal("text", refused.Field);
        Assert.Equal(bound, Assert.Single(refused.Violations).Bound);
    }

    [Theory]
    [InlineData("\u200B")]
    [InlineData("\u200B\u200C\u200D")]
    [InlineData(" \uFEFF \u2060 ")]
    [InlineData("\u3164\u2800")]
    [InlineData("\u00AD\u180E")]
    public void A_reply_of_invisible_characters_only_is_blank(string text)
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => Review().Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, text, Now));

        Assert.Equal(FieldBounds.Required, Assert.Single(refused.Violations).Bound);
    }

    [Fact]
    public void Invisible_characters_beside_words_are_kept_as_written()
    {
        var review = Review();

        Assert.True(review.Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, "Thank\u200Byou", Now));
        Assert.Equal("Thank\u200Byou", review.ReplyText);
    }

    [Theory]
    [InlineData("Aram Hakobyan", "Aram H.")]
    [InlineData("Լիլիթ Սարգսյան", "Լիլիթ Ս.")]
    [InlineData("Founder", "Founder")]
    [InlineData("General Manager", "General M.")]
    public void The_public_signs_a_reply_with_a_first_name_and_an_initial(string fullName, string shown)
    {
        Assert.Equal(shown, BranchReview.PublicReplyAuthorName(fullName));
    }

    [Theory]
    [InlineData("aram@example.am Hakobyan")]
    [InlineData("+37491000000")]
    [InlineData("   ")]
    public void A_name_that_looks_like_contact_details_is_not_published_at_all(string fullName)
    {
        // Not "Yalla diner": that is a diner's stand-in, and a reply is the venue's. The role signs it alone.
        Assert.Null(BranchReview.PublicReplyAuthorName(fullName));
    }

    [Fact]
    public void A_reply_over_the_limit_is_refused_and_the_limit_itself_is_fine()
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => Review().Reply(OwnerId, "A", StaffRole.Owner, new string('x', BranchReview.MaxReplyLength + 1), Now));

        var violation = Assert.Single(refused.Violations);
        Assert.Equal(FieldBounds.Max, violation.Bound);
        Assert.Equal(BranchReview.MaxReplyLength, violation.Max);

        Assert.True(Review().Reply(OwnerId, "A", StaffRole.Owner, new string('x', BranchReview.MaxReplyLength), Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_hidden_review_is_not_answered(bool byPlatform)
    {
        var review = Review();
        review.Hide(Guid.NewGuid(), byPlatform, "Offensive.", Now);

        Assert.Throws<DomainStateException>(() => review.Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, "Thank you.", Now));
        Assert.False(review.HasReply);
    }

    [Fact]
    public void Removing_clears_every_part_and_removing_nothing_is_a_no_op()
    {
        var review = Review();
        review.Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, "Thank you.", Now);

        Assert.True(review.RemoveReply());

        Assert.False(review.HasReply);
        Assert.Null(review.ReplyText);
        Assert.Null(review.ReplyAuthorName);
        Assert.Null(review.ReplyAuthorRole);
        Assert.Null(review.ReplyByStaffMemberId);
        Assert.Null(review.RepliedAtUtc);
        Assert.Null(review.ReplyUpdatedAtUtc);

        Assert.False(review.RemoveReply());
    }

    [Fact]
    public void A_reply_already_written_can_be_removed_after_the_review_is_hidden()
    {
        var review = Review();
        review.Reply(OwnerId, "Aram Hakobyan", StaffRole.Owner, "Thank you.", Now);
        review.Hide(Guid.NewGuid(), byPlatform: true, "Offensive.", Now.AddHours(1));

        Assert.True(review.RemoveReply());
    }
}
