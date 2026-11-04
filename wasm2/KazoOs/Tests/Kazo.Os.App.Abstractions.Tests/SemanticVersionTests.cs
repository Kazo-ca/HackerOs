using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.9.9", "2.0.0")]
    public void CompareTo_implements_semantic_version_precedence(string lower, string higher)
    {
        Assert.True(SemanticVersion.Parse(lower).CompareTo(SemanticVersion.Parse(higher)) < 0);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-alpha.01")]
    [InlineData("v1.0.0")]
    public void TryParse_rejects_non_semantic_versions(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }

    [Fact]
    public void Build_metadata_does_not_change_precedence()
    {
        SemanticVersion left = SemanticVersion.Parse("1.0.0+build.1");
        SemanticVersion right = SemanticVersion.Parse("1.0.0+build.2");

        Assert.Equal(0, left.CompareTo(right));
    }

    [Fact]
    public void Numeric_prerelease_identifiers_do_not_overflow_machine_integers()
    {
        SemanticVersion lower = SemanticVersion.Parse("1.0.0-999999999999999999999999");
        SemanticVersion higher = SemanticVersion.Parse("1.0.0-1000000000000000000000000");
        SemanticVersion alphabetic = SemanticVersion.Parse("1.0.0-alpha");

        Assert.True(lower.CompareTo(higher) < 0);
        Assert.True(higher.CompareTo(alphabetic) < 0);
    }

    [Fact]
    public void Oversized_core_number_fails_without_throwing()
    {
        bool parsed = SemanticVersion.TryParse("999999999999999999999999.0.0", out _);

        Assert.False(parsed);
    }
}