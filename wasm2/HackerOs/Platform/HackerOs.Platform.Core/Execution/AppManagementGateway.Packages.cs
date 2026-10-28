using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// Apps installed from their GitHub repository (ADR 0182) behind <see cref="HackAppPackages"/>.
/// The OS reads its own files and downloads by itself; an install or a removal writes as the
/// system once an administrator authenticated, as for a machine uninstall.
/// </summary>
internal sealed partial class AppManagementGateway
{
    /// <inheritdoc />
    public async ValueTask<HackPackageSources> GetPackageSourcesAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        return new HackPackageSources(
            sources.GitHubHost,
            [.. sources.TrustedKeys.Order(StringComparer.OrdinalIgnoreCase)],
            sources.Warnings);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackInstalledPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        InstalledAppsReadResult installed = await packages.ReadInstalledAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        return [.. installed.Sources
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => ToPublic(pair.Key, pair.Value))];
    }

    /// <inheritdoc />
    public async ValueTask<HackPackageInfo> GetPackageInfoAsync(string source, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        AppPackageSource parsed = ParseSource(source);
        IAppFileSystemGateway reader = CreateSystemWriter();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(reader, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult installed = await packages.ReadInstalledAsync(reader, cancellationToken).ConfigureAwait(false);
        AppPackageIndex index = await Translate(() => packages.GetIndexAsync(parsed, sources, cancellationToken)).ConfigureAwait(false);
        string? installedVersion = installed.Sources.TryGetValue(index.AppId, out AppPackageSource? current) && current.IsSameRepository(parsed)
            ? current.Version?.ToString()
            : null;
        return new HackPackageInfo(
            parsed.Name,
            index.AppId,
            [.. index.Versions.Select(static entry => entry.Version.ToString())],
            index.Latest?.Version.ToString(),
            sources.IsTrusted(parsed),
            installedVersion);
    }

    /// <inheritdoc />
    public async ValueTask<HackPackageInstallReport> InstallPackageAsync(
        string source, bool allowUntrusted = false, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        AppPackageSource parsed = ParseSource(source);
        IAppFileSystemGateway reader = CreateSystemWriter();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(reader, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult installed = await packages.ReadInstalledAsync(reader, cancellationToken).ConfigureAwait(false);
        // Download and every check first: no password is asked for a package that would be refused.
        AppPackagePlan plan = await Translate(
                () => packages.PrepareAsync(parsed, sources, allowUntrusted, _catalog, installed, cancellationToken))
            .ConfigureAwait(false);
        AppManifest manifest = plan.Package.Manifest;
        string operation = Strings.For(_language())[
            plan.Trusted ? "Admin.App.InstallPackage" : "Admin.App.InstallUntrustedPackage",
            manifest.Name,
            plan.Package.Version.ToString(),
            plan.Source.Name];
        IAppFileSystemGateway system = await AuthorizePackageAsync(operation, "apps.package.install", cancellationToken).ConfigureAwait(false);
        await packages.CommitAsync(system, plan, _principal.LoginName.Value, cancellationToken).ConfigureAwait(false);
        return new HackPackageInstallReport(
            manifest.Id,
            manifest.Name,
            plan.Package.Version.ToString(),
            plan.Source.ToString(),
            plan.PreviousVersion?.ToString(),
            manifest.Capabilities,
            plan.Trusted);
    }

    /// <inheritdoc />
    public async ValueTask<HackInstalledPackage> RemovePackageAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        AppPackageInstaller packages = RequirePackages();
        InstalledAppsReadResult installed = await packages.ReadInstalledAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        if (!installed.Sources.TryGetValue(appId, out AppPackageSource? source))
        {
            throw new HackPackageException("apps.package.not-installed", $"{appId} was not installed from a package.");
        }

        string name = _catalog.Manifests.TryGetValue(appId, out AppManifest? manifest) ? manifest.Name : appId;
        string operation = Strings.For(_language())["Admin.App.RemovePackage", name, source.Name];
        IAppFileSystemGateway system = await AuthorizePackageAsync(operation, "apps.package.remove", cancellationToken).ConfigureAwait(false);
        AppPackageSource removed = await Translate(
                () => packages.RemoveAsync(system, appId, _principal.LoginName.Value, cancellationToken))
            .ConfigureAwait(false);
        return ToPublic(appId, removed);
    }

    private AppPackageInstaller RequirePackages()
    {
        _capabilities.Require(AppCapabilities.AppsManage);
        return _packages?.Invoke() ?? throw new NotSupportedException("This host cannot install apps from a repository.");
    }

    private HackInstalledPackage ToPublic(string appId, AppPackageSource source) => new(
        appId,
        source.Name,
        source.Version?.ToString() ?? string.Empty,
        _catalog.Manifests.TryGetValue(appId, out AppManifest? loaded)
            && SemanticVersion.TryParse(loaded.Version, out SemanticVersion version)
            && version == source.Version);

    private static AppPackageSource ParseSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return AppPackageSource.TryParse(source, out AppPackageSource? parsed)
            ? parsed
            : throw new HackPackageException(
                "apps.package.bad-source",
                $"'{source}' is not a repository: use github:owner/repo, owner/repo or https://github.com/owner/repo, optionally followed by @version.");
    }

    private static async ValueTask<T> Translate<T>(Func<ValueTask<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (AppPackageException exception)
        {
            throw new HackPackageException(exception.Code, exception.Message);
        }
    }

    /// <summary>The system writer the OS acts with: reads of its own files, and writes once authorized.</summary>
    private AppFileSystemGateway CreateSystemWriter() =>
        new(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"]);

    /// <summary>Asks the authentication agent, like a machine uninstall, then returns the system writer.</summary>
    private async ValueTask<IAppFileSystemGateway> AuthorizePackageAsync(string operation, string action, CancellationToken cancellationToken)
    {
        IAdministratorAuthenticationAgent agent = _agent?.Invoke()
            ?? throw new HackUnauthorizedAccessException(
                "No administrator authentication agent is available on this host.", path: InstalledAppsLayout.Root);
        AdministratorAuthenticationResult authentication = await agent.AuthenticateAsync(
            new AdministratorAuthenticationRequest(_caller, operation, action, _principal, _isRootElevated),
            cancellationToken).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            throw new HackUnauthorizedAccessException(
                $"Administrator authentication failed ({authentication.FailureReason}).", path: InstalledAppsLayout.Root);
        }

        return CreateSystemWriter();
    }
}
