using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Globalization;
using Xunit;

namespace HackerOs.Apps.Settings.Tests;

/// <summary>Settings speaks English and French (ADR 0104): same keys in both, translated name in the catalog.</summary>
public sealed class SettingsLocalizationTests
{
    [Fact]
    public void English_and_French_texts_have_exactly_the_same_keys()
    {
        ResourceManager resources = HackLocalization.GetResourceManager(typeof(Strings));
        SortedSet<string> english = Keys(resources, CultureInfo.InvariantCulture);
        SortedSet<string> french = Keys(resources, CultureInfo.GetCultureInfo("fr"));

        Assert.NotEmpty(english);
        Assert.Equal(english, french);
    }

    [Fact]
    public void The_French_name_of_Settings_is_generated_next_to_the_manifest()
    {
        // The project declares two apps since ADR 0218 (Settings and « Set as wallpaper »): one folder per app.
        string directory = FindProjectDirectory();
        using JsonDocument french = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "localization", "org.hackeros.settings", "fr.json")));
        Assert.Equal("Paramètres", french.RootElement.GetProperty("name").GetString());

        AppManifest manifest = AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(Path.Combine(directory, "manifests", "org.hackeros.settings.json")));
        Assert.Contains(manifest.Localizations, localization => localization.Culture == "fr");
    }

    [Fact]
    public void Settings_may_write_the_user_locale_file_in_the_home_directory()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new SettingsApp());

        Assert.Contains(AppCapabilities.FileSystemUserHomeRead, manifest.Capabilities);
        Assert.Contains(AppCapabilities.FileSystemUserHomeWrite, manifest.Capabilities);
    }

    private static SortedSet<string> Keys(ResourceManager resources, CultureInfo culture)
    {
        ResourceSet set = resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No {culture.Name} resources.");
        return new SortedSet<string>(set.Cast<DictionaryEntry>().Select(static entry => (string)entry.Key), StringComparer.Ordinal);
    }

    private static string FindProjectDirectory()
    {
        for (DirectoryInfo? directory = new(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Apps", "System", "HackerOs.Apps.Settings");
            if (File.Exists(Path.Combine(candidate, "manifests", "org.hackeros.settings.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("HackerOs.Apps.Settings not found above the test output.");
    }
}
