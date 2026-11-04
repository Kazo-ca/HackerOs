using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.ErrorLogViewer.Tests;

/// <summary>
/// The Error Log Viewer's chrome speaks English and French (ADR 0104); the log lines themselves stay
/// as written in <c>/var/log</c>.
/// </summary>
public sealed class ErrorLogViewerLocalizationTests
{
    /// <summary>The app's texts, read by name so the test does not need the internal marker class.</summary>
    private static readonly ResourceManager Resources =
        new("Kazo.Os.Apps.ErrorLogViewer.Resources.Strings", typeof(ErrorLogViewerApp).Assembly);

    [Fact]
    public void English_and_French_texts_have_exactly_the_same_keys()
    {
        SortedSet<string> english = Keys(CultureInfo.InvariantCulture);
        SortedSet<string> french = Keys(CultureInfo.GetCultureInfo("fr"));

        Assert.NotEmpty(english);
        Assert.Equal(english, french);
    }

    [Fact]
    public void Column_headers_are_translated_in_French()
    {
        Assert.Equal("Level", Resources.GetString("Column.Level", CultureInfo.InvariantCulture));
        Assert.Equal("Niveau", Resources.GetString("Column.Level", CultureInfo.GetCultureInfo("fr-CA")));
        Assert.Equal("Fichier", Resources.GetString("Column.File", CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Fact]
    public void The_French_name_is_generated_next_to_the_manifest()
    {
        string manifestPath = FindManifestPath();
        string directory = Path.GetDirectoryName(manifestPath)!;
        using JsonDocument french = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "localization", "fr.json")));
        Assert.Equal("Journal des erreurs", french.RootElement.GetProperty("name").GetString());

        AppManifest manifest = AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(manifestPath));
        Assert.Contains(manifest.Localizations, localization => localization.Culture == "fr");
    }

    private static SortedSet<string> Keys(CultureInfo culture)
    {
        ResourceSet set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No {culture.Name} resources.");
        return new SortedSet<string>(set.Cast<DictionaryEntry>().Select(static entry => (string)entry.Key), StringComparer.Ordinal);
    }

    private static string FindManifestPath()
    {
        for (DirectoryInfo? directory = new(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Apps", "System", "Kazo.Os.Apps.ErrorLogViewer", "manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("manifest.json of Error Log Viewer not found above the test output.");
    }
}
