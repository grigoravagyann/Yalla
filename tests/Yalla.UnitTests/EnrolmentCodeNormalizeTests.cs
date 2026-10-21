using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests;

/// <summary>
/// An enrolment code is compared by hash, so the spelling a person types has to be brought back to
/// the one that was issued before hashing.
/// </summary>
public sealed class EnrolmentCodeNormalizeTests
{
    [Theory]
    [InlineData("H7KQ2MXP9D", "H7KQ2MXP9D")]
    [InlineData("h7kq2mxp9d", "H7KQ2MXP9D")]
    [InlineData("h7kq2-mxp9d", "H7KQ2MXP9D")]
    [InlineData(" H7KQ2 MXP9D\r\n", "H7KQ2MXP9D")]
    public void Every_spelling_of_one_code_is_that_code(string typed, string issued) =>
        Assert.Equal(issued, Secrets.NormalizeEnrolmentCode(typed));

    [Fact]
    public void A_letter_no_code_contains_is_kept_so_a_mistyped_code_still_fails() =>
        Assert.Equal("H7KQ2OXP9D", Secrets.NormalizeEnrolmentCode("h7kq2oxp9d"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" - ")]
    public void Nothing_typed_is_nothing(string? typed) =>
        Assert.Equal(string.Empty, Secrets.NormalizeEnrolmentCode(typed));

    [Fact]
    public void A_fresh_code_is_already_in_its_normal_form()
    {
        var code = Secrets.NewEnrolmentCode();

        Assert.Equal(code, Secrets.NormalizeEnrolmentCode(code));
    }
}
