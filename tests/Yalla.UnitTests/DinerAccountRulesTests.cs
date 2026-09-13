using Yalla.Domain.Identity;
using Yalla.Domain.Media;

namespace Yalla.UnitTests;

/// <summary>
/// The username, email and password rules a diner account is held to, and the entity's mutators.
/// </summary>
/// <remarks>
/// Pure functions, pinned here so the sign-up form and the profile edit cannot drift: both call
/// these and nothing else decides what a username is.
/// </remarks>
public class DinerAccountRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ username

    [Fact]
    public void A_username_is_trimmed_and_lowercased()
    {
        Assert.Equal("ani.k_1", DinerAccountRules.NormaliseUsername("  Ani.K_1 ", "username"));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("_ani")]
    [InlineData(".ani")]
    [InlineData("ani k")]
    [InlineData("ani@k")]
    [InlineData("ani-k")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_username_that_breaks_the_shape_is_refused_naming_the_field(string? username)
    {
        var refused = Assert.Throws<ArgumentException>(() => DinerAccountRules.NormaliseUsername(username, "username"));

        // The wire field, so the mapper turns it into context.field and the form can place it.
        Assert.Equal("username", refused.ParamName);
    }

    [Fact]
    public void A_username_is_at_most_thirty_characters()
    {
        var thirty = new string('a', DinerAccountRules.UsernameMaxLength);

        Assert.Equal(thirty, DinerAccountRules.NormaliseUsername(thirty, "username"));
        Assert.Throws<ArgumentException>(() => DinerAccountRules.NormaliseUsername(thirty + "a", "username"));
    }

    // ------------------------------------------------------------ email

    [Fact]
    public void An_email_is_trimmed_and_lowercased()
    {
        Assert.Equal("ani.k@example.test", DinerAccountRules.NormaliseEmail("  Ani.K@Example.TEST ", "email"));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("a@b")]
    [InlineData("a b@c.d")]
    [InlineData("@c.d")]
    [InlineData("a@")]
    [InlineData("")]
    [InlineData(null)]
    public void Something_that_is_not_an_address_is_refused_naming_the_field(string? email)
    {
        var refused = Assert.Throws<ArgumentException>(() => DinerAccountRules.NormaliseEmail(email, "email"));

        Assert.Equal("email", refused.ParamName);
    }

    [Fact]
    public void An_email_longer_than_the_column_is_refused()
    {
        var tooLong = new string('a', 320) + "@example.test";

        Assert.Throws<ArgumentException>(() => DinerAccountRules.NormaliseEmail(tooLong, "email"));
    }

    // ------------------------------------------------------------ password

    [Fact]
    public void A_password_comes_back_exactly_as_typed()
    {
        // Never trimmed: a trailing space somebody typed is part of the secret they chose.
        Assert.Equal(" pass word ", DinerAccountRules.CheckPassword(" pass word ", "ani", "ani@example.test", "password"));
    }

    [Theory]
    [InlineData("short7!")]
    [InlineData("")]
    [InlineData(null)]
    public void A_password_below_eight_characters_is_refused(string? password)
    {
        var refused = Assert.Throws<ArgumentException>(
            () => DinerAccountRules.CheckPassword(password, "ani", "ani@example.test", "password"));

        Assert.Equal("password", refused.ParamName);
    }

    [Fact]
    public void A_password_above_the_ceiling_is_refused()
    {
        var atCeiling = new string('x', DinerAccountRules.PasswordMaxLength);

        Assert.Equal(atCeiling, DinerAccountRules.CheckPassword(atCeiling, null, null, "password"));
        Assert.Throws<ArgumentException>(() => DinerAccountRules.CheckPassword(atCeiling + "x", null, null, "password"));
    }

    /// <summary>The two guesses anybody who knows the account would try first.</summary>
    [Theory]
    [InlineData("Ani.Khachatryan")]
    [InlineData("ANI.KHACHATRYAN@EXAMPLE.TEST")]
    [InlineData(" ani.khachatryan ")]
    public void A_password_equal_to_the_username_or_the_email_is_refused(string password)
    {
        var refused = Assert.Throws<ArgumentException>(
            () => DinerAccountRules.CheckPassword(password, "ani.khachatryan", "ani.khachatryan@example.test", "password"));

        Assert.Equal("password", refused.ParamName);
    }

    [Fact]
    public void Only_a_minimum_length_is_enforced_and_no_composition_rule()
    {
        // Eight lowercase letters is allowed: composition rules push people towards Password1!.
        Assert.Equal("khachapuri", DinerAccountRules.CheckPassword("khachapuri", "ani", null, "password"));
    }

    // ------------------------------------------------------------ the entity

    [Fact]
    public void Registering_normalises_the_identifiers_and_leaves_the_number_unverified()
    {
        var diner = DinerUser.Register("+37411223344", "hy", " Ani ", " Ani.K ", " Ani.K@Example.TEST ", "hash");

        Assert.Equal("ani.k", diner.Username);
        Assert.Equal("ani.k@example.test", diner.Email);
        Assert.Equal("Ani", diner.DisplayName);
        Assert.True(diner.HasPassword);
        Assert.False(diner.IsPhoneVerified);
        Assert.Null(diner.PhoneVerifiedAtUtc);
        Assert.True(diner.IsActive);
    }

    [Fact]
    public void Registering_requires_a_display_name()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => DinerUser.Register("+37411223344", "hy", "  ", "ani", "ani@example.test", "hash"));

        Assert.Equal("displayName", refused.ParamName);
    }

    [Fact]
    public void The_code_flows_account_has_no_password_and_no_sign_in_name()
    {
        var diner = new DinerUser("+37411223344", "hy");

        Assert.False(diner.HasPassword);
        Assert.Null(diner.Username);
        Assert.Null(diner.Email);
        Assert.Null(diner.DisplayName);
    }

    [Fact]
    public void The_first_phone_verification_is_the_one_kept()
    {
        var diner = new DinerUser("+37411223344", "hy");

        diner.MarkPhoneVerified(Now);
        diner.MarkPhoneVerified(Now.AddDays(3));

        Assert.Equal(Now, diner.PhoneVerifiedAtUtc);
        Assert.True(diner.IsPhoneVerified);
    }

    [Fact]
    public void Renaming_refuses_a_blank_where_the_optional_setter_would_clear()
    {
        var diner = DinerUser.Register("+37411223344", "hy", "Ani", "ani", "ani@example.test", "hash");

        Assert.Throws<ArgumentException>(() => diner.Rename(" "));
        Assert.Equal("Ani", diner.DisplayName);

        diner.SetDisplayName(" ");
        Assert.Null(diner.DisplayName);
    }

    [Fact]
    public void A_photo_is_pointed_at_or_cleared_and_never_at_an_empty_id()
    {
        var diner = new DinerUser("+37411223344", "hy");
        var photoId = Guid.CreateVersion7();

        diner.SetPhoto(photoId);
        Assert.Equal(photoId, diner.PhotoId);

        Assert.Throws<ArgumentException>(() => diner.SetPhoto(Guid.Empty));
        Assert.Equal(photoId, diner.PhotoId);

        diner.SetPhoto(null);
        Assert.Null(diner.PhotoId);
    }

    // ------------------------------------------------------------ the photo's one owner

    [Fact]
    public void A_branch_photo_has_no_diner_and_a_diner_photo_has_no_branch()
    {
        var branchId = Guid.CreateVersion7();
        var dinerId = Guid.CreateVersion7();

        var ofBranch = new Photo(branchId, new string('a', 64), "t", "c", "f", 10, 10, 30L, Now);
        var ofDiner = Photo.ForDiner(dinerId, new string('b', 64), "t", "c", "f", 10, 10, 30L, Now);

        Assert.Equal(branchId, ofBranch.BranchId);
        Assert.Null(ofBranch.DinerUserId);

        Assert.Equal(dinerId, ofDiner.DinerUserId);
        Assert.Null(ofDiner.BranchId);
        Assert.Null(ofDiner.UploadedByStaffId);
    }

    [Fact]
    public void A_photo_with_no_owner_cannot_be_constructed()
    {
        Assert.Throws<ArgumentException>(
            () => Photo.ForDiner(Guid.Empty, new string('b', 64), "t", "c", "f", 10, 10, 30L, Now));

        Assert.Throws<ArgumentException>(
            () => new Photo(Guid.Empty, new string('a', 64), "t", "c", "f", 10, 10, 30L, Now));
    }

    [Fact]
    public void Owner_keys_tell_a_branch_and_a_person_apart()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(id.ToString(), PhotoRules.OwnerKeyForBranch(id));
        Assert.Equal($"diner-{id}", PhotoRules.OwnerKeyForDiner(id));
    }
}
