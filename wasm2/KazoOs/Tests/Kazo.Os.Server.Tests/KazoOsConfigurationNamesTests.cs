using Kazo.Os.Server.Contracts.Hosting;
using Xunit;

namespace Kazo.Os.Server.Tests;

/// <summary>The configuration names read before the rename to Kazo OS (ADR 0228 §2).</summary>
public sealed class KazoOsConfigurationNamesTests
{
    [Fact]
    public void An_old_HackerOs_key_answers_under_KazoOs_when_the_new_section_lacks_it()
    {
        Dictionary<string, string?> settings = new()
        {
            ["HackerOs"] = null,
            ["HackerOs:Proxy:Enabled"] = "false",
            ["HackerOs:Pairing:Root"] = "/old/pairing",
            ["KazoOs:Pairing:Root"] = "/new/pairing",
            ["ConnectionStrings:HackerOsDb"] = "Data Source=old.db",
            ["Logging:LogLevel:Default"] = "Information",
        };

        Dictionary<string, string?> mapped = KazoOsConfigurationNames.MapLegacySettings(settings)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);

        Assert.Equal("false", mapped["KazoOs:Proxy:Enabled"]);
        Assert.Equal("Data Source=old.db", mapped["ConnectionStrings:KazoOsDb"]);
        // The new name wins: a key the KazoOs section already has is not overwritten.
        Assert.False(mapped.ContainsKey("KazoOs:Pairing:Root"));
        Assert.Equal(2, mapped.Count);
    }

    [Fact]
    public void A_configuration_with_only_the_new_names_needs_nothing_more()
    {
        Dictionary<string, string?> settings = new()
        {
            ["KazoOs:Proxy:Enabled"] = "true",
            ["ConnectionStrings:KazoOsDb"] = "Data Source=kazoos.db",
        };

        Assert.Empty(KazoOsConfigurationNames.MapLegacySettings(settings));
    }
}
