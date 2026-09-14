using Yalla.Application.Diners;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The rules behind the diner app's browse data: review bounds, listing fields, photo markers,
/// badges, distance, and how the kitchen rail maps to the app's order statuses.
/// </summary>
public class BranchListingRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ reviews

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void A_rating_from_one_to_five_is_accepted(int rating)
    {
        var review = new BranchReview(Guid.NewGuid(), Guid.NewGuid(), rating, "  Warm lavash.  ", Now);

        Assert.Equal(rating, review.Rating);
        Assert.Equal("Warm lavash.", review.Text);
        Assert.Equal(Now, review.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void A_rating_outside_one_to_five_is_refused_naming_the_field(int rating)
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => new BranchReview(Guid.NewGuid(), Guid.NewGuid(), rating, null, Now));

        Assert.Equal("rating", refused.Field);
    }

    [Fact]
    public void Review_text_over_a_thousand_characters_is_refused_and_both_bad_fields_are_reported()
    {
        var refused = Assert.Throws<FieldValidationException>(() => BranchReview.Check(9, new string('a', 1001)));

        Assert.Equal(["rating", "text"], refused.Violations.Select(v => v.Field));
    }

    [Fact]
    public void Blank_review_text_is_stored_as_none_and_a_revision_replaces_both_fields()
    {
        var review = new BranchReview(Guid.NewGuid(), Guid.NewGuid(), 2, "Slow.", Now);

        review.Revise(5, "   ", Now.AddDays(1));

        Assert.Equal(5, review.Rating);
        Assert.Null(review.Text);
        Assert.Equal(Now, review.CreatedAtUtc);
        Assert.Equal(Now.AddDays(1), review.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("Anahit Sargsyan", "Anahit S.")]
    [InlineData("Անահիտ Սարգսյան", "Անահիտ Ս.")]
    [InlineData("Анаит Саргсян", "Анаит С.")]
    [InlineData("  marco   de  tomasi ", "marco D.")]
    [InlineData("Narek", "Narek")]
    [InlineData("Jean-Luc Picard", "Jean-Luc P.")]
    [InlineData("O'Brien Smith", "OBrien S.")]
    [InlineData("Anahit 5-stars", "Anahit")]
    [InlineData("Abcdefghijklmnopqrstuvwxyzabc Zed", "Abcdefghijklmnopqrstuvwx Z.")]
    [InlineData("ani@mail.am", "Yalla diner")]
    [InlineData("+374 91 123456", "Yalla diner")]
    [InlineData("www.example.am", "Yalla diner")]
    [InlineData("instagram.com/ani", "Yalla diner")]
    [InlineData("Ani2 Sargsyan", "Yalla diner")]
    [InlineData("!!! ???", "Yalla diner")]
    [InlineData("", "Yalla diner")]
    [InlineData(null, "Yalla diner")]
    public void A_review_is_published_under_a_first_name_and_an_initial_and_never_contact_details(
        string? displayName, string expected)
    {
        Assert.Equal(expected, BranchReview.PublicAuthorName(displayName));
    }

    [Fact]
    public void Revising_to_the_same_rating_and_text_changes_nothing()
    {
        var review = new BranchReview(Guid.NewGuid(), Guid.NewGuid(), 4, "Good coffee.", Now);

        Assert.False(review.Revise(4, "  Good coffee. ", Now.AddDays(1)));
        Assert.Equal(Now, review.UpdatedAtUtc);
        Assert.False(review.IsEdited);

        Assert.True(review.Revise(3, "Good coffee.", Now.AddDays(2)));
        Assert.Equal(Now.AddDays(2), review.UpdatedAtUtc);
        Assert.True(review.IsEdited);
    }

    [Fact]
    public void A_takedown_needs_a_reason_and_putting_it_back_clears_who_and_why()
    {
        var review = new BranchReview(Guid.NewGuid(), Guid.NewGuid(), 1, "Awful.", Now);
        var moderator = Guid.NewGuid();

        Assert.Equal(
            "reason",
            Assert.Throws<FieldValidationException>(() => review.Hide(moderator, byPlatform: false, "  ", Now)).Field);
        Assert.Equal(
            FieldBounds.Max,
            Assert.Throws<FieldValidationException>(() => review.Hide(moderator, false, new string('r', 501), Now)).Violations[0].Bound);
        Assert.False(review.IsHidden);

        review.Hide(moderator, byPlatform: true, " Personal attack. ", Now);
        Assert.True(review.IsHidden);
        Assert.True(review.HiddenByPlatform);
        Assert.Equal("Personal attack.", review.HiddenReason);
        Assert.Equal(moderator, review.HiddenByStaffMemberId);

        review.Unhide();
        Assert.False(review.IsHidden);
        Assert.False(review.HiddenByPlatform);
        Assert.Null(review.HiddenReason);
        Assert.Null(review.HiddenByStaffMemberId);
    }

    [Theory]
    [InlineData("spam", null, null)]
    [InlineData(" Personal-Info ", "Their phone number.", null)]
    [InlineData("rude", null, "reason")]
    [InlineData(null, "No reason.", "reason")]
    public void A_report_reason_is_one_of_five_and_its_note_is_short(string? reason, string? note, string? refusedField)
    {
        if (refusedField is null)
        {
            var (checkedReason, checkedNote) = BranchReviewReport.Check(reason, note);

            Assert.Contains(checkedReason, ReviewReportReasons.All);
            Assert.Equal(note, checkedNote);
        }
        else
        {
            Assert.Equal(refusedField, Assert.Throws<FieldValidationException>(() => BranchReviewReport.Check(reason, note)).Field);
        }

        Assert.Equal(
            "note",
            Assert.Throws<FieldValidationException>(() => BranchReviewReport.Check("other", new string('n', 501))).Field);
    }

    // ------------------------------------------------------------ listing

    [Fact]
    public void Listing_fields_are_trimmed_amenities_matched_case_insensitively_and_deduplicated()
    {
        var listing = BranchListingRules.Normalise(
            " Armenian ", "  ", 2, "https://thegreentable.am", ["WIFI", "vegan", "wifi"]);

        Assert.Equal("Armenian", listing.Cuisine);
        Assert.Null(listing.About);
        Assert.Equal(2, listing.PriceLevel);
        Assert.Equal(["wifi", "vegan"], listing.Amenities);
    }

    [Fact]
    public void Every_broken_listing_field_is_reported_at_once()
    {
        var refused = Assert.Throws<FieldValidationException>(() => BranchListingRules.Normalise(
            new string('c', 121), null, 5, "ftp://example.test", ["jacuzzi"]));

        Assert.Equal(
            ["cuisine", "priceLevel", "websiteUrl", "amenities"],
            refused.Violations.Select(v => v.Field));
    }

    [Theory]
    [InlineData(0.5, 0.25)]
    [InlineData(0d, 1d)]
    public void A_table_takes_photo_coordinates_between_zero_and_one(double x, double y)
    {
        Assert.Equal((x, y), BranchListingRules.PhotoPosition(x, y, "7"));
    }

    [Fact]
    public void A_table_with_one_photo_coordinate_or_one_off_the_photo_is_refused()
    {
        Assert.Equal(
            "photoY",
            Assert.Throws<FieldValidationException>(() => BranchListingRules.PhotoPosition(0.5, null, "7")).Field);

        Assert.Equal(
            "photoX",
            Assert.Throws<FieldValidationException>(() => BranchListingRules.PhotoPosition(1.2, 0.5, "7")).Field);

        Assert.Equal((null, null), BranchListingRules.PhotoPosition(null, null, "7"));
    }

    // ------------------------------------------------------------ badges

    [Fact]
    public void A_branch_is_new_for_thirty_days_from_creation()
    {
        Assert.True(BranchBadgeRules.IsNew(Now.AddDays(-29), Now));
        Assert.False(BranchBadgeRules.IsNew(Now.AddDays(-30), Now));
    }

    [Theory]
    [InlineData(20, 0, null, true)]
    [InlineData(19, 0, null, false)]
    [InlineData(0, 5, 4.5, true)]
    [InlineData(0, 5, 4.4, false)]
    [InlineData(0, 4, 5.0, false)]
    public void Popular_is_a_full_room_or_well_reviewed(int sittings, int reviews, double? average, bool popular)
    {
        Assert.Equal(popular, BranchBadgeRules.IsPopular(sittings, reviews, average));
    }

    [Fact]
    public void Badges_list_popular_before_new_and_an_unrated_branch_has_no_rating()
    {
        Assert.Equal(["popular", "new"], BranchBadgeRules.For(Now.AddDays(-1), Now, 25, 0, null));
        Assert.Empty(BranchBadgeRules.For(Now.AddDays(-90), Now, 0, 0, null));
        Assert.Null(BranchBadgeRules.AverageRating(0, 0));
        Assert.Equal(4.7, BranchBadgeRules.AverageRating(3, 14));
    }

    [Fact]
    public void Distance_is_great_circle_kilometres_to_one_decimal()
    {
        // Republic Square to the Cascade, central Yerevan: about 1.3 km.
        var km = BranchListingRules.DistanceKm(40.1777, 44.5126, 40.1897, 44.5156);

        Assert.InRange(km, 1.2, 1.5);
        Assert.Equal(0d, BranchListingRules.DistanceKm(40.18, 44.51, 40.18, 44.51));
    }

    // ------------------------------------------------------------ order statuses

    [Theory]
    [InlineData(TabOrderStatus.New, "confirmed", true)]
    [InlineData(TabOrderStatus.InKitchen, "preparing", true)]
    [InlineData(TabOrderStatus.Ready, "ready", true)]
    [InlineData(TabOrderStatus.Served, "completed", false)]
    [InlineData(TabOrderStatus.Voided, "cancelled", false)]
    public void The_kitchen_rail_maps_onto_the_app_statuses(TabOrderStatus status, string app, bool active)
    {
        Assert.Equal(app, DinerOrderStatuses.From(status));
        Assert.Equal(active, DinerOrderStatuses.IsActive(status));
    }
}
