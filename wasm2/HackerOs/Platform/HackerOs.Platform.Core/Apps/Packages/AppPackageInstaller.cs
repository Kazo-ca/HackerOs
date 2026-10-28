using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>A downloaded and verified package, ready to be written (ADR 0182 §5, steps 1 to 4).</summary>
/// <param name="Source">Its source, pinned to the package's version.</param>
/// <param name="Package">The verified package.</param>
/// <param name="PreviousVersion">The version it replaces, or <see langword="null"/> for a new app.</param>
/// <param name="Trusted">Whether the source's owner is in <c>[trusted]</c>.</param>
public sealed record AppPackagePlan(AppPackageSource Source, AppPackage Package, SemanticVersion? PreviousVersion, bool Trusted);

/// <summary>
/// Installs and removes apps from their GitHub repository (ADR 0182 §5): reads the trusted
/// sources, downloads and verifies the package, then writes it under
/// <see cref="InstalledAppsLayout.Root"/> and in <c>[installed]</c> of <c>/etc/hackeros/apps.config</c>.
/// The app joins the catalog at the next start of the OS (Q5 answer C).
/// </summary>
/// <remarks>
/// It never authenticates anyone: the caller asks the authentication agent first and passes the
/// system writer it then received, as <c>apt</c> runs only after <c>sudo</c>.
/// </remarks>
public sealed class AppPackageInstaller
{
    private const ushort FileMode = 0b110_100_100;

    private readonly IAppPackageDownloader _downloader;
    private readonly IRuntimeApiSurface _runtime;
    private readonly SemanticVersion _sdkVersion;
    private readonly ILogger _logger;

    /// <summary>Creates the installer.</summary>
    /// <param name="downloader">Downloads the index and the package.</param>
    /// <param name="runtime">The loaded runtime the references are checked against (Q1); the page's own by default.</param>
    /// <param name="sdkVersion">SDK version of this OS; <see cref="HackerOsSdk.SemanticVersion"/> by default.</param>
    /// <param name="logger">Writes the <c>/var/log/syslog</c> lines.</param>
    public AppPackageInstaller(
        IAppPackageDownloader downloader,
        IRuntimeApiSurface? runtime = null,
        SemanticVersion? sdkVersion = null,
        ILogger<AppPackageInstaller>? logger = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _runtime = runtime ?? LoadedRuntimeApiSurface.Instance;
        _sdkVersion = sdkVersion ?? HackerOsSdk.SemanticVersion;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Reads <c>/etc/hackeros/app-sources.config</c>; its defaults when it does not exist.</summary>
    /// <param name="fileSystem">Any reader: the file is <c>0644</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<AppSourcesConfig> ReadSourcesAsync(IAppFileSystemGateway fileSystem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        SettingsFileReadResult file = await SettingsTextFile
            .ReadAsync(fileSystem, AppSourcesConfigDocument.MachinePath, cancellationToken)
            .ConfigureAwait(false);
        return AppSourcesConfigDocument.Read(file.Content);
    }

    /// <summary>Reads <c>[installed]</c> of <c>/etc/hackeros/apps.config</c>.</summary>
    /// <param name="fileSystem">Any reader: the file is <c>0644</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<InstalledAppsReadResult> ReadInstalledAsync(IAppFileSystemGateway fileSystem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        SettingsFileReadResult file = await SettingsTextFile
            .ReadAsync(fileSystem, AppsConfigDocument.MachinePath, cancellationToken)
            .ConfigureAwait(false);
        return AppsConfigDocument.ReadInstalled(file.Content, AppsConfigDocument.MachinePath);
    }

    /// <summary>Downloads the source's <c>index.json</c> from the GitHub host of <paramref name="sources"/>.</summary>
    /// <param name="source">The repository; its version is ignored.</param>
    /// <param name="sources">The trusted sources and hosts.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="AppPackageException"><c>apps.package.no-index</c>, <c>apps.index.invalid</c> or a download error.</exception>
    public async ValueTask<AppPackageIndex> GetIndexAsync(
        AppPackageSource source, AppSourcesConfig sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sources);
        Uri uri = source.GetIndexUri(sources.GitHubHost);
        byte[] bytes = await _downloader.DownloadAsync(uri, AppPackageIndex.MaximumBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new AppPackageException(
                "apps.package.no-index",
                $"{source.Name} publishes no package: {uri} does not exist (branch '{AppPackageSource.PackagesBranch}').");
        return AppPackageIndex.Parse(bytes);
    }

    /// <summary>
    /// Steps 1 to 3 of ADR 0182 §5: checks the trust, picks the version, downloads and verifies the
    /// package, then refuses a conflicting, incompatible or trimmed-out one. Nothing is written.
    /// </summary>
    /// <param name="source">The repository, with or without a version.</param>
    /// <param name="sources">The trusted sources and hosts.</param>
    /// <param name="allowUntrusted">Accepts an owner missing from <c>[trusted]</c>.</param>
    /// <param name="catalog">The running catalog: built-in apps and the apps installed at boot.</param>
    /// <param name="installed">The current <c>[installed]</c> section.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <exception cref="AppPackageException">The package is refused; <see cref="AppPackageException.Code"/> says why.</exception>
    public async ValueTask<AppPackagePlan> PrepareAsync(
        AppPackageSource source,
        AppSourcesConfig sources,
        bool allowUntrusted,
        AppCatalog catalog,
        InstalledAppsReadResult installed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installed);
        bool trusted = sources.IsTrusted(source);
        if (!trusted && !allowUntrusted)
        {
            throw new AppPackageException(
                "apps.package.untrusted",
                $"{source.TrustKey} is not a trusted source ({AppSourcesConfigDocument.MachinePath.Value}); add --allow-untrusted to install anyway.");
        }

        AppPackageIndex index = await GetIndexAsync(source, sources, cancellationToken).ConfigureAwait(false);
        AppPackageIndexEntry entry = (source.Version is { } wanted ? index.Find(wanted) : index.Latest)
            ?? throw new AppPackageException(
                "apps.package.version-not-found",
                source.Version is null
                    ? $"{source.Name} has published no version yet."
                    : $"{source.Name} has no version {source.Version}.");
        byte[] archive = await _downloader
            .DownloadAsync(source.GetFileUri(sources.GitHubHost, entry.Path), AppPackage.MaximumArchiveBytes, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AppPackageException("apps.package.download-failed", $"{entry.Path} is listed in the index of {source.Name} but missing.");
        if (!string.Equals(AppPackage.Sha256(archive), entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppPackageException("apps.package.hash-mismatch", $"{entry.Path} does not match the SHA-256 of the index of {source.Name}.");
        }

        AppPackage package = AppPackage.Read(archive);
        if (!string.Equals(package.AppId, index.AppId, StringComparison.Ordinal) || package.Version != entry.Version)
        {
            throw new AppPackageException(
                "apps.package.id-mismatch",
                $"{entry.Path} holds {package.AppId} {package.Version}, the index announces {index.AppId} {entry.Version}.");
        }

        AppPackageSource pinned = source.WithVersion(package.Version);
        SemanticVersion? previous = Check(package, pinned, catalog, installed);
        return new AppPackagePlan(pinned, package, previous, trusted);
    }

    /// <summary>
    /// Steps 2 and 3 of ADR 0182 §5 on an already verified package: returns the version it
    /// replaces, or <see langword="null"/> for a new app.
    /// </summary>
    /// <param name="package">The verified package.</param>
    /// <param name="source">Its source, pinned to its version.</param>
    /// <param name="catalog">The running catalog.</param>
    /// <param name="installed">The current <c>[installed]</c> section.</param>
    /// <exception cref="AppPackageException">The package is refused.</exception>
    public SemanticVersion? Check(AppPackage package, AppPackageSource source, AppCatalog catalog, InstalledAppsReadResult installed)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installed);
        AppManifest manifest = package.Manifest;
        SemanticVersion? previous = null;
        if (installed.Sources.TryGetValue(package.AppId, out AppPackageSource? current))
        {
            if (!current.IsSameRepository(source))
            {
                throw new AppPackageException(
                    "apps.package.other-source", $"{package.AppId} is already installed from {current.Name}; remove it first.");
            }

            if (current.Version == package.Version)
            {
                throw new AppPackageException("apps.package.already-installed", $"{package.AppId} {package.Version} is already installed.");
            }

            previous = current.Version;
        }
        else if (catalog.Manifests.ContainsKey(package.AppId) || catalog.Rejected.Any(rejection => rejection.AppId == package.AppId))
        {
            throw new AppPackageException("apps.package.built-in", $"{package.AppId} is an app of this OS; a package cannot replace it.");
        }

        if (!HackerOsSdk.IsCompatible(manifest.SdkCompatibility, _sdkVersion))
        {
            throw new AppPackageException(
                "apps.package.sdk-incompatible",
                $"{package.AppId} needs SDK {manifest.SdkCompatibility.MinimumVersion}"
                + (manifest.SdkCompatibility.MaximumVersion is { } maximum ? $" to {maximum}" : string.Empty)
                + $"; this OS has SDK {_sdkVersion}.");
        }

        // A Web Worker cannot load an assembly from the file system: installed apps run on the page.
        if (AppExecutionHosts.Of(manifest) != AppExecutionHost.MainThread)
        {
            throw new AppPackageException(
                "apps.package.worker-unsupported",
                $"{package.AppId} runs in a Web Worker ({AppExecutionHosts.Of(manifest)}); an installed app must declare Execution = MainThread.");
        }

        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(package.Assemblies, _runtime);
        if (missing.Count > 0)
        {
            throw new AppPackageException(
                "apps.package.missing-api",
                $"{package.AppId} uses APIs this OS does not have:{Environment.NewLine}"
                + string.Join(Environment.NewLine, missing.Select(static api => $"  {api.Member}")));
        }

        return previous;
    }

    /// <summary>
    /// Step 5 of ADR 0182 §5: writes the package to its folder (<c>root:root</c>, folders
    /// <c>0755</c>, files <c>0644</c>), records it in <c>[installed]</c>, then deletes the
    /// replaced version's folder.
    /// </summary>
    /// <param name="system">The system writer handed out after an administrator authenticated.</param>
    /// <param name="plan">The verified package.</param>
    /// <param name="by">Login name of the administrator, for the log.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <exception cref="IOException">A write failed; the previous version stays installed.</exception>
    public async ValueTask CommitAsync(IAppFileSystemGateway system, AppPackagePlan plan, string by, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(plan);
        AppPackage package = plan.Package;
        VirtualPath directory = InstalledAppsLayout.GetDirectory(package.AppId, package.Version);
        // A folder left by an interrupted install of the same version is replaced whole.
        await DeleteIfExistsAsync(system, directory, cancellationToken).ConfigureAwait(false);
        Ensure(await SettingsTextFile.EnsureDirectoryAsync(system, directory, cancellationToken).ConfigureAwait(false));
        foreach ((string relativePath, byte[] content) in package.Files.OrderBy(static file => file.Key, StringComparer.Ordinal))
        {
            await WriteFileAsync(system, VirtualPath.Parse($"{directory.Value}/{relativePath}"), content, cancellationToken).ConfigureAwait(false);
        }

        Ensure(await SettingsTextFile.EditAsync(
                system,
                AppsConfigDocument.MachinePath,
                content => AppsConfigDocument.SetInstalled(content, package.AppId, plan.Source),
                createIfMissing: true,
                cancellationToken)
            .ConfigureAwait(false));
        if (plan.PreviousVersion is { } previous && previous != package.Version)
        {
            await DeleteIfExistsAsync(system, InstalledAppsLayout.GetDirectory(package.AppId, previous), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "apps: {AppId} {Version} installed from {Source} (by={User})",
            package.AppId, package.Version, plan.Source.Name, by);
    }

    /// <summary>
    /// Removes an installed app (ADR 0182 §5): its <c>[installed]</c> line, then its folder. The
    /// user's settings stay; the app leaves the catalog at the next start of the OS.
    /// </summary>
    /// <param name="system">The system writer handed out after an administrator authenticated.</param>
    /// <param name="appId">The installed app.</param>
    /// <param name="by">Login name of the administrator, for the log.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The source it was installed from.</returns>
    /// <exception cref="AppPackageException"><c>apps.package.not-installed</c>.</exception>
    public async ValueTask<AppPackageSource> RemoveAsync(IAppFileSystemGateway system, string appId, string by, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        InstalledAppsReadResult installed = await ReadInstalledAsync(system, cancellationToken).ConfigureAwait(false);
        if (!installed.Sources.TryGetValue(appId, out AppPackageSource? source))
        {
            throw new AppPackageException("apps.package.not-installed", $"{appId} was not installed from a package.");
        }

        Ensure(await SettingsTextFile.EditAsync(
                system,
                AppsConfigDocument.MachinePath,
                content => AppsConfigDocument.RemoveInstalled(content, appId),
                createIfMissing: false,
                cancellationToken)
            .ConfigureAwait(false));
        await DeleteIfExistsAsync(system, InstalledAppsLayout.GetAppDirectory(appId), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("apps: {AppId} {Version} removed, installed from {Source} (by={User})", appId, source.Version, source.Name, by);
        return source;
    }

    private static async ValueTask WriteFileAsync(IAppFileSystemGateway system, VirtualPath path, byte[] content, CancellationToken cancellationToken)
    {
        string parent = path.Value[..path.Value.LastIndexOf('/')];
        Ensure(await SettingsTextFile.EnsureDirectoryAsync(system, VirtualPath.Parse(parent), cancellationToken).ConfigureAwait(false));
        FileSystemMutationResult created = await system
            .CreateAsync(new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(FileMode)), cancellationToken)
            .ConfigureAwait(false);
        Ensure(created.Succeeded ? null : created.Transaction.Error);
        FileSystemMutationResult written = await system
            .WriteAsync(new FileSystemWriteRequest(path), new BytesContentSource(content), cancellationToken)
            .ConfigureAwait(false);
        Ensure(written.Succeeded ? null : written.Transaction.Error);
    }

    private static async ValueTask DeleteIfExistsAsync(IAppFileSystemGateway system, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> entry = await system.StatAsync(new FileSystemStatRequest(path), cancellationToken).ConfigureAwait(false);
        if (!entry.Succeeded)
        {
            Ensure(entry.Error?.Code == FileSystemErrorCode.NotFound ? null : entry.Error);
            return;
        }

        VirtualPath parent = VirtualPath.Parse(path.Value[..path.Value.LastIndexOf('/')]);
        FileSystemResult<FileSystemEntrySnapshot> parentEntry = await system.StatAsync(new FileSystemStatRequest(parent), cancellationToken).ConfigureAwait(false);
        Ensure(parentEntry.Succeeded ? null : parentEntry.Error);
        FileSystemMutationResult deleted = await system.DeleteAsync(
                new FileSystemDeleteRequest(path, entry.Value!.Metadata.Revision, parentEntry.Value!.Metadata.Revision, recursive: true),
                cancellationToken)
            .ConfigureAwait(false);
        Ensure(deleted.Succeeded ? null : deleted.Transaction.Error);
    }

    private static void Ensure(FileSystemError? error)
    {
        if (error is not null)
        {
            throw new IOException($"{error.Path?.Value}: {error.Code}.");
        }
    }

    private sealed class BytesContentSource(byte[] content) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Binary();

        public long? Length => content.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }
}
