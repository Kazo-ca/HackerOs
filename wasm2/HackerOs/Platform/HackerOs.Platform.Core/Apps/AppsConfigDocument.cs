using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Apps;

/// <summary>
/// The <c>apps.config</c> files of ADR 0048 section 3: an ADR 0011 <c>.config</c> document read
/// without a schema, where only the keys of the <c>[uninstalled]</c> section count.
/// </summary>
/// <remarks>
/// <code>
/// # /etc/hackeros/apps.config
/// [uninstalled]
/// org.hackeros.hack-paint=true
/// </code>
/// </remarks>
public static class AppsConfigDocument
{
    /// <summary>Section holding the uninstalled app IDs.</summary>
    public const string Section = "uninstalled";

    /// <summary>
    /// Machine-file section holding the apps installed from a package (ADR 0182 §5):
    /// <c>org.kazo.webamp=github:Kazo-ca/HackerOs-App-Webamp@1.0.0</c>.
    /// </summary>
    public const string InstalledSection = "installed";

    /// <summary>File name under <c>/etc/hackeros</c> and <c>~/.config/hackeros</c>.</summary>
    public const string FileName = "apps.config";

    /// <summary>Machine file, <c>system:system 0644</c>.</summary>
    public static VirtualPath MachinePath { get; } = VirtualPath.Parse($"/etc/hackeros/{FileName}");

    /// <summary>Header of the machine file written at installation, so an administrator edits a real, documented file.</summary>
    public const string MachineDefaultContent =
        "# Apps uninstalled for every user of this machine (ADR 0048).\n"
        + "# One app ID per line under [uninstalled], set to true. Remove the line (or set it to false)\n"
        + "# to reinstall. System apps cannot be uninstalled: their lines are ignored. No data is deleted.\n"
        + "# Users uninstall apps for themselves in ~/.config/hackeros/apps.config.\n"
        + "[uninstalled]\n";

    /// <summary>Header of a user file created by the first user-scope uninstall.</summary>
    public const string UserDefaultContent =
        "# Apps you uninstalled for yourself (ADR 0048). Same format as /etc/hackeros/apps.config.\n"
        + "[uninstalled]\n";

    /// <summary>Gets the user file of one home directory.</summary>
    /// <param name="homeDirectory">Absolute home directory, e.g. <c>/home/alice</c>.</param>
    public static VirtualPath GetUserPath(string homeDirectory) =>
        VirtualPath.Parse($"{homeDirectory.TrimEnd('/')}/.config/hackeros/{FileName}");

    /// <summary>
    /// Reads the uninstalled app IDs of one file against a catalog. Never fails: a bad line is
    /// skipped and reported in <see cref="AppsConfigReadResult.Warnings"/>.
    /// </summary>
    /// <param name="content">File content, or <see langword="null"/> when the file is absent or unreadable (nothing uninstalled).</param>
    /// <param name="path">File path, used in warnings.</param>
    /// <param name="catalog">Catalog of this build.</param>
    /// <remarks>
    /// The last occurrence of a key wins, like every <c>.config</c> reader. <c>true</c> uninstalls,
    /// <c>false</c> does nothing; another value is a warning. An ID unknown to this build is ignored
    /// silently (the same file may serve several builds), and a system app ID is ignored with a
    /// warning, so editing the file cannot bypass ADR 0048 section 1.
    /// </remarks>
    public static AppsConfigReadResult Read(string? content, VirtualPath path, AppCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrEmpty(content))
        {
            return AppsConfigReadResult.Empty;
        }

        Dictionary<string, ConfigLine> lastByAppId = new(StringComparer.Ordinal);
        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind == ConfigLineKind.KeyValue
                && !line.InMalformedSection
                && string.Equals(line.Group, Section, StringComparison.Ordinal)
                && line.Key is { Length: > 0 } key)
            {
                lastByAppId[key] = line;
            }
        }

        HashSet<string> uninstalled = new(StringComparer.Ordinal);
        List<string> warnings = [];
        foreach ((string appId, ConfigLine line) in lastByAppId)
        {
            string value = (line.Value ?? string.Empty).Trim();
            bool isTrue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            if (!isTrue && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"apps: {path.Value}:{line.LineNumber}: '{value}' is not true or false, line ignored");
                continue;
            }

            if (!isTrue || !catalog.Manifests.ContainsKey(appId))
            {
                continue;
            }

            if (catalog.IsSystemApp(appId))
            {
                warnings.Add($"apps: {path.Value}:{line.LineNumber}: {appId} is a system app and cannot be uninstalled, line ignored");
                continue;
            }

            uninstalled.Add(appId);
        }

        return new AppsConfigReadResult(uninstalled, warnings);
    }

    /// <summary>Marks every app ID uninstalled, keeping every other line exactly.</summary>
    /// <param name="content">Current content, or <see langword="null"/> for a new file.</param>
    /// <param name="appIds">App IDs to write as <c>true</c>.</param>
    /// <param name="defaultContent">Header used when the file does not exist yet.</param>
    public static string MarkUninstalled(string? content, IEnumerable<string> appIds, string defaultContent)
    {
        string edited = string.IsNullOrEmpty(content) ? defaultContent : content;
        foreach (string appId in appIds)
        {
            edited = ConfigDocumentEditor.SetValue(edited, Section, appId, "true");
        }

        return edited;
    }

    /// <summary>Removes every line of each app ID from <c>[uninstalled]</c>, keeping every other line exactly.</summary>
    /// <param name="content">Current content.</param>
    /// <param name="appIds">App IDs to reinstall.</param>
    public static string MarkInstalled(string? content, IEnumerable<string> appIds)
    {
        string edited = content ?? string.Empty;
        foreach (string appId in appIds)
        {
            edited = ConfigDocumentEditor.RemoveValue(edited, Section, appId);
        }

        return edited;
    }

    /// <summary>
    /// Reads the <c>[installed]</c> section (ADR 0182 §5). Never fails: a line whose value is not a
    /// source pinned to a version is skipped with a warning; the last occurrence of a key wins.
    /// </summary>
    /// <param name="content">Machine file content, or <see langword="null"/> when absent.</param>
    /// <param name="path">File path, used in warnings.</param>
    public static InstalledAppsReadResult ReadInstalled(string? content, VirtualPath path)
    {
        if (string.IsNullOrEmpty(content))
        {
            return InstalledAppsReadResult.Empty;
        }

        Dictionary<string, AppPackageSource> sources = new(StringComparer.Ordinal);
        List<string> warnings = [];
        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind != ConfigLineKind.KeyValue
                || line.InMalformedSection
                || !string.Equals(line.Group, InstalledSection, StringComparison.Ordinal)
                || line.Key is not { Length: > 0 } appId)
            {
                continue;
            }

            string value = (line.Value ?? string.Empty).Trim();
            if (!AppPackageSource.TryParse(value, out AppPackageSource? source) || source.Version is null)
            {
                warnings.Add($"apps: {path.Value}:{line.LineNumber}: '{value}' is not a package source with a version, line ignored");
                sources.Remove(appId);
                continue;
            }

            sources[appId] = source;
        }

        return new InstalledAppsReadResult(sources, warnings);
    }

    /// <summary>Records an installed package, keeping every other line exactly.</summary>
    /// <param name="content">Current machine file content, or <see langword="null"/> for a new file.</param>
    /// <param name="appId">Installed app.</param>
    /// <param name="source">Its source, pinned to the installed version.</param>
    public static string SetInstalled(string? content, string appId, AppPackageSource source) =>
        SetInstalled(content, appId, source, MachineDefaultContent);

    /// <summary>Records an installed package in any <c>[installed]</c> file (ADR 0189 §3), keeping every other line exactly.</summary>
    /// <param name="content">Current file content, or <see langword="null"/> for a new file.</param>
    /// <param name="appId">Installed app.</param>
    /// <param name="source">Its source, pinned to the installed version.</param>
    /// <param name="defaultContent">Content of a new file.</param>
    public static string SetInstalled(string? content, string appId, AppPackageSource source, string defaultContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Version is null)
        {
            throw new ArgumentException("An installed source carries its version.", nameof(source));
        }

        return ConfigDocumentEditor.SetValue(string.IsNullOrEmpty(content) ? defaultContent : content, InstalledSection, appId, source.ToString());
    }

    /// <summary>Forgets an installed package, keeping every other line exactly.</summary>
    /// <param name="content">Current machine file content.</param>
    /// <param name="appId">Removed app.</param>
    public static string RemoveInstalled(string? content, string appId) =>
        ConfigDocumentEditor.RemoveValue(content ?? string.Empty, InstalledSection, appId);

    /// <summary>
    /// Expands a set of uninstalled apps with every catalog app that depends on one of them through
    /// a chain of non-optional dependencies: such an app cannot run, so it counts as uninstalled too.
    /// </summary>
    /// <param name="catalog">Catalog of this build.</param>
    /// <param name="roots">Directly uninstalled app IDs.</param>
    public static HashSet<string> ExpandDependents(AppCatalog catalog, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        HashSet<string> closure = new(roots, StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (AppManifest manifest in catalog.Manifests.Values)
            {
                if (!closure.Contains(manifest.Id)
                    && manifest.Dependencies.Any(dependency => !dependency.Optional && closure.Contains(dependency.AppId)))
                {
                    closure.Add(manifest.Id);
                    changed = true;
                }
            }
        }

        return closure;
    }

    /// <summary>Gets <paramref name="appId"/> and every non-optional dependency it needs, transitively.</summary>
    /// <param name="catalog">Catalog of this build.</param>
    /// <param name="appId">App being reinstalled.</param>
    public static HashSet<string> ExpandDependencies(AppCatalog catalog, string appId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        HashSet<string> closure = new(StringComparer.Ordinal) { appId };
        Stack<string> pending = new([appId]);
        while (pending.Count > 0)
        {
            if (!catalog.Manifests.TryGetValue(pending.Pop(), out AppManifest? manifest))
            {
                continue;
            }

            foreach (AppDependencyManifest dependency in manifest.Dependencies.Where(static dependency => !dependency.Optional))
            {
                if (closure.Add(dependency.AppId))
                {
                    pending.Push(dependency.AppId);
                }
            }
        }

        return closure;
    }
}

/// <summary>Apps installed from a package, read from the <c>[installed]</c> section of the machine file.</summary>
/// <param name="Sources">Source pinned to its installed version, by app ID.</param>
/// <param name="Warnings">One <c>/var/log/syslog</c> line per ignored line.</param>
public sealed record InstalledAppsReadResult(IReadOnlyDictionary<string, AppPackageSource> Sources, IReadOnlyList<string> Warnings)
{
    /// <summary>Nothing installed, no warning.</summary>
    public static InstalledAppsReadResult Empty { get; } = new(new Dictionary<string, AppPackageSource>(StringComparer.Ordinal), []);
}

/// <summary>Uninstalled app IDs read from one <c>apps.config</c> file.</summary>
/// <param name="UninstalledAppIds">Known, non-system app IDs set to <c>true</c>.</param>
/// <param name="Warnings">One <c>/var/log/syslog</c> line per ignored line.</param>
public sealed record AppsConfigReadResult(IReadOnlySet<string> UninstalledAppIds, IReadOnlyList<string> Warnings)
{
    /// <summary>Nothing uninstalled, no warning.</summary>
    public static AppsConfigReadResult Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal), []);
}
