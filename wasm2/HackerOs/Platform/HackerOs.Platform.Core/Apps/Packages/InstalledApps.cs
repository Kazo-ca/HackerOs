using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>Reads one file's bytes, or <see langword="null"/> when it does not exist.</summary>
/// <param name="path">Absolute path.</param>
/// <param name="cancellationToken">Cancels the read.</param>
public delegate ValueTask<byte[]?> InstalledAppFileReader(VirtualPath path, CancellationToken cancellationToken);

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
    /// <summary>Gets the app identifier.</summary>
    public string AppId => Manifest.Id;

    /// <summary>Gets the installed version.</summary>
    public SemanticVersion Version => Source.Version ?? SemanticVersion.Parse(Manifest.Version);

    /// <summary>Gets the folder of this version.</summary>
    public VirtualPath Directory => InstalledAppsLayout.GetDirectory(AppId, Version);

    /// <summary>Gets the assembly file names the manifest declares (all in <c>lib/</c>).</summary>
    public IReadOnlySet<string> AssemblyNames => AppPackage.GetDeclaredAssemblies(Manifest);

    /// <summary>Gets the path of one assembly.</summary>
    public VirtualPath GetAssemblyPath(string fileName) => VirtualPath.Parse($"{Directory.Value}/{AppPackage.LibraryFolder}{fileName}");

    /// <summary>Gets the path of one declared asset.</summary>
    public VirtualPath GetAssetPath(string assetPath) => VirtualPath.Parse($"{Directory.Value}/{AppPackage.AssetFolder}{assetPath}");
}

/// <summary>
/// The apps installed from packages, read at boot from <c>[installed]</c> of
/// <c>/etc/hackeros/apps.config</c> and each app's <c>manifest.json</c> (ADR 0182 §5, Q5 answer C:
/// they join the catalog when the OS starts). A broken entry is skipped with a warning, never fatal.
/// </summary>
/// <param name="Apps">Intact installed apps, ordered by app ID.</param>
/// <param name="Warnings">One <c>/var/log/syslog</c> line per skipped entry.</param>
public sealed record InstalledAppsImage(IReadOnlyList<InstalledApp> Apps, IReadOnlyList<string> Warnings)
{
    /// <summary>No installed app.</summary>
    public static InstalledAppsImage Empty { get; } = new([], []);

    /// <summary>Reads the installed apps.</summary>
    /// <param name="read">Reads a file as the system.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<InstalledAppsImage> LoadAsync(InstalledAppFileReader read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        byte[]? config = await read(AppsConfigDocument.MachinePath, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult installed = AppsConfigDocument.ReadInstalled(
            config is null ? null : Encoding.UTF8.GetString(config), AppsConfigDocument.MachinePath);
        List<string> warnings = [.. installed.Warnings];
        List<InstalledApp> apps = [];
        foreach ((string appId, AppPackageSource source) in installed.Sources.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            VirtualPath manifestPath = VirtualPath.Parse($"{InstalledAppsLayout.GetDirectory(appId, source.Version!.Value).Value}/{AppPackage.ManifestFileName}");
            byte[]? bytes = await read(manifestPath, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                warnings.Add($"apps: {appId} is listed as installed but {manifestPath.Value} is missing, app skipped");
                continue;
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
                continue;
            }

            if (!string.Equals(manifest.Id, appId, StringComparison.Ordinal)
                || !SemanticVersion.TryParse(manifest.Version, out SemanticVersion version)
                || version != source.Version)
            {
                warnings.Add($"apps: {manifestPath.Value} describes {manifest.Id} {manifest.Version}, not {appId} {source.Version}, app skipped");
                continue;
            }

            apps.Add(new InstalledApp(source, manifest));
        }

        return new InstalledAppsImage(apps, warnings);
    }
}
