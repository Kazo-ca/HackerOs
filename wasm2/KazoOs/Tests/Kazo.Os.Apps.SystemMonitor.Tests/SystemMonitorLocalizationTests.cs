using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Apps.SystemMonitor.Metrics;
using Xunit;

namespace Kazo.Os.Apps.SystemMonitor.Tests;

/// <summary>
/// System Monitor speaks English and French (ADR 0104): the same keys in both resource files, the
/// translated name next to the manifest, and French units for numbers read from <c>/proc</c>.
/// </summary>
public sealed class SystemMonitorLocalizationTests
{
    /// <summary>The app's texts, read by name so the test does not need the internal marker class.</summary>
    private static readonly ResourceManager Resources =
        new("Kazo.Os.Apps.SystemMonitor.Resources.Strings", typeof(SystemMonitorApp).Assembly);

    [Fact]
    public void English_and_French_texts_have_exactly_the_same_keys()
    {
        SortedSet<string> english = Keys(CultureInfo.InvariantCulture);
        SortedSet<string> french = Keys(CultureInfo.GetCultureInfo("fr"));

        Assert.NotEmpty(english);
        Assert.Equal(english, french);
    }

    [Fact]
    public void Tabs_and_process_states_are_translated_in_French()
    {
        CultureInfo french = CultureInfo.GetCultureInfo("fr-CA");
        Assert.Equal("Processes", Resources.GetString("Tab.Processes", CultureInfo.InvariantCulture));
        Assert.Equal("Processus", Resources.GetString("Tab.Processes", french));
        Assert.Equal("En cours", Resources.GetString("ProcessState.Running", french));
    }

    [Fact]
    public void The_French_name_is_generated_next_to_the_manifest()
    {
        string directory = FindProjectDirectory();
        using JsonDocument french = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "localization", "fr.json")));
        Assert.Equal("Moniteur système", french.RootElement.GetProperty("name").GetString());

        AppManifest manifest = AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        Assert.Contains(manifest.Localizations, localization => localization.Culture == "fr");
    }

    [Fact]
    public void French_units_use_octets_a_decimal_comma_and_a_space_before_percent()
    {
        CultureInfo french = CultureInfo.GetCultureInfo("fr-FR");
        Assert.Equal("1,5 Kio", Units.Bytes(1536, french));
        Assert.Equal("2 Kio/s", Units.Rate(2048, french));
        Assert.Equal("12,5 %", Units.Percent(12.5, french));
        Assert.Equal("1 j 02:03:04", Units.Duration(new TimeSpan(1, 2, 3, 4), french));
        // English cultures keep the invariant spelling.
        Assert.Equal("1.5 KiB", Units.Bytes(1536, CultureInfo.GetCultureInfo("en-US")));
    }

    private static SortedSet<string> Keys(CultureInfo culture)
    {
        ResourceSet set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No {culture.Name} resources.");
        return new SortedSet<string>(set.Cast<DictionaryEntry>().Select(static entry => (string)entry.Key), StringComparer.Ordinal);
    }

    private static string FindProjectDirectory()
    {
        for (DirectoryInfo? directory = new(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Apps", "System", "Kazo.Os.Apps.SystemMonitor");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Kazo.Os.Apps.SystemMonitor not found above the test output.");
    }
}
