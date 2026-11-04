using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests;

public sealed class AppAuthorityPolicyTests
{
    [Theory]
    [InlineData(AppAuthority.User, AppAuthority.User, true)]
    [InlineData(AppAuthority.User, AppAuthority.Administrator, false)]
    [InlineData(AppAuthority.Administrator, AppAuthority.User, true)]
    [InlineData(AppAuthority.Administrator, AppAuthority.System, false)]
    [InlineData(AppAuthority.System, AppAuthority.Administrator, true)]
    [InlineData(AppAuthority.System, AppAuthority.System, true)]
    public void Satisfies_enforces_the_ordered_authority_hierarchy(
        AppAuthority actual,
        AppAuthority required,
        bool expected)
    {
        Assert.Equal(expected, AppAuthorityPolicy.Satisfies(actual, required));
    }
}