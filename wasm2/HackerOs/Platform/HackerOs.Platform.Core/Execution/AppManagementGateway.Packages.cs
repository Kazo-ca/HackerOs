using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// Apps installed from their GitHub repository (ADR 0182, ADR 0189) behind <see cref="HackAppPackages"/>.
/// The OS reads its own files and downloads by itself. A personal install from a trusted source is
/// written as the session user; a system install, an untrusted source or a blocked app needs the
/// two-step approval of ADR 0189 §4 (a fresh administrator sign-in, then the OS's approval window),
/// after which the OS writes as the system.
/// </summary>
internal sealed partial class AppManagementGateway
{
    /// <inheritdoc />
    public async ValueTask<HackPackageSources> GetPackageSourcesAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        return ToPublic(await packages.ReadSourcesAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackInstalledPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        InstalledAppsReadResult system = await packages.ReadInstalledAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult personal = await packages
            .ReadPersonalInstalledAsync(CreateUserWriter(), _principal.HomePath, cancellationToken)
            .ConfigureAwait(false);
        return [.. system.Sources.Select(pair => ToPublic(pair.Key, pair.Value, systemWide: true))
            .Concat(personal.Sources.Where(pair => !system.Sources.ContainsKey(pair.Key)).Select(pair => ToPublic(pair.Key, pair.Value, systemWide: false)))
            .OrderBy(static package => package.AppId, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async ValueTask<HackPackageInfo> GetPackageInfoAsync(string source, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway reader = CreateSystemWriter();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(reader, cancellationToken).ConfigureAwait(false);
        AppPackageSource parsed = await ResolveSourceAsync(packages, source, sources, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult system = await packages.ReadInstalledAsync(reader, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult personal = await packages
            .ReadPersonalInstalledAsync(CreateUserWriter(), _principal.HomePath, cancellationToken)
            .ConfigureAwait(false);
        AppPackageIndex index = await Translate(() => packages.GetIndexAsync(parsed, sources, cancellationToken)).ConfigureAwait(false);
        AppPackageSource? current = null;
        bool? systemWide = null;
        if (system.Sources.TryGetValue(index.AppId, out AppPackageSource? forSystem) && forSystem.IsSameRepository(parsed))
        {
            (current, systemWide) = (forSystem, true);
        }
        else if (personal.Sources.TryGetValue(index.AppId, out AppPackageSource? forUser) && forUser.IsSameRepository(parsed))
        {
            (current, systemWide) = (forUser, false);
        }

        AppSourceTrust trust = sources.GetTrust(parsed);
        return new HackPackageInfo(
            parsed.Name,
            index.AppId,
            [.. index.Versions.Select(static entry => entry.Version.ToString())],
            index.Latest?.Version.ToString(),
            trust != AppSourceTrust.Untrusted,
            current?.Version?.ToString())
        {
            Trust = ToPublic(trust),
            InstalledSystemWide = systemWide,
        };
    }

    /// <inheritdoc />
    public async ValueTask<HackPackageInstallReport> InstallPackageAsync(
        string source, HackPackageScope scope = HackPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway system = CreateSystemWriter();
        IAppFileSystemGateway user = CreateUserWriter();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(system, cancellationToken).ConfigureAwait(false);
        AppPackageSource parsed = await ResolveSourceAsync(packages, source, sources, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult systemInstalled = await packages.ReadInstalledAsync(system, cancellationToken).ConfigureAwait(false);
        AppPackageInstallContext context = new(
            sources,
            await packages.ReadPolicyAsync(system, cancellationToken).ConfigureAwait(false),
            (AppPackageScope)scope,
            _principal.HomePath,
            _catalog,
            systemInstalled,
            await packages.ReadPersonalInstalledAsync(user, _principal.HomePath, cancellationToken).ConfigureAwait(false))
        {
            PackagedAppIds = (_installedPackages?.Invoke()?.Apps.Keys ?? []).Concat(systemInstalled.Sources.Keys).ToHashSet(StringComparer.Ordinal),
        };
        // Download and every check first: no administrator is asked for a package that would be refused.
        AppPackagePlan plan = await Translate(() => packages.PrepareAsync(parsed, context, cancellationToken)).ConfigureAwait(false);
        AppManifest manifest = plan.Package.Manifest;
        string by = _principal.LoginName.Value;
        if (plan.RequiresAdministrator)
        {
            string operation = Strings.For(_language())[
                plan.SystemWide
                    ? plan.Trust == AppSourceTrust.Untrusted ? "Admin.App.InstallUntrustedPackage" : "Admin.App.InstallPackage"
                    : "Admin.App.InstallUntrustedPackageForUser",
                manifest.Name,
                plan.Package.Version.ToString(),
                plan.Source.Name,
                _principal.LoginName.Value];
            by = await ApproveTwoStepAsync(
                operation,
                "apps.package.install",
                new AppInstallApprovalRequest(
                    manifest.Id,
                    manifest.Name,
                    plan.Package.Version.ToString(),
                    plan.PreviousVersion?.ToString(),
                    plan.SizeBytes,
                    plan.Source.ToString(),
                    plan.Trust,
                    manifest.Capabilities,
                    plan.PreviousVersion is null ? AppInstallApprovalOperation.Install : AppInstallApprovalOperation.Update,
                    plan.SystemWide,
                    string.Empty,
                    _principal,
                    _caller)
                {
                    BlockReason = plan.BlockReason,
                },
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> personalCopies = plan.SystemWide
            ? await packages.FindPersonalCopiesAsync(system, manifest.Id, cancellationToken).ConfigureAwait(false)
            : [];
        await packages
            .CommitAsync(plan.SystemWide ? system : user, plan, by, cancellationToken, personalCopies)
            .ConfigureAwait(false);
        return new HackPackageInstallReport(
            manifest.Id,
            manifest.Name,
            plan.Package.Version.ToString(),
            plan.Source.ToString(),
            plan.PreviousVersion?.ToString(),
            manifest.Capabilities,
            plan.Trust != AppSourceTrust.Untrusted)
        {
            Trust = ToPublic(plan.Trust),
            SystemWide = plan.SystemWide,
            BlockedBy = plan.BlockReason?.ToString(),
            ReplacedPersonalCopies = personalCopies.Count,
        };
    }

    /// <inheritdoc />
    public async ValueTask<HackInstalledPackage> RemovePackageAsync(
        string appId, HackPackageScope scope = HackPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway user = CreateUserWriter();
        InstalledAppsReadResult personal = await packages.ReadPersonalInstalledAsync(user, _principal.HomePath, cancellationToken).ConfigureAwait(false);
        bool forUser = scope == HackPackageScope.User || (scope == HackPackageScope.Auto && personal.Sources.ContainsKey(appId));
        if (forUser)
        {
            AppPackageSource removedForUser = await Translate(
                    () => packages.RemoveAsync(user, appId, _principal.LoginName.Value, cancellationToken, _principal.HomePath))
                .ConfigureAwait(false);
            return ToPublic(appId, removedForUser, systemWide: false);
        }

        IAppFileSystemGateway system = CreateSystemWriter();
        InstalledAppsReadResult installed = await packages.ReadInstalledAsync(system, cancellationToken).ConfigureAwait(false);
        if (!installed.Sources.TryGetValue(appId, out AppPackageSource? source))
        {
            throw new HackPackageException("apps.package.not-installed", $"{appId} was not installed from a package.");
        }

        AppSourcesConfig sources = await packages.ReadSourcesAsync(system, cancellationToken).ConfigureAwait(false);
        AppManifest? manifest = _catalog.Manifests.TryGetValue(appId, out AppManifest? loaded) ? loaded : null;
        string name = manifest?.Name ?? appId;
        string operation = Strings.For(_language())["Admin.App.RemovePackage", name, source.Name];
        string by = await ApproveTwoStepAsync(
            operation,
            "apps.package.remove",
            new AppInstallApprovalRequest(
                appId,
                name,
                source.Version?.ToString() ?? string.Empty,
                null,
                null,
                source.ToString(),
                sources.GetTrust(source),
                manifest?.Capabilities ?? [],
                AppInstallApprovalOperation.Remove,
                true,
                string.Empty,
                _principal,
                _caller),
            cancellationToken).ConfigureAwait(false);
        AppPackageSource removed = await Translate(() => packages.RemoveAsync(system, appId, by, cancellationToken)).ConfigureAwait(false);
        return ToPublic(appId, removed, systemWide: true);
    }

    /// <inheritdoc />
    public async ValueTask<HackPackageSources> AddPackageSourceAsync(string source, bool trusted, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        AppSourceSpecification parsed = ParseSourceSpecification(source);
        if (parsed.IsCatalog && parsed.CatalogAddress is null)
        {
            throw new HackPackageException("apps.source.invalid", $"A new catalog needs its address: {parsed.Name}=https://…/catalog.json.");
        }

        IAppFileSystemGateway system = await AuthorizePackageAsync(
            Strings.For(_language())["Admin.App.EditAppSources", $"+ {parsed.Name}{(trusted ? " (trusted)" : string.Empty)}"],
            "apps.sources.add",
            cancellationToken).ConfigureAwait(false);
        await EditMachineFileAsync(
            system,
            AppSourcesConfigDocument.MachinePath,
            content => AppSourcesConfigDocument.AddSource(content ?? AppSourcesConfigDocument.BuildDefaultContent(packages.Defaults), parsed, trusted),
            cancellationToken).ConfigureAwait(false);
        return await GetPackageSourcesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<HackPackageSources> RemovePackageSourceAsync(string source, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        AppSourceSpecification parsed = ParseSourceSpecification(source);
        AppSourcesConfig current = await packages.ReadSourcesAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        bool listed = (parsed.IsCatalog && current.Catalogs.ContainsKey(parsed.Name))
            || current.TrustedKeys.Contains(parsed.TrustKey)
            || current.UntrustedKeys.Contains(parsed.TrustKey);
        if (!listed)
        {
            throw new HackPackageException("apps.source.not-found", $"{parsed.Name} is not in {AppSourcesConfigDocument.MachinePath.Value}.");
        }

        IAppFileSystemGateway system = await AuthorizePackageAsync(
            Strings.For(_language())["Admin.App.EditAppSources", $"- {parsed.Name}"],
            "apps.sources.remove",
            cancellationToken).ConfigureAwait(false);
        await EditMachineFileAsync(
            system,
            AppSourcesConfigDocument.MachinePath,
            content => AppSourcesConfigDocument.RemoveSource(content, parsed),
            cancellationToken).ConfigureAwait(false);
        return await GetPackageSourcesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<HackPackagePolicy> GetPackagePolicyAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        return ToPublic(await packages.ReadPolicyAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async ValueTask<HackPackagePolicy> SetPackagePolicyAsync(
        HackPackageBlockKind kind, string value, bool blocked, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        AppPackageInstaller packages = RequirePackages();
        AppBlockKind rule = (AppBlockKind)kind;
        string section = AppPolicyConfigDocument.SectionOf(rule);
        string key = value.Trim();
        if (key.Any(static c => char.IsWhiteSpace(c) || c is '=' or '[' or ']' or '#' or ';'))
        {
            throw new HackPackageException("apps.policy.invalid", $"'{value}' cannot be a key of [{section}].");
        }

        IAppFileSystemGateway system = await AuthorizePackageAsync(
            Strings.For(_language())["Admin.App.EditAppPolicy", $"{(blocked ? '+' : '-')} [{section}] {key}"],
            blocked ? "apps.policy.block" : "apps.policy.unblock",
            cancellationToken).ConfigureAwait(false);
        await EditMachineFileAsync(
            system,
            AppPolicyConfigDocument.MachinePath,
            content => AppPolicyConfigDocument.Set(content ?? AppPolicyConfigDocument.BuildDefaultContent(packages.Defaults), rule, key, blocked),
            cancellationToken).ConfigureAwait(false);
        return await GetPackagePolicyAsync(cancellationToken).ConfigureAwait(false);
    }

    private AppPackageInstaller RequirePackages()
    {
        _capabilities.Require(AppCapabilities.AppsManage);
        return _packages?.Invoke() ?? throw new NotSupportedException("This host cannot install apps from a repository.");
    }

    private HackInstalledPackage ToPublic(string appId, AppPackageSource source, bool systemWide) => new(
        appId,
        source.Name,
        source.Version?.ToString() ?? string.Empty,
        _catalog.Manifests.TryGetValue(appId, out AppManifest? loaded)
            && SemanticVersion.TryParse(loaded.Version, out SemanticVersion version)
            && version == source.Version)
    {
        SystemWide = systemWide,
    };

    private static HackPackageTrust ToPublic(AppSourceTrust trust) => (HackPackageTrust)trust;

    private static HackPackageSources ToPublic(AppSourcesConfig sources) =>
        new(sources.GitHubHost, [.. sources.TrustedKeys.Order(StringComparer.OrdinalIgnoreCase)], sources.Warnings)
        {
            Catalogs = [.. sources.Catalogs
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new HackPackageCatalog(
                    pair.Key,
                    pair.Value.AbsoluteUri,
                    ToPublic(sources.GetCatalogTrust(pair.Key)),
                    sources.BuiltInKeys.Contains(AppSourcesConfigDocument.CatalogTrustPrefix + pair.Key)))],
            UntrustedSources = [.. sources.UntrustedKeys.Order(StringComparer.OrdinalIgnoreCase)],
            BuiltInSources = [.. sources.BuiltInKeys.Order(StringComparer.OrdinalIgnoreCase)],
            UpdateIntervalHours = sources.UpdateIntervalHours,
        };

    private static HackPackagePolicy ToPublic(AppPolicy policy) => new(
        [.. policy.BlockedApps.Order(StringComparer.Ordinal)],
        [.. policy.BlockedCategories.Order(StringComparer.OrdinalIgnoreCase)],
        [.. policy.BlockedCapabilities.Order(StringComparer.Ordinal)],
        policy.Warnings);

    private static AppSourceSpecification ParseSourceSpecification(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return AppSourceSpecification.TryParse(source, out AppSourceSpecification? parsed)
            ? parsed
            : throw new HackPackageException(
                "apps.source.invalid",
                $"'{source}' is not a source: use name=https://…/catalog.json, catalog:name, github:owner or github:owner/repo.");
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

    private static async ValueTask EditMachineFileAsync(
        IAppFileSystemGateway system, VirtualPath path, Func<string?, string> edit, CancellationToken cancellationToken)
    {
        FileSystemError? error = await SettingsTextFile.EditAsync(system, path, edit, createIfMissing: true, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            throw new IOException($"{error.Path?.Value ?? path.Value}: {error.Code}.");
        }
    }

    /// <summary>The system writer the OS acts with: reads of its own files, and writes once authorized.</summary>
    private AppFileSystemGateway CreateSystemWriter() =>
        new(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"]);

    /// <summary>The session user's writer, for their own personal installs (ADR 0189 §3).</summary>
    private AppFileSystemGateway CreateUserWriter() =>
        new(_fileSystem, _clock, OsSettingsService.CreateUserOperation(_principal), [.. _principal.GroupIds.Select(static id => id.ToString())]);

    /// <summary>
    /// One administrator authentication, as for a machine uninstall (ADR 0055 §5), then the system
    /// writer: the edits of <c>app-sources.config</c> and <c>app-policy.config</c>, which an
    /// administrator may also make with <c>sudo nano</c>.
    /// </summary>
    private async ValueTask<IAppFileSystemGateway> AuthorizePackageAsync(string operation, string action, CancellationToken cancellationToken)
    {
        AdministratorAuthenticationResult authentication = await AuthenticateAsync(operation, action, _isRootElevated, cancellationToken).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            throw new HackUnauthorizedAccessException(
                $"Administrator authentication failed ({authentication.FailureReason}).", path: AppSourcesConfigDocument.MachinePath.Value);
        }

        return CreateSystemWriter();
    }

    /// <summary>
    /// The two steps of ADR 0189 §4, every time: a fresh administrator sign-in (no <c>sudo</c>
    /// timestamp, not even a root process: Q28 answer A), then the approval window showing the
    /// app, its source, its trust level and its capabilities.
    /// </summary>
    /// <returns>The login of the administrator who approved, for the log.</returns>
    private async ValueTask<string> ApproveTwoStepAsync(
        string operation, string action, AppInstallApprovalRequest request, CancellationToken cancellationToken)
    {
        AdministratorAuthenticationResult authentication = await AuthenticateAsync(operation, action, isRootElevated: false, cancellationToken).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            throw new HackUnauthorizedAccessException(
                $"Administrator authentication failed ({authentication.FailureReason}).", path: InstalledAppsLayout.Root);
        }

        string administrator = authentication.AdministratorLogin ?? "root";
        IAppInstallApprovalPrompt prompt = _approval?.Invoke()
            ?? throw new HackUnauthorizedAccessException(
                "No approval window is available on this host (ADR 0189 section 4).", path: InstalledAppsLayout.Root);
        AppInstallApprovalOutcome outcome = await prompt
            .RequestAsync(request with { AdministratorLogin = administrator }, cancellationToken)
            .ConfigureAwait(false);
        if (outcome != AppInstallApprovalOutcome.Approved)
        {
            throw new HackUnauthorizedAccessException(
                $"The administrator {administrator} did not approve ({outcome.ToString().ToLowerInvariant()}).", path: InstalledAppsLayout.Root);
        }

        return administrator;
    }

    private async ValueTask<AdministratorAuthenticationResult> AuthenticateAsync(
        string operation, string action, bool isRootElevated, CancellationToken cancellationToken)
    {
        IAdministratorAuthenticationAgent agent = _agent?.Invoke()
            ?? throw new HackUnauthorizedAccessException(
                "No administrator authentication agent is available on this host.", path: InstalledAppsLayout.Root);
        return await agent.AuthenticateAsync(
            new AdministratorAuthenticationRequest(_caller, operation, action, _principal, isRootElevated),
            cancellationToken).ConfigureAwait(false);
    }
}
