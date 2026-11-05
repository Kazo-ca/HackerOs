using System.Globalization;
using System.Xml.Linq;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Translates the generated manifests of an app project (ADR 0104, section 4): reads
/// <c>Resources/AppManifest.&lt;culture&gt;.resx</c>, writes one <c>localization/&lt;culture&gt;.json</c>
/// per app and language, and adds the matching entries to <see cref="AppManifest.Localizations"/>.
/// </summary>
/// <remarks>
/// <para>
/// Keys are <c>Name</c>, <c>Description</c> and <c>Terminal.Usage</c>. In a project declaring
/// several apps (the commands project), each key is prefixed with the app id:
/// <c>org.kazoos.commands.ls.Description</c>, and the files go to
/// <c>localization/&lt;appId&gt;/&lt;culture&gt;.json</c>.
/// </para>
/// <para>
/// A missing key keeps the English text of the <c>AppDefinition</c>. A key naming no app, an
/// unknown suffix or a malformed file fails the build (<c>KOSAPP0040</c>). The English texts stay in
/// C#. The one exception is the titles of the file menu entries (ADR 0159 §3): their English text is
/// in <c>Resources/AppManifest.resx</c>, their translations here, and
/// <see cref="ContextMenuRegistrationInspector"/> copies both into the manifest.
/// </para>
/// </remarks>
public static class ManifestLocalizationGenerator
{
    /// <summary>Folder of the generated files, relative to the project (the package root).</summary>
    public const string OutputFolder = "localization";

    /// <summary>Base name of the source files under <c>Resources/</c>.</summary>
    public const string SourceBaseName = "AppManifest";

    private static readonly string[] Suffixes = ["Name", "Description", "Terminal.Usage"];

    /// <summary>The files written for one project and the manifests that now reference them.</summary>
    /// <param name="Manifests">The manifests, with <see cref="AppManifest.Localizations"/> filled.</param>
    /// <param name="Files">Full paths of the localization files that now describe the project.</param>
    /// <param name="Diagnostics">Problems found; nothing is written when there is one.</param>
    public sealed record Result(IReadOnlyList<GeneratedManifest> Manifests, IReadOnlyList<string> Files, IReadOnlyList<GeneratorDiagnostic> Diagnostics);

    /// <summary>Returns the source files of <paramref name="projectDirectory"/>, sorted by culture.</summary>
    /// <param name="projectDirectory">The app project directory.</param>
    public static IReadOnlyList<(string Culture, string Path)> FindSources(string projectDirectory)
    {
        string resources = Path.Combine(projectDirectory, "Resources");
        if (!Directory.Exists(resources))
        {
            return [];
        }

        List<(string Culture, string Path)> sources = [];
        foreach (string file in Directory.EnumerateFiles(resources, SourceBaseName + ".*.resx"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            sources.Add((name[(SourceBaseName.Length + 1)..], Path.GetFullPath(file)));
        }

        return [.. sources.OrderBy(source => source.Culture, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Applies the translations of <paramref name="projectDirectory"/> to <paramref name="manifests"/>
    /// and writes their files (only when their bytes change); stale files are removed.
    /// </summary>
    /// <param name="projectDirectory">The app project directory.</param>
    /// <param name="manifests">The manifests from <see cref="AppDefinitionInspector"/>.</param>
    public static Result Apply(string projectDirectory, IReadOnlyList<GeneratedManifest> manifests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(manifests);

        bool multi = manifests.Count > 1;
        List<GeneratorDiagnostic> diagnostics = [];
        Dictionary<string, List<LocalizationManifest>> entries = new(StringComparer.Ordinal);
        Dictionary<string, string> files = new(StringComparer.Ordinal);

        foreach ((string culture, string path) in FindSources(projectDirectory))
        {
            if (!IsCultureName(culture))
            {
                diagnostics.Add(new(GeneratorCodes.InvalidLocalization, path, $"'{culture}' is not a culture name (expected e.g. AppManifest.fr.resx)."));
                continue;
            }

            if (!TryReadResx(path, diagnostics, out Dictionary<string, string>? values))
            {
                continue;
            }

            Dictionary<string, Dictionary<string, string>> byApp = new(StringComparer.Ordinal);
            foreach ((string key, string value) in values)
            {
                // ADR 0159 §3: the titles of the file menu entries live here too; the inspector copied them.
                if (IsMenuTitleKey(key, multi, manifests))
                {
                    continue;
                }

                if (!TrySplitKey(key, multi, manifests, out string appId, out string suffix))
                {
                    diagnostics.Add(new(GeneratorCodes.InvalidLocalization, path,
                        multi
                            ? $"Key '{key}' must be '<app id>.Name', '<app id>.Description' or '<app id>.Terminal.Usage' for an app of this project."
                            : $"Key '{key}' must be 'Name', 'Description' or 'Terminal.Usage'."));
                    continue;
                }

                (byApp.TryGetValue(appId, out Dictionary<string, string>? texts) ? texts : byApp[appId] = new(StringComparer.Ordinal))[suffix] = value;
            }

            foreach (GeneratedManifest generated in manifests)
            {
                AppManifest manifest = generated.Manifest;
                if (!byApp.TryGetValue(manifest.Id, out Dictionary<string, string>? texts))
                {
                    continue;
                }

                if (texts.ContainsKey("Terminal.Usage") && manifest.Terminal is null)
                {
                    diagnostics.Add(new(GeneratorCodes.InvalidLocalization, path, $"'{manifest.Id}' is not a terminal command: Terminal.Usage has nothing to translate."));
                    continue;
                }

                AppManifestText text = new(
                    texts.GetValueOrDefault("Name") ?? manifest.Name,
                    texts.GetValueOrDefault("Description") ?? manifest.Description,
                    texts.GetValueOrDefault("Terminal.Usage") ?? manifest.Terminal?.Usage);
                string relative = multi
                    ? $"{OutputFolder}/{manifest.Id}/{culture}.json"
                    : $"{OutputFolder}/{culture}.json";
                files[Path.GetFullPath(Path.Combine(projectDirectory, relative))] = AppManifestTextJsonSerializer.SerializeCanonical(text);
                (entries.TryGetValue(manifest.Id, out List<LocalizationManifest>? list) ? list : entries[manifest.Id] = [])
                    .Add(new LocalizationManifest(culture, relative));
            }
        }

        if (diagnostics.Count > 0)
        {
            return new Result([], [], diagnostics);
        }

        List<GeneratedManifest> updated = [];
        foreach (GeneratedManifest generated in manifests)
        {
            if (!entries.TryGetValue(generated.Manifest.Id, out List<LocalizationManifest>? list))
            {
                updated.Add(generated);
                continue;
            }

            AppManifest manifest = generated.Manifest with
            {
                Localizations = [.. generated.Manifest.Localizations, .. list]
            };
            updated.Add(generated with { Manifest = manifest, CanonicalJson = AppManifestJsonSerializer.SerializeCanonical(manifest) });
        }

        foreach ((string file, string content) in files)
        {
            ManifestWriter.WriteIfChanged(file, content);
        }

        DeleteStale(Path.Combine(projectDirectory, OutputFolder), files.Keys);
        return new Result(updated, [.. files.Keys.Order(StringComparer.Ordinal)], []);
    }

    /// <summary>Reads the localization files a manifest references, relative to its package root.</summary>
    /// <param name="manifestPath">The generated manifest file (<c>manifest.json</c> or <c>manifests/&lt;id&gt;.json</c>).</param>
    /// <param name="manifest">The manifest read from it.</param>
    /// <param name="diagnostics">Receives unreadable or invalid files.</param>
    /// <returns>The translations by culture; empty when the manifest has none.</returns>
    public static IReadOnlyDictionary<string, AppManifestText> ReadReferenced(
        string manifestPath, AppManifest manifest, ICollection<GeneratorDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Dictionary<string, AppManifestText> result = new(StringComparer.Ordinal);
        if (manifest.Localizations.Count == 0)
        {
            return result;
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string packageRoot = string.Equals(Path.GetFileName(directory), ManifestWriter.MultiDirectoryName, StringComparison.Ordinal)
            ? Path.GetDirectoryName(directory)!
            : directory;
        foreach (LocalizationManifest localization in manifest.Localizations)
        {
            string file = Path.Combine(packageRoot, localization.ResourcePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                result[localization.Culture] = AppManifestTextJsonSerializer.DeserializeStrict(File.ReadAllText(file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                diagnostics.Add(new(GeneratorCodes.InvalidLocalization, file, $"Localization '{localization.Culture}' of '{manifest.Id}' cannot be read: {exception.Message}"));
            }
        }

        return result;
    }

    private static bool IsMenuTitleKey(string key, bool multi, IReadOnlyList<GeneratedManifest> manifests) =>
        manifests.Any(generated => ContextMenuRegistrationInspector.TitleKeys(generated.Manifest)
            .Any(title => string.Equals(multi ? $"{generated.Manifest.Id}.{title}" : title, key, StringComparison.Ordinal)));

    private static bool TrySplitKey(
        string key, bool multi, IReadOnlyList<GeneratedManifest> manifests, out string appId, out string suffix)
    {
        appId = string.Empty;
        suffix = string.Empty;
        foreach (string candidate in Suffixes)
        {
            if (!multi)
            {
                if (string.Equals(key, candidate, StringComparison.Ordinal))
                {
                    appId = manifests[0].Manifest.Id;
                    suffix = candidate;
                    return true;
                }

                continue;
            }

            if (key.EndsWith("." + candidate, StringComparison.Ordinal))
            {
                string id = key[..^(candidate.Length + 1)];
                if (manifests.Any(manifest => string.Equals(manifest.Manifest.Id, id, StringComparison.Ordinal)))
                {
                    appId = id;
                    suffix = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadResx(
        string path, List<GeneratorDiagnostic> diagnostics, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Dictionary<string, string>? values)
    {
        values = null;
        try
        {
            XDocument document = XDocument.Load(path);
            Dictionary<string, string> result = new(StringComparer.Ordinal);
            foreach (XElement data in document.Root?.Elements("data") ?? [])
            {
                string? name = data.Attribute("name")?.Value;
                string? value = data.Element("value")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value) || data.Attribute("type") is not null)
                {
                    diagnostics.Add(new(GeneratorCodes.InvalidLocalization, path, $"Entry '{name}' must be a non-empty string."));
                    continue;
                }

                if (!result.TryAdd(name, value.Trim()))
                {
                    diagnostics.Add(new(GeneratorCodes.InvalidLocalization, path, $"Key '{name}' appears twice."));
                }
            }

            values = result;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            diagnostics.Add(new(GeneratorCodes.InvalidLocalization, path, $"The file cannot be read: {exception.Message}"));
            return false;
        }
    }

    private static bool IsCultureName(string culture)
    {
        if (culture.Length == 0 || culture.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return false;
        }

        try
        {
            return CultureInfo.GetCultureInfo(culture, predefinedOnly: true).Name.Length > 0;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static void DeleteStale(string directory, IEnumerable<string> keep)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        HashSet<string> kept = new(keep, StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            if (!kept.Contains(Path.GetFullPath(file)))
            {
                File.Delete(file);
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(sub).Any())
            {
                Directory.Delete(sub);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }
}
