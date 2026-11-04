using Kazo.Os.Infrastructure.Browser.Rename;
using Kazo.Os.Infrastructure.Browser.Schema;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>The name table of the rename migration (ADR 0230 §3, ADR 0228 §1).</summary>
public sealed class RenamedNamesTests
{
    [Theory]
    [InlineData("hackeros", "kazoos")]
    [InlineData("hackeros-host-folders", "kazoos-host-folders")]
    [InlineData("hackweb.org.hackeros.intranet", "kazoweb.org.kazoos.intranet")]
    [InlineData("hackeros.backup.restore-record", "kazoos.backup.restore-record")]
    [InlineData("org.hackeros.hack-paint", "org.kazoos.kazo-paint")]
    [InlineData("org.hackeros.commands.hackpkg", "org.kazoos.commands.kazopkg")]
    [InlineData("HackerOs.Apps.HackPaint.dll", "Kazo.Os.Apps.KazoPaint.dll")]
    [InlineData("HackerOs.Apps.Terminal.TerminalWindow", "Kazo.Os.Apps.Terminal.TerminalWindow")]
    [InlineData("Simulated web browser for the HackerOS virtual network", "Simulated web browser for the Kazo OS virtual network")]
    [InlineData("hackeros.files.read", "kazoos.files.read")]
    [InlineData("/etc/hackweb/sites.config", "/etc/kazoweb/sites.config")]
    [InlineData("HACKEROS_PROXY", "KAZOOS_PROXY")]
    [InlineData("theme=HackerOs", "theme=KazoOs")]
    [InlineData("demo.hackapp", "demo.kazoapp")]
    public void Old_names_become_their_Kazo_OS_names_by_context(string before, string after)
    {
        Assert.True(RenamedNames.ContainsOldName(before));
        Assert.Equal(after, RenamedNames.Rewrite(before));
        Assert.False(RenamedNames.ContainsOldName(after));
        Assert.Equal(after, RenamedNames.Rewrite(after));
    }

    [Theory]
    [InlineData("kazoos")]
    [InlineData("Kazo OS")]
    [InlineData("a hacker types fast")]
    [InlineData("hacking")]
    [InlineData("")]
    public void Text_without_an_old_name_is_returned_unchanged(string text)
    {
        Assert.False(RenamedNames.ContainsOldName(text));
        Assert.Same(text, RenamedNames.Rewrite(text));
    }

    [Fact]
    public void The_legacy_os_database_maps_to_the_current_one()
    {
        Assert.True(RenamedNames.IsLegacyDatabaseName(RenamedNames.LegacyOsDatabaseName));
        Assert.Equal(KazoOsIndexedDbSchema.DatabaseName, RenamedNames.Rewrite(RenamedNames.LegacyOsDatabaseName));
        Assert.True(RenamedNames.IsLegacyDatabaseName("hackweb.org.hackeros.intranet"));
        Assert.False(RenamedNames.IsLegacyDatabaseName("kazoos"));
        Assert.False(RenamedNames.IsLegacyDatabaseName("hackerosity"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("garbage", true)]
    [InlineData("2.4.1", true)]
    [InlineData("3.0.0", false)]
    [InlineData("3.0.0-preview.1", false)]
    [InlineData("3.2.0+abc", false)]
    [InlineData("10.0.0", false)]
    public void Data_predates_the_rename_below_3_0_0_or_without_a_version(string? version, bool predates) =>
        Assert.Equal(predates, RenamedNames.PredatesRename(version));
}
