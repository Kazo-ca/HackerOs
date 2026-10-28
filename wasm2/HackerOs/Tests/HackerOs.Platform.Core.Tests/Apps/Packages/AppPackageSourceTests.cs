using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Platform.Core.Tests.Apps.Packages;

/// <summary>Sources, <c>app-sources.config</c> and <c>[installed]</c> (ADR 0182 §2 to §5).</summary>
public sealed class AppPackageSourceTests
{
    [Theory]
    [InlineData("github:Kazo-ca/HackerOs-App-Webamp")]
    [InlineData("Kazo-ca/HackerOs-App-Webamp")]
    [InlineData("https://github.com/Kazo-ca/HackerOs-App-Webamp")]
    [InlineData("https://github.com/Kazo-ca/HackerOs-App-Webamp.git")]
    [InlineData("https://github.com/Kazo-ca/HackerOs-App-Webamp/")]
    public void Every_written_form_names_the_same_repository(string text)
    {
        Assert.True(AppPackageSource.TryParse(text, out AppPackageSource? source));

        Assert.Equal("github:Kazo-ca/HackerOs-App-Webamp", source.ToString());
        Assert.Null(source.Version);
    }

    [Fact]
    public void A_version_suffix_pins_the_source()
    {
        Assert.True(AppPackageSource.TryParse("Kazo-ca/HackerOs-App-IRC@1.2.0", out AppPackageSource? source));

        Assert.Equal(SemanticVersion.Parse("1.2.0"), source.Version);
        Assert.Equal("github:Kazo-ca/HackerOs-App-IRC@1.2.0", source.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("webamp")]
    [InlineData("a/b/c")]
    [InlineData("../x")]
    [InlineData("Kazo-ca/..")]
    [InlineData("Kazo-ca/x@latest")]
    [InlineData("https://gitlab.com/Kazo-ca/x")]
    [InlineData("https://github.com/Kazo-ca/x?tab=readme")]
    [InlineData("Kazo ca/x")]
    public void Anything_else_is_refused(string text)
    {
        Assert.False(AppPackageSource.TryParse(text, out _));
    }

    [Fact]
    public void Owner_and_repository_compare_without_case_like_GitHub()
    {
        Assert.True(AppPackageSource.TryParse("kazo-CA/hackeros-app-webamp", out AppPackageSource? lower));
        Assert.True(AppPackageSource.TryParse("Kazo-ca/HackerOs-App-Webamp", out AppPackageSource? upper));

        Assert.Equal(upper, lower);
        Assert.Equal(upper.GetHashCode(), lower.GetHashCode());
    }

    [Fact]
    public void Files_are_read_from_the_packages_branch_on_the_download_host()
    {
        AppPackageSource source = new("Kazo-ca", "HackerOs-App-Webamp");

        Assert.Equal(
            "https://raw.githubusercontent.com/Kazo-ca/HackerOs-App-Webamp/hackeros-packages/index.json",
            source.GetIndexUri("raw.githubusercontent.com").AbsoluteUri);
        Assert.Equal(
            "https://raw.githubusercontent.com/Kazo-ca/HackerOs-App-Webamp/hackeros-packages/1.0.0/org.kazo.webamp-1.0.0.hackapp",
            source.GetFileUri("raw.githubusercontent.com", "1.0.0/org.kazo.webamp-1.0.0.hackapp").AbsoluteUri);
        Assert.Throws<ArgumentException>(() => source.GetFileUri("raw.githubusercontent.com", "../main/secret"));
        Assert.Throws<ArgumentException>(() => source.GetFileUri("raw.githubusercontent.com", "/etc/passwd"));
    }

    [Fact]
    public void Without_a_file_GitHub_is_the_download_host_and_only_Kazo_ca_is_trusted()
    {
        AppSourcesConfig config = AppSourcesConfigDocument.Read(null);

        Assert.Equal("raw.githubusercontent.com", config.GitHubHost);
        Assert.True(config.IsTrusted(new AppPackageSource("kazo-ca", "Anything")));
        Assert.False(config.IsTrusted(new AppPackageSource("someone", "Anything")));
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void The_administrator_can_trust_other_owners_and_withdraw_Kazo_ca()
    {
        AppSourcesConfig config = AppSourcesConfigDocument.Read(
            "[hosts]\ngithub=mirror.example.org\n[trusted]\ngithub.com/Kazo-ca=false\ngithub.com/alice=true\ngithub.com/bob=maybe\n");

        Assert.Equal("mirror.example.org", config.GitHubHost);
        Assert.False(config.IsTrusted(new AppPackageSource("Kazo-ca", "x")));
        Assert.True(config.IsTrusted(new AppPackageSource("Alice", "x")));
        Assert.Single(config.Warnings);
        Assert.Contains("'maybe' is not true or false", config.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_that_is_not_a_DNS_name_is_ignored()
    {
        AppSourcesConfig config = AppSourcesConfigDocument.Read("[hosts]\ngithub=https://evil/x\n");

        Assert.Equal(AppSourcesConfigDocument.DefaultGitHubHost, config.GitHubHost);
        Assert.Single(config.Warnings);
    }

    [Fact]
    public void Installed_apps_round_trip_through_the_machine_file_without_touching_uninstalled()
    {
        VirtualPath path = AppsConfigDocument.MachinePath;
        AppPackageSource source = new("Kazo-ca", "HackerOs-App-Webamp", SemanticVersion.Parse("1.0.0"));

        string written = AppsConfigDocument.SetInstalled("[uninstalled]\norg.hackeros.calculator=true\n", "org.kazo.webamp", source);
        InstalledAppsReadResult read = AppsConfigDocument.ReadInstalled(written, path);

        Assert.Equal(source, read.Sources["org.kazo.webamp"]);
        Assert.Contains("org.hackeros.calculator=true", written, StringComparison.Ordinal);
        Assert.Contains("org.kazo.webamp=github:Kazo-ca/HackerOs-App-Webamp@1.0.0", written, StringComparison.Ordinal);
        Assert.Empty(AppsConfigDocument.ReadInstalled(AppsConfigDocument.RemoveInstalled(written, "org.kazo.webamp"), path).Sources);
    }

    [Fact]
    public void An_installed_line_without_a_version_is_ignored_with_a_warning()
    {
        InstalledAppsReadResult read = AppsConfigDocument.ReadInstalled(
            "[installed]\norg.kazo.a=github:Kazo-ca/A\norg.kazo.b=nonsense\n", AppsConfigDocument.MachinePath);

        Assert.Empty(read.Sources);
        Assert.Equal(2, read.Warnings.Count);
    }

    [Fact]
    public void A_source_without_a_version_cannot_be_recorded()
    {
        Assert.Throws<ArgumentException>(() => AppsConfigDocument.SetInstalled(null, "org.kazo.a", new AppPackageSource("Kazo-ca", "A")));
    }
}
