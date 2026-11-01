using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>Reads one file's bytes, or <see langword="null"/> when it does not exist.</summary>
/// <param name="path">Absolute path.</param>
/// <param name="cancellationToken">Cancels the read.</param>
public delegate ValueTask<byte[]?> InstalledAppFileReader(VirtualPath path, CancellationToken cancellationToken);

/// <summary>Lists the users' home folders searched for personal installs (ADR 0189 §3).</summary>
/// <param name="cancellationToken">Cancels the listing.</param>
public delegate ValueTask<IReadOnlyList<string>> InstalledAppHomeLister(CancellationToken cancellationToken);

/// <summary>Where installed packages live (ADR 0182 §5): <c>/usr/lib/hackeros/apps/&lt;appId&gt;/&lt;version&gt;/</c>.</summary>
public static class InstalledAppsLayout
{
    /// <summary>Root of every installed package, <c>root:root 0755</c>.</summary>
    public const string Root = "/usr/lib/hackeros/apps";

    /// <summary>Gets the folder of one installed version.</summary>
    public static VirtualPath GetDirectory(string appId, SemanticVersion version) =>
        VirtualPath.Parse($"{Root}/{appId}/{version}");

    /// <summary>Gets the folder of every version of one app.</summary>
    public static VirtualPath GetAppDirectory(string appId) => VirtualPath.Parse($"{Root}/{appId}");

    /// <summary>Folder of the users' homes searched for personal installs at boot.</summary>
    public const string HomesRoot = "/home";

    /// <summary>Path of the personal apps under a home (ADR 0189 §3).</summary>
    public const string PersonalRootUnderHome = ".local/share/hackeros/apps";

    /// <summary>Path of the personal <c>[installed]</c> file under a home (ADR 0189 §3).</summary>
    public const string PersonalConfigUnderHome = ".config/hackeros/installed-apps.config";

    /// <summary>Content of a new personal <c>installed-apps.config</c>.</summary>
    public const string PersonalConfigDefaultContent =
        "# Apps you installed for yourself (ADR 0189 section 3), read when your session opens.\n"
        + "# Use hackpkg install --user | remove --user to change this list.\n"
        + "[" + AppsConfigDocument.InstalledSection + "]\n";

    /// <summary>Gets the root of one user's personal apps, <c>~/.local/share/hackeros/apps</c>.</summary>
    /// <param name="homeDirectory">The user's home.</param>
    public static VirtualPath GetPersonalRoot(string homeDirectory) => VirtualPath.Parse($"{Trim(homeDirectory)}/{PersonalRootUnderHome}");

    /// <summary>Gets the folder of one personal version.</summary>
    public static VirtualPath GetPersonalDirectory(string homeDirectory, string appId, SemanticVersion version) =>
        VirtualPath.Parse($"{GetPersonalRoot(homeDirectory).Value}/{appId}/{version}");

    /// <summary>Gets the folder of every personal version of one app.</summary>
    public static VirtualPath GetPersonalAppDirectory(string homeDirectory, string appId) =>
        VirtualPath.Parse($"{GetPersonalRoot(homeDirectory).Value}/{appId}");

    /// <summary>Gets one user's <c>~/.config/hackeros/installed-apps.config</c>.</summary>
    public static VirtualPath GetPersonalConfigPath(string homeDirectory) => VirtualPath.Parse($"{Trim(homeDirectory)}/{PersonalConfigUnderHome}");

    /// <summary>
    /// Lists the homes under <c>/home</c> straight from a file-system provider, as the system: the
    /// boot reads every user's personal installs before any session exists (ADR 0189 §3).
    /// </summary>
    /// <param name="provider">The provider holding <c>/home</c>.</param>
    public static InstalledAppHomeLister CreateHomeLister(IFileSystemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return async cancellationToken =>
        {
            FileSystemAuthorizationContext context = new(OsSettingsService.CreateSystemOperation(), ["system"], DateTimeOffset.UtcNow);
            FileSystemResult<FileSystemDirectorySnapshot> listing = await provider
                .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(HomesRoot)), context, cancellationToken)
                .ConfigureAwait(false);
            return !listing.Succeeded || listing.Value is null
                ? []
                : [.. listing.Value.Entries
                    .Where(static entry => entry.Metadata.Kind == FileSystemEntryKind.Directory)
                    .Select(static entry => $"{HomesRoot}/{entry.Name.Value}")];
        };
    }

    private static string Trim(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return homeDirectory.TrimEnd('/');
    }

    /// <summary>
    /// Reads files straight from a file-system provider, as the system: installed packages are
    /// <c>root:root</c> files every user may read, and the boot reads them before any session exists.
    /// </summary>
    /// <param name="provider">The provider holding <see cref="Root"/> and <c>/etc</c>.</param>
    public static InstalledAppFileReader CreateReader(IFileSystemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return async (path, cancellationToken) =>
        {
            FileSystemAuthorizationContext context = new(OsSettingsService.CreateSystemOperation(), ["system"], DateTimeOffset.UtcNow);
            FileSystemResult<FileSystemContentReadHandle> result = await provider
                .ReadAsync(new FileSystemReadRequest(path), context, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded || result.Value is null)
            {
                return null;
            }

            await using FileSystemContentReadHandle handle = result.Value;
            using MemoryStream buffer = new();
            await handle.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        };
    }
}

/// <summary>One app installed from a package and found intact at boot.</summary>
/// <param name="Source">Where it came from, pinned to its version.</param>
/// <param name="Manifest">Its manifest, read from its folder.</param>
public sealed record InstalledApp(AppPackageSource Source, AppManifest Manifest)
{
    /// <summary>
    /// Gets the homes of the users who installed it for themselves (ADR 0189 §3); empty for an app
    /// installed for the whole system. Only those users' sessions see a personal app.
    /// </summary>
    public IReadOnlySet<string> OwnerHomes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets the home whose copy the OS loads, or <see langword="null"/> for a system install.</summary>
    public string? FilesHome { get; init; }

    /// <summary>Gets whether the app was installed for the whole system.</summary>
    public bool IsSystemWide => FilesHome is null;

    /// <summary>Gets the app identifier.</summary>
    public string AppId => Manifest.Id;

    /// <summary>Gets the installed version.</summary>
    public SemanticVersion Version => Source.Version ?? SemanticVersion.Parse(Manifest.Version);

    /// <summary>Gets the folder of this version.</summary>
    public VirtualPath Directory => FilesHome is null
        ? InstalledAppsLayout.GetDirectory(AppId, Version)
        : InstalledAppsLayout.GetPersonalDirectory(FilesHome, AppId, Version);

    /// <summary>Gets the assembly file names the manifest declares (all in <c>lib/</c>).</summary>
    public IReadOnlySet<string> AssemblyNames => AppPackage.GetDeclaredAssemblies(Manifest);

    /// <summary>Gets the path of one assembly.</summary>
    public VirtualPath GetAssemblyPath(string fileName) => VirtualPath.Parse($"{Directory.Value}/{AppPackage.LibraryFolder}{fileName}");

    /// <summary>
    /// Gets the path of the satellite assembly that translates one of its assemblies into
    /// <paramref name="culture"/>: <c>lib/&lt;culture&gt;/&lt;assembly&gt;.resources.dll</c> (ADR 0222 §1).
    /// </summary>
    /// <param name="culture">Culture name, e.g. <c>fr</c>.</param>
    /// <param name="assemblyFileName">The translated assembly, e.g. <c>Kazo.Webamp.dll</c>.</param>
    public VirtualPath GetSatellitePath(string culture, string assemblyFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyFileName);
        return VirtualPath.Parse($"{Directory.Value}/{AppPackage.LibraryFolder}{culture}/{Path.GetFileNameWithoutExtension(assemblyFileName)}{AppPackage.SatelliteSuffix}");
    }

    /// <summary>Gets the path of one declared asset.</summary>
    public VirtualPath GetAssetPath(string assetPath) => VirtualPath.Parse($"{Directory.Value}/{AppPackage.AssetFolder}{assetPath}");
}

/// <summary>
/// The apps installed from packages, read at boot from <c>[installed]</c> of
/// <c>/etc/hackeros/apps.config</c>, of each user's <c>~/.config/hackeros/installed-apps.config</c>
/// (ADR 0189 §3), and each app's <c>manifest.json</c> (ADR 0182 §5, Q5 answer C: they join the
/// catalog when the OS starts). A broken entry is skipped with a warning, never fatal.
/// </summary>
/// <remarks>
/// The page runs one catalog for every session, so the boot reads every user's personal apps; a
/// session sees only the system apps and its own (<see cref="InstalledPackageCatalog"/>). One app ID
/// has one copy: a system install wins over personal ones, and when several users installed the same
/// app for themselves, the newest version is loaded for all of them.
/// </remarks>
/// <param name="Apps">Intact installed apps, ordered by app ID.</param>
/// <param name="Warnings">One <c>/var/log/syslog</c> line per skipped entry.</param>
public sealed record InstalledAppsImage(IReadOnlyList<InstalledApp> Apps, IReadOnlyList<string> Warnings)
{
    /// <summary>No installed app.</summary>
    public static InstalledAppsImage Empty { get; } = new([], []);

    /// <summary>Reads the installed apps.</summary>
    /// <param name="read">Reads a file as the system.</param>
    /// <param name="listHomes">Lists the users' homes; <see langword="null"/> reads the system installs only.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<InstalledAppsImage> LoadAsync(
        InstalledAppFileReader read, InstalledAppHomeLister? listHomes = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        List<string> warnings = [];
        List<InstalledApp> apps = [];
        InstalledAppsReadResult system = await ReadConfigAsync(read, AppsConfigDocument.MachinePath, warnings, cancellationToken).ConfigureAwait(false);
        foreach ((string appId, AppPackageSource source) in system.Sources.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (await ReadAppAsync(read, appId, source, null, warnings, cancellationToken).ConfigureAwait(false) is { } app)
            {
                apps.Add(app);
            }
        }

        IReadOnlyList<string> homes = listHomes is null ? [] : await listHomes(cancellationToken).ConfigureAwait(false);
        Dictionary<string, List<(string Home, AppPackageSource Source)>> personal = new(StringComparer.Ordinal);
        foreach (string home in homes.Order(StringComparer.Ordinal))
        {
            VirtualPath path = InstalledAppsLayout.GetPersonalConfigPath(home);
            InstalledAppsReadResult installed = await ReadConfigAsync(read, path, warnings, cancellationToken).ConfigureAwait(false);
            foreach ((string appId, AppPackageSource source) in installed.Sources)
            {
                if (system.Sources.ContainsKey(appId))
                {
                    warnings.Add($"apps: {appId} in {path.Value} is also installed for the system, personal copy skipped");
                    continue;
                }

                if (!personal.TryGetValue(appId, out List<(string Home, AppPackageSource Source)>? copies))
                {
                    personal[appId] = copies = [];
                }

                copies.Add((home, source));
            }
        }

        foreach ((string appId, List<(string Home, AppPackageSource Source)> copies) in personal.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            (string home, AppPackageSource source) = copies.OrderByDescending(static copy => copy.Source.Version!.Value).First();
            if (copies.Count > 1)
            {
                warnings.Add($"apps: {appId} is installed by {copies.Count} users, {source.Version} from {home} is loaded for all of them");
            }

            if (await ReadAppAsync(read, appId, source, home, warnings, cancellationToken).ConfigureAwait(false) is { } app)
            {
                apps.Add(app with { OwnerHomes = copies.Select(static copy => copy.Home).ToHashSet(StringComparer.Ordinal) });
            }
        }

        return new InstalledAppsImage([.. apps.OrderBy(static app => app.AppId, StringComparer.Ordinal)], warnings);
    }

    private static async Task<InstalledAppsReadResult> ReadConfigAsync(
        InstalledAppFileReader read, VirtualPath path, List<string> warnings, CancellationToken cancellationToken)
    {
        byte[]? config = await read(path, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult installed = AppsConfigDocument.ReadInstalled(config is null ? null : Encoding.UTF8.GetString(config), path);
        warnings.AddRange(installed.Warnings);
        return installed;
    }

    private static async Task<InstalledApp?> ReadAppAsync(
        InstalledAppFileReader read, string appId, AppPackageSource source, string? home, List<string> warnings, CancellationToken cancellationToken)
    {
        VirtualPath directory = home is null
            ? InstalledAppsLayout.GetDirectory(appId, source.Version!.Value)
            : InstalledAppsLayout.GetPersonalDirectory(home, appId, source.Version!.Value);
        VirtualPath manifestPath = VirtualPath.Parse($"{directory.Value}/{AppPackage.ManifestFileName}");
        byte[]? bytes = await read(manifestPath, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            warnings.Add($"apps: {appId} is listed as installed but {manifestPath.Value} is missing, app skipped");
            return null;
        }

        AppManifest manifest;
        try
        {
            manifest = AppManifestJsonSerializer.NormalizeOptionalCollections(
                AppManifestJsonSerializer.DeserializeStrict(Encoding.UTF8.GetString(bytes)));
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            warnings.Add($"apps: {manifestPath.Value} is not a valid manifest ({exception.Message}), app skipped");
            return null;
        }

        if (!string.Equals(manifest.Id, appId, StringComparison.Ordinal)
            || !SemanticVersion.TryParse(manifest.Version, out SemanticVersion version)
            || version != source.Version)
        {
            warnings.Add($"apps: {manifestPath.Value} describes {manifest.Id} {manifest.Version}, not {appId} {source.Version}, app skipped");
            return null;
        }

        return new InstalledApp(source, manifest) { FilesHome = home };
    }
}
