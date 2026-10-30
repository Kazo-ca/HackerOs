using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>For whom an app is installed (ADR 0189 §3, §4).</summary>
public enum AppPackageScope
{
    /// <summary>For oneself when allowed (trusted source, not blocked, not installed for the system), otherwise for the system.</summary>
    Auto = 0,

    /// <summary>For the session user only, in <c>~/.local/share/hackeros/apps</c>.</summary>
    User = 1,

    /// <summary>For every user, in <c>/usr/lib/hackeros/apps</c>, after the two-step approval.</summary>
    System = 2
}

/// <summary>A downloaded and verified package, ready to be written (ADR 0182 §5, steps 1 to 4).</summary>
/// <param name="Source">Its source, pinned to the package's version.</param>
/// <param name="Package">The verified package.</param>
/// <param name="PreviousVersion">The version it replaces, or <see langword="null"/> for a new app.</param>
/// <param name="Trusted">Whether the source is in <c>[trusted]</c>.</param>
public sealed record AppPackagePlan(AppPackageSource Source, AppPackage Package, SemanticVersion? PreviousVersion, bool Trusted)
{
    /// <summary>Gets the trust level of the source (ADR 0189 §2).</summary>
    public AppSourceTrust Trust { get; init; } = Trusted ? AppSourceTrust.Trusted : AppSourceTrust.Untrusted;

    /// <summary>Gets the home of the user it is installed for, or <see langword="null"/> for the whole system.</summary>
    public string? Home { get; init; }

    /// <summary>Gets whether it is installed for every user.</summary>
    public bool SystemWide => Home is null;

    /// <summary>Gets the block-list rule that matches the app, if any (ADR 0189 §5).</summary>
    public AppBlockReason? BlockReason { get; init; }

    /// <summary>
    /// Gets whether the two-step administrator approval of ADR 0189 §4 is needed: always for the
    /// system, and for a personal install from a source that is not trusted.
    /// </summary>
    public bool RequiresAdministrator => SystemWide || Trust == AppSourceTrust.Untrusted;

    /// <summary>Gets the size of the package's files, shown in the approval window.</summary>
    public long SizeBytes => Package.Files.Values.Sum(static file => (long)file.Length);
}

/// <summary>What <see cref="AppPackageInstaller.PrepareAsync"/> decides with (ADR 0189).</summary>
/// <param name="Sources">The sources and hosts.</param>
/// <param name="Policy">The administrator's block list.</param>
/// <param name="Scope">For whom the caller asked.</param>
/// <param name="Home">The session user's home, or <see langword="null"/> without a session.</param>
/// <param name="Catalog">The running catalog: built-in apps and the apps installed at boot.</param>
/// <param name="SystemInstalled">The <c>[installed]</c> section of <c>/etc/hackeros/apps.config</c>.</param>
/// <param name="PersonalInstalled">The session user's <c>[installed]</c> section.</param>
public sealed record AppPackageInstallContext(
    AppSourcesConfig Sources,
    AppPolicy Policy,
    AppPackageScope Scope,
    string? Home,
    AppCatalog Catalog,
    InstalledAppsReadResult SystemInstalled,
    InstalledAppsReadResult PersonalInstalled)
{
    /// <summary>Gets the catalog apps that came from a package rather than with the OS.</summary>
    public IReadOnlySet<string> PackagedAppIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Installs and removes apps from their GitHub repository (ADR 0182 §5, ADR 0189): reads the
/// sources and the block list, downloads and verifies the package, then writes it for the whole
/// system (<see cref="InstalledAppsLayout.Root"/>, <c>[installed]</c> of <c>/etc/hackeros/apps.config</c>)
/// or for one user (<c>~/.local/share/hackeros/apps</c>, <c>~/.config/hackeros/installed-apps.config</c>).
/// The app joins the catalog at the next start of the OS (Q5 answer C).
/// </summary>
/// <remarks>
/// It never authenticates anyone: the caller runs the two-step approval first when
/// <see cref="AppPackagePlan.RequiresAdministrator"/>, and passes the writer it then may use (the
/// system's, or the session user's for a personal install), as <c>apt</c> runs only after <c>sudo</c>.
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
    /// <param name="defaults">Sources and block list the host was built with; <see cref="AppPackageDefaults.Standard"/> by default.</param>
    public AppPackageInstaller(
        IAppPackageDownloader downloader,
        IRuntimeApiSurface? runtime = null,
        SemanticVersion? sdkVersion = null,
        ILogger<AppPackageInstaller>? logger = null,
        AppPackageDefaults? defaults = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _runtime = runtime ?? LoadedRuntimeApiSurface.Instance;
        _sdkVersion = sdkVersion ?? HackerOsSdk.SemanticVersion;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Defaults = defaults ?? AppPackageDefaults.Standard;
    }

    /// <summary>Gets the sources and block list the host was built with (ADR 0189 §1, §5).</summary>
    public AppPackageDefaults Defaults { get; }

    /// <summary>Reads <c>/etc/hackeros/app-sources.config</c>; the host's defaults when it does not exist.</summary>
    /// <param name="fileSystem">Any reader: the file is <c>0644</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<AppSourcesConfig> ReadSourcesAsync(IAppFileSystemGateway fileSystem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        SettingsFileReadResult file = await SettingsTextFile
            .ReadAsync(fileSystem, AppSourcesConfigDocument.MachinePath, cancellationToken)
            .ConfigureAwait(false);
        return AppSourcesConfigDocument.Read(file.Content, Defaults);
    }

    /// <summary>Reads <c>/etc/hackeros/app-policy.config</c>; the host's defaults when it does not exist.</summary>
    /// <param name="fileSystem">Any reader: the file is <c>0644</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<AppPolicy> ReadPolicyAsync(IAppFileSystemGateway fileSystem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        SettingsFileReadResult file = await SettingsTextFile
            .ReadAsync(fileSystem, AppPolicyConfigDocument.MachinePath, cancellationToken)
            .ConfigureAwait(false);
        return AppPolicyConfigDocument.Read(file.Content, Defaults);
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

    /// <summary>Reads <c>[installed]</c> of one user's <c>~/.config/hackeros/installed-apps.config</c>.</summary>
    /// <param name="fileSystem">The user's own reader, or the system's.</param>
    /// <param name="homeDirectory">The user's home.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<InstalledAppsReadResult> ReadPersonalInstalledAsync(
        IAppFileSystemGateway fileSystem, string homeDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        VirtualPath path = InstalledAppsLayout.GetPersonalConfigPath(homeDirectory);
        SettingsFileReadResult file = await SettingsTextFile.ReadAsync(fileSystem, path, cancellationToken).ConfigureAwait(false);
        return AppsConfigDocument.ReadInstalled(file.Content, path);
    }

    /// <summary>Lists the homes under <c>/home</c> whose user installed <paramref name="appId"/> for themselves.</summary>
    /// <param name="system">The system reader.</param>
    /// <param name="appId">The app.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask<IReadOnlyList<string>> FindPersonalCopiesAsync(
        IAppFileSystemGateway system, string appId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        FileSystemResult<FileSystemDirectorySnapshot> homes = await system
            .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(InstalledAppsLayout.HomesRoot)), cancellationToken)
            .ConfigureAwait(false);
        List<string> found = [];
        foreach (FileSystemDirectoryItem entry in homes.Succeeded ? homes.Value!.Entries : [])
        {
            if (entry.Metadata.Kind != FileSystemEntryKind.Directory)
            {
                continue;
            }

            string home = $"{InstalledAppsLayout.HomesRoot}/{entry.Name.Value}";
            InstalledAppsReadResult installed = await ReadPersonalInstalledAsync(system, home, cancellationToken).ConfigureAwait(false);
            if (installed.Sources.ContainsKey(appId))
            {
                found.Add(home);
            }
        }

        return found;
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
    /// Steps 1 to 3 of ADR 0182 §5 with the rules of ADR 0189: picks the version, downloads and
    /// verifies the package, chooses for whom it is installed, then refuses a conflicting,
    /// incompatible, blocked or trimmed-out one. Nothing is written.
    /// </summary>
    /// <param name="source">The repository, with or without a version.</param>
    /// <param name="request">The sources, the block list, the scope and what is installed already.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <exception cref="AppPackageException">The package is refused; <see cref="AppPackageException.Code"/> says why.</exception>
    public async ValueTask<AppPackagePlan> PrepareAsync(
        AppPackageSource source, AppPackageInstallContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        AppSourcesConfig sources = request.Sources;
        AppSourceTrust trust = sources.GetTrust(source);
        if (request.Scope == AppPackageScope.User && request.Home is null)
        {
            throw new AppPackageException("apps.package.no-session", "A personal install needs a signed-in user.");
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
        AppBlockReason? blocked = request.Policy.FindBlock(package.Manifest);
        bool systemWide = ChooseSystemWide(package.AppId, trust, blocked, request);
        SemanticVersion? previous = Check(
            package, pinned, request.Catalog, systemWide ? request.SystemInstalled : request.PersonalInstalled, request.PackagedAppIds);
        return new AppPackagePlan(pinned, package, previous, trust != AppSourceTrust.Untrusted)
        {
            Trust = trust,
            Home = systemWide ? null : request.Home,
            BlockReason = blocked,
        };
    }

    /// <summary>
    /// ADR 0189 §3 to §5: an app already installed for the system stays there (one copy, Q27); a
    /// blocked app cannot be installed for oneself; <see cref="AppPackageScope.Auto"/> picks the
    /// personal install whenever it is allowed.
    /// </summary>
    private static bool ChooseSystemWide(string appId, AppSourceTrust trust, AppBlockReason? blocked, AppPackageInstallContext request)
    {
        bool forSystem = request.SystemInstalled.Sources.ContainsKey(appId);
        switch (request.Scope)
        {
            case AppPackageScope.System:
                return true;
            case AppPackageScope.User when forSystem:
                throw new AppPackageException(
                    "apps.package.installed-for-system",
                    $"{appId} is installed for every user; one copy at a time (update it with --system).");
            case AppPackageScope.User when blocked is not null:
                throw new AppPackageException(
                    "apps.package.blocked",
                    $"{appId} is blocked for a personal install by {AppPolicyConfigDocument.MachinePath.Value} ({blocked}); an administrator can install it with --system.");
            case AppPackageScope.User:
                return false;
            default:
                // A blocked app goes to the system (its personal copies leave then); an untrusted
                // one too unless it is a personal copy being updated: either way an administrator approves.
                return request.Home is null
                    || forSystem
                    || blocked is not null
                    || (!request.PersonalInstalled.Sources.ContainsKey(appId) && trust == AppSourceTrust.Untrusted);
        }
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
    /// <param name="packagedAppIds">Catalog apps that came from a package (another user's personal apps among them), not built in.</param>
    public SemanticVersion? Check(
        AppPackage package, AppPackageSource source, AppCatalog catalog, InstalledAppsReadResult installed, IReadOnlySet<string>? packagedAppIds = null)
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
        else if (packagedAppIds?.Contains(package.AppId) != true
            && (catalog.Manifests.ContainsKey(package.AppId) || catalog.Rejected.Any(rejection => rejection.AppId == package.AppId)))
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
    /// Step 5 of ADR 0182 §5: writes the package to its folder (folders <c>0755</c>, files
    /// <c>0644</c>), records it in the <c>[installed]</c> section of its scope, then deletes the
    /// replaced version's folder. For the system, every personal copy listed in
    /// <paramref name="personalCopies"/> is removed afterwards (one copy at a time, ADR 0189 §3);
    /// their users' settings stay.
    /// </summary>
    /// <param name="writer">
    /// The system writer handed out after the two-step approval for a system install; the session
    /// user's own writer for a personal one.
    /// </param>
    /// <param name="plan">The verified package.</param>
    /// <param name="by">Login name of the user or administrator, for the log.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="personalCopies">Homes whose personal copy a system install replaces.</param>
    /// <exception cref="IOException">A write failed; the previous version stays installed.</exception>
    public async ValueTask CommitAsync(
        IAppFileSystemGateway writer,
        AppPackagePlan plan,
        string by,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? personalCopies = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(plan);
        AppPackage package = plan.Package;
        VirtualPath directory = GetDirectory(plan.Home, package.AppId, package.Version);
        // A folder left by an interrupted install of the same version is replaced whole.
        await DeleteIfExistsAsync(writer, directory, cancellationToken).ConfigureAwait(false);
        Ensure(await SettingsTextFile.EnsureDirectoryAsync(writer, directory, cancellationToken).ConfigureAwait(false));
        foreach ((string relativePath, byte[] content) in package.Files.OrderBy(static file => file.Key, StringComparer.Ordinal))
        {
            await WriteFileAsync(writer, VirtualPath.Parse($"{directory.Value}/{relativePath}"), content, cancellationToken).ConfigureAwait(false);
        }

        VirtualPath config = plan.Home is null ? AppsConfigDocument.MachinePath : InstalledAppsLayout.GetPersonalConfigPath(plan.Home);
        string defaultContent = plan.Home is null ? AppsConfigDocument.MachineDefaultContent : InstalledAppsLayout.PersonalConfigDefaultContent;
        if (plan.Home is not null)
        {
            Ensure(await SettingsTextFile.EnsureDirectoryAsync(writer, SettingsTextFile.GetParent(config), cancellationToken).ConfigureAwait(false));
        }

        Ensure(await SettingsTextFile.EditAsync(
                writer,
                config,
                content => AppsConfigDocument.SetInstalled(content, package.AppId, plan.Source, defaultContent),
                createIfMissing: true,
                cancellationToken)
            .ConfigureAwait(false));
        if (plan.PreviousVersion is { } previous && previous != package.Version)
        {
            await DeleteIfExistsAsync(writer, GetDirectory(plan.Home, package.AppId, previous), cancellationToken).ConfigureAwait(false);
        }

        if (plan.Home is null)
        {
            foreach (string home in personalCopies ?? [])
            {
                await RemovePersonalCopyAsync(writer, home, package.AppId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("apps: {AppId} personal copy of {Home} removed, now installed for the system", package.AppId, home);
            }
        }

        _logger.LogInformation(
            "apps: {AppId} {Version} installed for {Scope} from {Source} (trust={Trust}, by={User})",
            package.AppId, package.Version, plan.Home ?? "the system", plan.Source.Name, plan.Trust, by);
    }

    /// <summary>
    /// Removes an installed app (ADR 0182 §5, ADR 0189 §3): its <c>[installed]</c> line, then its
    /// folder. The user's settings stay; the app leaves the catalog at the next start of the OS.
    /// </summary>
    /// <param name="writer">The system writer after the two-step approval, or the user's own writer for a personal app.</param>
    /// <param name="appId">The installed app.</param>
    /// <param name="by">Login name of the user or administrator, for the log.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="home">The user's home for a personal app, or <see langword="null"/> for the system.</param>
    /// <returns>The source it was installed from.</returns>
    /// <exception cref="AppPackageException"><c>apps.package.not-installed</c>.</exception>
    public async ValueTask<AppPackageSource> RemoveAsync(
        IAppFileSystemGateway writer, string appId, string by, CancellationToken cancellationToken = default, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        InstalledAppsReadResult installed = home is null
            ? await ReadInstalledAsync(writer, cancellationToken).ConfigureAwait(false)
            : await ReadPersonalInstalledAsync(writer, home, cancellationToken).ConfigureAwait(false);
        if (!installed.Sources.TryGetValue(appId, out AppPackageSource? source))
        {
            throw new AppPackageException(
                "apps.package.not-installed",
                home is null ? $"{appId} was not installed for the system from a package." : $"{appId} was not installed for you from a package.");
        }

        if (home is null)
        {
            Ensure(await SettingsTextFile.EditAsync(
                    writer,
                    AppsConfigDocument.MachinePath,
                    content => AppsConfigDocument.RemoveInstalled(content, appId),
                    createIfMissing: false,
                    cancellationToken)
                .ConfigureAwait(false));
            await DeleteIfExistsAsync(writer, InstalledAppsLayout.GetAppDirectory(appId), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RemovePersonalCopyAsync(writer, home, appId, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "apps: {AppId} {Version} removed for {Scope}, installed from {Source} (by={User})",
            appId, source.Version, home ?? "the system", source.Name, by);
        return source;
    }

    private static VirtualPath GetDirectory(string? home, string appId, SemanticVersion version) =>
        home is null ? InstalledAppsLayout.GetDirectory(appId, version) : InstalledAppsLayout.GetPersonalDirectory(home, appId, version);

    private static async ValueTask RemovePersonalCopyAsync(IAppFileSystemGateway writer, string home, string appId, CancellationToken cancellationToken)
    {
        Ensure(await SettingsTextFile.EditAsync(
                writer,
                InstalledAppsLayout.GetPersonalConfigPath(home),
                content => AppsConfigDocument.RemoveInstalled(content, appId),
                createIfMissing: false,
                cancellationToken)
            .ConfigureAwait(false));
        await DeleteIfExistsAsync(writer, InstalledAppsLayout.GetPersonalAppDirectory(home, appId), cancellationToken).ConfigureAwait(false);
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
