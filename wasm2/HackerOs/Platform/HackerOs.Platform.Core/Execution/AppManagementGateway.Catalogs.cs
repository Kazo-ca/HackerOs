using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// The catalogs of the Hack Store behind <see cref="HackAppPackages"/> (ADR 0188, ADR 0212): the
/// OS downloads each <c>catalog.json</c> itself and hands the app its apps, with the catalog's trust
/// level; the update check, and the restart that finishes an install.
/// </summary>
internal sealed partial class AppManagementGateway
{
    /// <summary>Gets the update check that receives what the Store finds (ADR 0212 §5).</summary>
    public Func<AppUpdateMonitor?>? UpdateMonitor { get; init; }

    /// <summary>Gets the restart of « Restart to finish » (ADR 0212 §3).</summary>
    public Func<Task>? RestartForPackages { get; init; }

    /// <summary>Gets what brings installed apps into the running catalog without a restart (ADR 0214 §1).</summary>
    public Func<IInstalledAppsActivator?>? InstalledAppsActivator { get; init; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackAppCatalog>> GetPackageCatalogsAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        List<HackAppCatalog> catalogs = [];
        foreach (string name in AppPackageInstaller.GetCatalogSearchOrder(sources))
        {
            HackPackageTrust trust = ToPublic(sources.GetCatalogTrust(name));
            try
            {
                AppCatalogListing listing = await packages.GetCatalogAsync(name, sources, cancellationToken, refresh).ConfigureAwait(false);
                catalogs.Add(new HackAppCatalog(
                    name,
                    trust,
                    [.. listing.Categories.Select(static category => new HackCatalogCategory(category.Id, category.Labels))],
                    [.. listing.Apps.Select(app => ToPublic(listing, app, trust))]));
            }
            catch (AppPackageException exception)
            {
                catalogs.Add(new HackAppCatalog(name, trust, [], []) { Error = exception.Message });
            }
        }

        return catalogs;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetCatalogDescriptionAsync(
        string catalog, string appId, string version, string language, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        AppPackageInstaller packages = RequirePackages();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        return await Translate(async () =>
        {
            AppCatalogListing listing = await packages.GetCatalogAsync(catalog, sources, cancellationToken).ConfigureAwait(false);
            AppCatalogVersion? entry = listing.Apps.FirstOrDefault(app => string.Equals(app.AppId, appId, StringComparison.Ordinal))?.Versions
                .FirstOrDefault(candidate => string.Equals(candidate.Version.ToString(), version, StringComparison.Ordinal));
            if (entry is null)
            {
                return null;
            }

            string? path = entry.Descriptions.TryGetValue(language, out string? exact) ? exact
                : language.Length > 2 && entry.Descriptions.TryGetValue(language[..2], out string? neutral) ? neutral
                : entry.Descriptions.TryGetValue("en", out string? english) ? english
                : entry.Descriptions.Values.FirstOrDefault();
            return path is null ? null : await packages.DownloadCatalogTextAsync(catalog, path, sources, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackPackageUpdate>> GetPackageUpdatesAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway system = CreateSystemWriter();
        AppPackageUpdateReport report = await packages.FindUpdatesAsync(
                await packages.ReadSourcesAsync(system, cancellationToken).ConfigureAwait(false),
                await packages.ReadInstalledAsync(system, cancellationToken).ConfigureAwait(false),
                await packages.ReadPersonalInstalledAsync(CreateUserWriter(), _principal.HomePath, cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        // What the Store shows is not notified again (ADR 0215 §1).
        UpdateMonitor?.Invoke()?.Publish(report.Updates);
        return [.. report.Updates.Select(static update => new HackPackageUpdate(
            update.AppId,
            update.Installed.Name,
            update.Installed.Version?.ToString() ?? string.Empty,
            update.Available.ToString(),
            update.SystemWide))];
    }

    /// <inheritdoc />
    public async ValueTask RestartForPackagesAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        Func<Task> restart = RestartForPackages ?? throw new NotSupportedException("This host cannot restart the OS.");
        if (!await HasPendingPackageChangesAsync(packages, cancellationToken).ConfigureAwait(false))
        {
            throw new HackPackageException("apps.package.nothing-pending", "No installed, updated or removed app waits for a restart.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await restart().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies what was just written to the running catalog (ADR 0214 §1). A host without hot add,
    /// or a failed read, leaves the change for the next start of the OS: the files are written
    /// already, so the install itself succeeded.
    /// </summary>
    private async ValueTask ActivateInstalledAppsAsync(CancellationToken cancellationToken)
    {
        if (InstalledAppsActivator?.Invoke() is not { } activator)
        {
            return;
        }

        try
        {
            await activator.ActivateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            // « Restart to finish » stays offered: the catalog did not change.
        }
    }

    /// <summary>Gets whether the running catalog has <paramref name="appId"/> at <paramref name="version"/>.</summary>
    private bool RunsVersion(string appId, SemanticVersion version) =>
        _catalog.Manifests.TryGetValue(appId, out AppManifest? manifest)
        && SemanticVersion.TryParse(manifest.Version, out SemanticVersion running)
        && running == version;

    /// <summary>
    /// Whether the running catalog differs from what is installed for the system or for the
    /// session user: an app installed or updated since the start, or removed but still loaded.
    /// </summary>
    private async ValueTask<bool> HasPendingPackageChangesAsync(AppPackageInstaller packages, CancellationToken cancellationToken)
    {
        IReadOnlyList<HackInstalledPackage> installed = await GetInstalledPackagesAsync(cancellationToken).ConfigureAwait(false);
        if (installed.Any(static package => !package.Loaded))
        {
            return true;
        }

        InstalledPackageCatalog? running = _installedPackages?.Invoke();
        HashSet<string> installedIds = [.. installed.Select(static package => package.AppId)];
        return running is not null
            && running.Apps.Keys.Any(appId => running.IsVisibleTo(appId, _principal.HomePath) && !installedIds.Contains(appId));
    }

    /// <summary>
    /// Reads what the user typed: a repository or catalog source (<see cref="AppPackageSource.TryParse"/>),
    /// or an app ID alone, looked up in the catalogs (ADR 0188 §8, ADR 0215 §2).
    /// </summary>
    private async ValueTask<AppPackageSource> ResolveSourceAsync(
        AppPackageInstaller packages, string source, AppSourcesConfig sources, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (AppPackageSource.TryParse(source, out AppPackageSource? parsed))
        {
            return parsed;
        }

        string text = source.Trim();
        SemanticVersion? version = null;
        int at = text.LastIndexOf('@');
        if (at > 0 && SemanticVersion.TryParse(text[(at + 1)..], out SemanticVersion requested))
        {
            version = requested;
            text = text[..at];
        }

        if (!AppPackageSource.IsValidAppId(text))
        {
            throw new HackPackageException(
                "apps.package.bad-source",
                $"'{source}' is not a source: use an app ID, catalog:name/app.id, github:owner/repo, owner/repo or https://github.com/owner/repo, optionally followed by @version.");
        }

        return await Translate(() => packages.FindInCatalogsAsync(text, version, sources, cancellationToken)).ConfigureAwait(false);
    }

    private static HackCatalogApp ToPublic(AppCatalogListing listing, AppCatalogEntry app, HackPackageTrust trust) => new(
        listing.Name,
        app.AppId,
        app.Name,
        app.Category,
        app.Tags,
        app.ShortDescriptions,
        app.License,
        app.Repository,
        app.LatestVersion.ToString(),
        [.. app.Versions.Select(version => new HackCatalogAppVersion(version.Version.ToString(), version.Size, version.Capabilities)
        {
            PublishedAt = version.PublishedAt,
            MinimumSdkVersion = version.MinimumSdkVersion,
            IconUri = version.Icon is { } icon ? listing.Resolve(icon).AbsoluteUri : null,
            ScreenshotUris = [.. version.Screenshots.Select(path => listing.Resolve(path).AbsoluteUri)],
            DescriptionLanguages = [.. version.Descriptions.Keys.Order(StringComparer.OrdinalIgnoreCase)],
        })],
        trust)
    {
        Kind = app.Kind == AppCatalogEntryKind.ThemePack ? HackCatalogItemKind.ThemePack : HackCatalogItemKind.App,
    };
}
