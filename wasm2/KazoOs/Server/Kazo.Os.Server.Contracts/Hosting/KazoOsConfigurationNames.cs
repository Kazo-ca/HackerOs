namespace Kazo.Os.Server.Contracts.Hosting;

/// <summary>
/// Names of the host configuration of Kazo OS, and the names read before the rename (ADR 0228 §2).
/// Both hosts (the standalone PWA and <c>Kazo.Os.Server</c>) read the <see cref="Section"/>
/// section; a deployment configured before the rename still has a <see cref="LegacySection"/>
/// section, <see cref="LegacyEnvironmentPrefix"/> variables or a
/// <see cref="LegacyConnectionStringName"/> connection string, which keep working: the host adds
/// what <see cref="MapLegacySettings"/> returns as one more configuration source.
/// </summary>
public static class KazoOsConfigurationNames
{
    /// <summary>The configuration section of the OS and of its server.</summary>
    public const string Section = "KazoOs";

    /// <summary>The section's name before the rename, still read when the new one lacks a key.</summary>
    public const string LegacySection = "HackerOs";

    /// <summary>Prefix of the environment variables the server reads (<c>KAZOOS_KazoOs__Proxy__Enabled</c>).</summary>
    public const string EnvironmentPrefix = "KAZOOS_";

    /// <summary>The prefix before the rename; the server still reads it, the new prefix wins.</summary>
    public const string LegacyEnvironmentPrefix = "HACKEROS_";

    /// <summary>Name of the server's connection string (<c>ConnectionStrings:KazoOsDb</c>).</summary>
    public const string ConnectionStringName = "KazoOsDb";

    /// <summary>The connection string's name before the rename.</summary>
    public const string LegacyConnectionStringName = "HackerOsDb";

    private const string ConnectionStringsSection = "ConnectionStrings";

    /// <summary>
    /// Gets the settings to add so that every key of the old names is also seen under the new
    /// names: <c>HackerOs:Proxy:Enabled</c> as <c>KazoOs:Proxy:Enabled</c>, and
    /// <c>ConnectionStrings:HackerOsDb</c> as <c>ConnectionStrings:KazoOsDb</c>. A key the new
    /// names already have is not returned, so the new name always wins.
    /// </summary>
    /// <param name="settings">Every key and value of the configuration built so far.</param>
    /// <returns>The keys to add under the new names; empty when nothing uses the old names.</returns>
    public static IReadOnlyList<KeyValuePair<string, string?>> MapLegacySettings(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<KeyValuePair<string, string?>> all = [.. settings];
        HashSet<string> present = new(all.Select(static setting => setting.Key), StringComparer.OrdinalIgnoreCase);
        string legacyConnection = ConnectionStringsSection + ":" + LegacyConnectionStringName;
        string connection = ConnectionStringsSection + ":" + ConnectionStringName;
        List<KeyValuePair<string, string?>> mapped = [];
        foreach ((string key, string? value) in all)
        {
            string? renamed = key.StartsWith(LegacySection + ":", StringComparison.OrdinalIgnoreCase)
                ? Section + key[LegacySection.Length..]
                : string.Equals(key, legacyConnection, StringComparison.OrdinalIgnoreCase) ? connection : null;
            if (renamed is not null && value is not null && !present.Contains(renamed))
            {
                mapped.Add(new KeyValuePair<string, string?>(renamed, value));
            }
        }

        return mapped;
    }
}
