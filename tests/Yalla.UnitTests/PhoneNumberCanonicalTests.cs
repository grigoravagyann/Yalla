using Yalla.Domain.Common;

namespace Yalla.UnitTests;

/// <summary>
/// The canonical form a booking's number is keyed on (review S9): two spellings of one number are one key.
/// </summary>
public sealed class PhoneNumberCanonicalTests
{
    [Theory]
    [InlineData("+37491234567", "+37491234567")]
    [InlineData("+374091234567", "+37491234567")]
    [InlineData(" +37411223344 ", "+37411223344")]
    [InlineData("+4402079460000", "+442079460000")]
    [InlineData("+14155550123", "+14155550123")]
    public void Two_spellings_of_one_number_have_one_canonical_form(string typed, string canonical) =>
        Assert.Equal(canonical, PhoneNumber.Canonical(typed));

    [Fact]
    public void Something_the_library_cannot_read_is_kept_as_it_came()
    {
        // Normalise has already insisted on E.164 by the time a number is keyed; this is only the fallback.
        Assert.Equal("+999", PhoneNumber.Canonical("+999"));
    }
}
