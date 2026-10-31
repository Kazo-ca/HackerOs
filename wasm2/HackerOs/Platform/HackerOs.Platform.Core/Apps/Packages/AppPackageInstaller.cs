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

    /// <summary>
    /// Gets whether a key of <c>[signing-keys]</c> verified the package's signature (ADR 0214 §2);
    /// <see langword="false"/> for a source without a key, whose package is checked by its SHA-256 only.
    /// </summary>
    public bool Signed { get; init; }

    /// <summary>Gets the size of the package's files, shown in the approval window.</summary>
    public long SizeBytes => Package.Files.Values.Sum(static file => (long)file.Length);
}

/// <summary>A newer version published by the source of an installed app (ADR 0188 §3).</summary>
/// <param name="AppId">The installed app.</param>
/// <param name="Installed">Its source, pinned to the installed version.</param>
/// <param name="Available">The newer version.</param>
/// <param name="SystemWide">Whether the installed copy is for every user.</param>
public sealed record AppPackageUpdate(string AppId, AppPackageSource Installed, SemanticVersion Available, bool SystemWide);

/// <summary>What <see cref="AppPackageInstaller.FindUpdatesAsync"/> found.</summary>
/// <param name="Updates">The updates, in app ID order.</param>
/// <param name="Failures">Sources that could not be read, one line each.</param>
public sealed record AppPackageUpdateReport(IReadOnlyList<AppPackageUpdate> Updates, IReadOnlyList<string> Failures);

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
    private readonly IAppPackageSignatureVerifier _signatures;
    private readonly IRuntimeApiSurface _runtime;
    private readonly SemanticVersion _sdkVersion;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _catalogGate = new();
    private readonly Dictionary<Uri, (DateTimeOffset At, AppCatalogListing Listing)> _catalogs = [];

    /// <summary>How long a downloaded <c>catalog.json</c> is reused before it is downloaded again.</summary>
    public static readonly TimeSpan CatalogCacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Creates the installer.</summary>
    /// <param name="downloader">Downloads the index and the package.</param>
    /// <param name="runtime">The loaded runtime the references are checked against (Q1); the page's own by default.</param>
    /// <param name="sdkVersion">SDK version of this OS; <see cref="HackerOsSdk.SemanticVersion"/> by default.</param>
    /// <param name="logger">Writes the <c>/var/log/syslog</c> lines.</param>
    /// <param name="defaults">Sources and block list the host was built with; <see cref="AppPackageDefaults.Standard"/> by default.</param>
    /// <param name="clock">Clock of the catalog cache; the system clock by default.</param>
    /// <param name="signatures">
    /// Checks package signatures (ADR 0214 §2): Web Crypto in the browser,
    /// <see cref="ManagedAppPackageSignatureVerifier"/> by default.
    /// </param>
    public AppPackageInstaller(
        IAppPackageDownloader downloader,
        IRuntimeApiSurface? runtime = null,
        SemanticVersion? sdkVersion = null,
        ILogger<AppPackageInstaller>? logger = null,
        AppPackageDefaults? defaults = null,
        Func<DateTimeOffset>? clock = null,
        IAppPackageSignatureVerifier? signatures = null)
    {
        _signatures = signatures ?? ManagedAppPackageSignatureVerifier.Instance;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
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

    /// <summary>
    /// Downloads the source's <c>index.json</c> from the GitHub host of <paramref name="sources"/>; for
    /// a catalog app, its versions in the catalog's <c>catalog.json</c> (ADR 0212 §1).
    /// </summary>
    /// <param name="source">The repository or catalog app; its version is ignored.</param>
    /// <param name="sources">The trusted sources and hosts.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="AppPackageException"><c>apps.package.no-index</c>, <c>apps.index.invalid</c> or a download error.</exception>
    public async ValueTask<AppPackageIndex> GetIndexAsync(
        AppPackageSource source, AppSourcesConfig sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sources);
        if (source.IsCatalog)
        {
            return (await GetCatalogEntryAsync(source, sources, cancellationToken).ConfigureAwait(false)).Entry.ToIndex();
        }

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
        Uri packageUri = source.IsCatalog
            ? (await GetCatalogEntryAsync(source, sources, cancellationToken).ConfigureAwait(false)).Listing.Resolve(entry.Path)
            : source.GetFileUri(sources.GitHubHost, entry.Path);
        byte[] archive = await _downloader
            .DownloadAsync(packageUri, AppPackage.MaximumArchiveBytes, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AppPackageException("apps.package.download-failed", $"{entry.Path} is listed in the index of {source.Name} but missing.");
        if (!string.Equals(AppPackage.Sha256(archive), entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppPackageException("apps.package.hash-mismatch", $"{entry.Path} does not match the SHA-256 of the index of {source.Name}.");
        }

        bool signed = await VerifySignatureAsync(source, sources, entry, archive, cancellationToken).ConfigureAwait(false);
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
            Signed = signed,
        };
    }

    /// <summary>
    /// ADR 0214 §2 (Q2 answer A): a source with keys in <c>[signing-keys]</c> must have signed the
    /// package with one of them; a source without keys is accepted on its SHA-256 alone. Runs before
    /// the package is even opened, and before any administrator is asked.
    /// </summary>
    /// <returns>Whether a key verified the signature.</returns>
    /// <exception cref="AppPackageException"><c>apps.package.unsigned</c> or <c>apps.package.bad-signature</c>.</exception>
    private async ValueTask<bool> VerifySignatureAsync(
        AppPackageSource source, AppSourcesConfig sources, AppPackageIndexEntry entry, byte[] archive, CancellationToken cancellationToken)
    {
        IReadOnlyList<byte[]> keys = sources.GetSigningKeys(source);
        if (keys.Count == 0)
        {
            return false;
        }

        if (entry.Signature is not { } signature)
        {
            throw new AppPackageException(
                "apps.package.unsigned",
                $"{entry.Path} of {source.Name} is not signed, but {AppSourcesConfigDocument.MachinePath.Value} requires a signature from this source.");
        }

        foreach (byte[] key in keys)
        {
            if (await _signatures.VerifyAsync(key, archive, signature, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw new AppPackageException(
            "apps.package.bad-signature",
            $"The signature of {entry.Path} of {source.Name} does not match any key of {AppSourcesConfigDocument.MachinePath.Value}: the package was not published by this source.");
    }

    /// <summary>
    /// Downloads one catalog of <c>[catalogs]</c> (ADR 0188 §6), or reuses the copy downloaded less
    /// than <see cref="CatalogCacheLifetime"/> ago.
    /// </summary>
    /// <param name="name">Catalog name.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <param name="refresh">Downloads again even when a recent copy exists.</param>
    /// <exception cref="AppPackageException"><c>apps.catalog.not-found</c>, <c>apps.catalog.download-failed</c>, an invalid catalog or a download error.</exception>
    public async ValueTask<AppCatalogListing> GetCatalogAsync(
        string name, AppSourcesConfig sources, CancellationToken cancellationToken = default, bool refresh = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sources);
        if (!sources.Catalogs.TryGetValue(name, out Uri? address))
        {
            throw new AppPackageException(
                "apps.catalog.not-found", $"There is no catalog '{name}' in {AppSourcesConfigDocument.MachinePath.Value}.");
        }

        DateTimeOffset now = _clock();
        lock (_catalogGate)
        {
            if (!refresh && _catalogs.TryGetValue(address, out (DateTimeOffset At, AppCatalogListing Listing) cached)
                && now - cached.At < CatalogCacheLifetime)
            {
                return cached.Listing with { Name = name };
            }
        }

        byte[] bytes = await _downloader.DownloadAsync(address, AppCatalogDocument.MaximumBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new AppPackageException("apps.catalog.download-failed", $"The catalog '{name}' does not exist: {address} answered 404.");
        AppCatalogListing listing = AppCatalogDocument.Parse(name, address, bytes);
        foreach (string warning in listing.Warnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        lock (_catalogGate)
        {
            _catalogs[address] = (now, listing);
        }

        return listing;
    }

    /// <summary>
    /// Gets the catalogs in the order an app ID is looked up (ADR 0212 §4): the official catalog
    /// first, then the others by name.
    /// </summary>
    /// <param name="sources">The sources.</param>
    public static IReadOnlyList<string> GetCatalogSearchOrder(AppSourcesConfig sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return [.. sources.Catalogs.Keys
            .OrderBy(static name => string.Equals(name, AppSourcesConfig.OfficialCatalogName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Finds the catalog that publishes <paramref name="appId"/> (<c>hackpkg install &lt;appId&gt;</c>,
    /// ADR 0188 §8): the first one of <see cref="GetCatalogSearchOrder"/> that lists it. A catalog
    /// that cannot be downloaded is skipped.
    /// </summary>
    /// <param name="appId">The app.</param>
    /// <param name="version">A version, or <see langword="null"/> for the newest stable one.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <exception cref="AppPackageException"><c>apps.package.not-in-catalog</c>, with the catalogs that failed.</exception>
    public async ValueTask<AppPackageSource> FindInCatalogsAsync(
        string appId, SemanticVersion? version, AppSourcesConfig sources, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(sources);
        List<string> failures = [];
        foreach (string name in GetCatalogSearchOrder(sources))
        {
            try
            {
                AppCatalogListing listing = await GetCatalogAsync(name, sources, cancellationToken).ConfigureAwait(false);
                if (listing.Find(appId) is not null)
                {
                    return AppPackageSource.ForCatalog(name, appId, version);
                }
            }
            catch (AppPackageException exception)
            {
                failures.Add($"{name}: {exception.Message}");
            }
        }

        throw new AppPackageException(
            "apps.package.not-in-catalog",
            sources.Catalogs.Count == 0
                ? $"{appId} is in no catalog: {AppSourcesConfigDocument.MachinePath.Value} lists none."
                : $"{appId} is in none of the catalogs ({string.Join(", ", GetCatalogSearchOrder(sources))})."
                    + (failures.Count == 0 ? string.Empty : $" Could not read: {string.Join("; ", failures)}"));
    }

    /// <summary>Downloads a text file of a catalog, such as an app's <c>description.fr.md</c>.</summary>
    /// <param name="name">Catalog name.</param>
    /// <param name="relativePath">Path from <c>catalog.json</c>.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The UTF-8 text, or <see langword="null"/> when the file does not exist.</returns>
    public async ValueTask<string?> DownloadCatalogTextAsync(
        string name, string relativePath, AppSourcesConfig sources, CancellationToken cancellationToken = default)
    {
        AppCatalogListing listing = await GetCatalogAsync(name, sources, cancellationToken).ConfigureAwait(false);
        byte[]? bytes = await _downloader
            .DownloadAsync(listing.Resolve(relativePath), MaximumCatalogTextBytes, cancellationToken)
            .ConfigureAwait(false);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Largest catalog text file downloaded: the catalog accepts 20 KB descriptions.</summary>
    public const int MaximumCatalogTextBytes = 64 * 1024;

    /// <summary>
    /// Finds the installed packages whose source publishes a newer version (ADR 0188 §3): for each
    /// app, its newest stable version (or the newest one when the installed one is a prerelease) is
    /// compared with the installed one. A source that cannot be read is skipped with a warning.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <param name="systemInstalled">The <c>[installed]</c> section of the system.</param>
    /// <param name="personalInstalled">The session user's <c>[installed]</c> section.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    public async ValueTask<AppPackageUpdateReport> FindUpdatesAsync(
        AppSourcesConfig sources,
        InstalledAppsReadResult systemInstalled,
        InstalledAppsReadResult personalInstalled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(systemInstalled);
        ArgumentNullException.ThrowIfNull(personalInstalled);
        IEnumerable<(string AppId, AppPackageSource Source, bool SystemWide)> installed = systemInstalled.Sources
            .Select(static pair => (pair.Key, pair.Value, true))
            .Concat(personalInstalled.Sources
                .Where(pair => !systemInstalled.Sources.ContainsKey(pair.Key))
                .Select(static pair => (pair.Key, pair.Value, false)));
        List<AppPackageUpdate> updates = [];
        List<string> failures = [];
        foreach ((string appId, AppPackageSource source, bool systemWide) in installed.OrderBy(static item => item.AppId, StringComparer.Ordinal))
        {
            try
            {
                AppPackageIndex index = await GetIndexAsync(source, sources, cancellationToken).ConfigureAwait(false);
                AppPackageIndexEntry? newest = source.Version?.PreRelease is null ? index.Latest : index.Versions.FirstOrDefault();
                if (newest is not null && source.Version is { } current && newest.Version.CompareTo(current) > 0)
                {
                    updates.Add(new AppPackageUpdate(appId, source, newest.Version, systemWide));
                }
            }
            catch (AppPackageException exception)
            {
                failures.Add($"{source.Name}: {exception.Message}");
                _logger.LogWarning("apps: update check of {AppId} from {Source} failed: {Reason}", appId, source.Name, exception.Message);
            }
        }

        return new AppPackageUpdateReport(updates, failures);
    }

    private async ValueTask<(AppCatalogListing Listing, AppCatalogEntry Entry)> GetCatalogEntryAsync(
        AppPackageSource source, AppSourcesConfig sources, CancellationToken cancellationToken)
    {
        AppCatalogListing listing = await GetCatalogAsync(source.Catalog!, sources, cancellationToken).ConfigureAwait(false);
        AppCatalogEntry entry = listing.Find(source.CatalogAppId!)
            ?? throw new AppPackageException(
                "apps.package.not-in-catalog", $"The catalog '{source.Catalog}' does not list {source.CatalogAppId}.");
        return (listing, entry);
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
