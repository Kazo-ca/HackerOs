using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests;

public sealed class AppPlatformIdTests
{
    [Theory]
    [InlineData("desktop")]
    [InlineData("mobile")]
    [InlineData("desktop-vr")]
    [InlineData("a")]
    public void TryParse_accepts_lowercase_hyphenated_identifiers(string value)
    {
        Assert.True(AppPlatformId.TryParse(value, out AppPlatformId platformId));
        Assert.Equal(value, platformId.Value);
        Assert.Equal(value, platformId.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Desktop")]
    [InlineData("mobile ")]
    [InlineData(" mobile")]
    [InlineData("mobile_app")]
    [InlineData("-mobile")]
    [InlineData("mobile-")]
    [InlineData("mo--bile")]
    [InlineData("mobile phone")]
    public void TryParse_rejects_invalid_identifiers(string value)
    {
        Assert.False(AppPlatformId.TryParse(value, out _));
    }

    [Fact]
    public void TryParse_rejects_null()
    {
        Assert.False(AppPlatformId.TryParse(null, out _));
    }

    [Fact]
    public void TryParse_rejects_identifiers_over_maximum_length()
    {
        string tooLong = new('a', AppPlatformId.MaximumLength + 1);

        Assert.False(AppPlatformId.TryParse(tooLong, out _));
    }

    [Fact]
    public void Parse_throws_format_exception_for_invalid_identifiers()
    {
        Assert.Throws<FormatException>(() => AppPlatformId.Parse("Not Valid"));
    }

    [Fact]
    public void WellKnownAppPlatforms_expose_desktop_and_mobile()
    {
        Assert.Equal("desktop", WellKnownAppPlatforms.Desktop.Value);
        Assert.Equal("mobile", WellKnownAppPlatforms.Mobile.Value);
    }

    [Fact]
    public void Equal_values_compare_equal()
    {
        Assert.Equal(AppPlatformId.Parse("desktop"), AppPlatformId.Parse("desktop"));
        Assert.NotEqual(AppPlatformId.Parse("desktop"), AppPlatformId.Parse("mobile"));
    }
}
