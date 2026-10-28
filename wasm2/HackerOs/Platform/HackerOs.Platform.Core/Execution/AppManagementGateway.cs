using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Time;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// Trusted <see cref="IAppManagementGateway"/> (ADR 0056, section 3) over
/// <see cref="IAppInstallationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every call requires <c>apps.manage</c>. A <see cref="HackInstallScope.Machine"/> change then asks
/// the OS authentication agent (ADR 0055, section 5); once an administrator authenticated, the OS
/// writes <c>/etc/hackeros/apps.config</c> itself in an audited system context, as
/// <c>pkexec</c> runs the helper as root. The app's own filesystem rights are never widened.
/// </para>
/// <para>
/// A <see cref="HackInstallScope.User"/> change is written as the session user, in their own
/// <c>~/.config/hackeros/apps.config</c>.
/// </para>
/// </remarks>
internal sealed partial class AppManagementGateway : IAppManagementGateway
{
    private readonly AppCatalog _catalog;
    private readonly Func<IAppInstallationService> _installation;
    private readonly Func<IAdministratorAuthenticationAgent>? _agent;
    private readonly ICapabilityChecker _capabilities;
    private readonly AppManifest _caller;
    private readonly AuthenticatedPrincipal _principal;
    private readonly bool _isRootElevated;
    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly Func<System.Globalization.CultureInfo> _language;
    private readonly Func<AppAssetStore>? _assets;
    private readonly Func<Apps.Packages.AppPackageInstaller?>? _packages;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="catalog">The app catalog.</param>
    /// <param name="installation">Lazily resolved installation service.</param>
    /// <param name="agent">Lazily resolved authentication agent; <see langword="null"/> refuses machine changes.</param>
    /// <param name="capabilities">The app's capability checker.</param>
    /// <param name="caller">Manifest of the app asking.</param>
    /// <param name="principal">Session user running the app.</param>
    /// <param name="isRootElevated">Whether the app runs as root through <c>sudo</c>.</param>
    /// <param name="fileSystem">Filesystem engine the OS writes <c>/etc/hackeros/apps.config</c> with.</param>
    /// <param name="clock">Simulation clock.</param>
    /// <param name="language">
    /// Language of the calling process (ADR 0104) for the operation shown in the administrator
    /// prompt; English when <see langword="null"/>.
    /// </param>
    /// <param name="assets">Integrity of the installed apps' assets (ADR 0098 §5), or <see langword="null"/>.</param>
    /// <param name="packages">Installs apps from their repository (ADR 0182), or <see langword="null"/> to refuse it.</param>
    public AppManagementGateway(
        AppCatalog catalog,
        Func<IAppInstallationService> installation,
        Func<IAdministratorAuthenticationAgent>? agent,
        ICapabilityChecker capabilities,
        AppManifest caller,
        AuthenticatedPrincipal principal,
        bool isRootElevated,
        IFileSystemService fileSystem,
        ISimulationClock clock,
        Func<System.Globalization.CultureInfo>? language = null,
        Func<AppAssetStore>? assets = null,
        Func<Apps.Packages.AppPackageInstaller?>? packages = null)
    {
        _assets = assets;
        _packages = packages;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _installation = installation ?? throw new ArgumentNullException(nameof(installation));
        _agent = agent;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _isRootElevated = isRootElevated;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _language = language ?? (static () => HackerOs.AppSdk.Globalization.HackLocale.DefaultLanguage);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HackAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.AppsManage);
        cancellationToken.ThrowIfCancellationRequested();
        IAppInstallationService installation = _installation();
        AppAssetStore? assets = _assets?.Invoke();
        IEnumerable<HackAppInstallInfo> loaded = _catalog.Manifests.Values
            .Select(manifest => new HackAppInstallInfo(
                HackAppInfo.FromManifest(manifest, _catalog.IsSystemApp(manifest.Id)),
                assets is not null && assets.IsDamaged(manifest.Id)
                    ? HackAppInstallState.Damaged
                    : ToPublic(installation.GetState(manifest.Id)),
                installation.GetUninstallClosure(manifest.Id))
            {
                Problem = assets is not null && assets.Damaged.TryGetValue(manifest.Id, out AppIntegrityProblem? problem)
                    ? $"Asset '{problem.AssetPath}' does not match its manifest: {problem.Reason}."
                    : null
            });
        // ADR 0098 §3: apps the OS refused to load are listed too, as incompatible, with the reason.
        IEnumerable<HackAppInstallInfo> refused = _catalog.Rejected.Select(rejection => new HackAppInstallInfo(
            HackAppInfo.FromManifest(rejection.Manifest, _catalog.IsSystemApp(rejection.AppId)),
            HackAppInstallState.Incompatible,
            [])
        {
            Problem = rejection.Message
        });
        IReadOnlyList<HackAppInstallInfo> all = [.. loaded.Concat(refused)
            .OrderBy(entry => entry.App.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.App.Id, StringComparer.Ordinal)];
        return ValueTask.FromResult(all);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> UninstallAsync(
        string appId, HackInstallScope scope, CancellationToken cancellationToken = default)
    {
        AppManifest target = RequireApp(appId);
        IAppFileSystemGateway? writer = await AuthorizeAsync(target, scope, uninstall: true, cancellationToken).ConfigureAwait(false);
        AppInstallationResult result = await _installation()
            .UninstallAsync(appId, ToInternal(scope), writer, cancellationToken)
            .ConfigureAwait(false);
        return Unwrap(result);
    }

    /// <inheritdoc />
    public async ValueTask<HackUninstallPlan> PlanUninstallAsync(string appId, CancellationToken cancellationToken = default)
    {
        RequireApp(appId);
        AppUninstallPlan plan = await _installation().PlanUninstallAsync(appId, cancellationToken).ConfigureAwait(false);
        return new HackUninstallPlan(plan.AppIds, [.. plan.RunningProcesses.Select(ToPublic)]);
    }

    /// <inheritdoc />
    public async ValueTask<HackUninstallReport> UninstallWithReportAsync(
        string appId, HackInstallScope scope, CancellationToken cancellationToken = default)
    {
        AppManifest target = RequireApp(appId);
        IAppFileSystemGateway? writer = await AuthorizeAsync(target, scope, uninstall: true, cancellationToken).ConfigureAwait(false);
        AppInstallationResult result = await _installation()
            .UninstallAsync(appId, ToInternal(scope), writer, cancellationToken)
            .ConfigureAwait(false);
        return new HackUninstallReport(Unwrap(result), [.. (result.StoppedProcesses ?? []).Select(ToPublic)]);
    }

    private static HackRunningProcessInfo ToPublic(Lifecycle.AppProcessInfo process) =>
        new(process.Pid, process.AppId, process.Name, process.UserName, process.Forced);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ReinstallAsync(
        string appId, HackInstallScope scope, CancellationToken cancellationToken = default)
    {
        AppManifest target = RequireApp(appId);
        IAppFileSystemGateway? writer = await AuthorizeAsync(target, scope, uninstall: false, cancellationToken).ConfigureAwait(false);
        AppInstallationResult result = await _installation()
            .ReinstallAsync(appId, ToInternal(scope), writer, cancellationToken)
            .ConfigureAwait(false);
        return Unwrap(result);
    }

    private AppManifest RequireApp(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _capabilities.Require(AppCapabilities.AppsManage);
        return _catalog.Manifests.TryGetValue(appId, out AppManifest? manifest)
            ? manifest
            : throw new ArgumentException($"App '{appId}' is not in the catalog.", nameof(appId));
    }

    /// <summary>
    /// For the machine scope, asks the authentication agent and returns the system writer the OS
    /// then uses; for the user scope, returns <see langword="null"/> (the session user writes).
    /// </summary>
    private async ValueTask<IAppFileSystemGateway?> AuthorizeAsync(
        AppManifest target, HackInstallScope scope, bool uninstall, CancellationToken cancellationToken)
    {
        if (scope != HackInstallScope.Machine)
        {
            return null;
        }

        // A system app is refused before any prompt: no password is asked for an impossible change.
        if (uninstall && _catalog.IsSystemApp(target.Id))
        {
            throw new InvalidOperationException($"{target.Name} is a system app and cannot be uninstalled.");
        }

        IAdministratorAuthenticationAgent agent = _agent?.Invoke()
            ?? throw new HackUnauthorizedAccessException(
                "No administrator authentication agent is available on this host.", path: AppsConfigDocument.MachinePath.Value);
        System.Globalization.CultureInfo language = _language();
        AdministratorAuthenticationResult authentication = await agent.AuthenticateAsync(
            new AdministratorAuthenticationRequest(
                _caller,
                Strings.For(language)[uninstall ? "Admin.App.Uninstall" : "Admin.App.Reinstall", _catalog.Localize(target, language).Name],
                uninstall ? "apps.uninstall.machine" : "apps.reinstall.machine",
                _principal,
                _isRootElevated),
            cancellationToken).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            throw new HackUnauthorizedAccessException(
                $"Administrator authentication failed ({authentication.FailureReason}).",
                path: AppsConfigDocument.MachinePath.Value);
        }

        return new AppFileSystemGateway(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"]);
    }

    private IReadOnlyList<string> Unwrap(AppInstallationResult result)
    {
        if (result.Success)
        {
            return result.AppIds;
        }

        string error = result.Errors.Count > 0 ? result.Errors[0] : "apps.unknown";
        int separator = error.IndexOf(':');
        string code = separator < 0 ? error : error[..separator];
        string appId = separator < 0 ? string.Empty : error[(separator + 1)..];
        string name = _catalog.Manifests.TryGetValue(appId, out AppManifest? manifest) ? manifest.Name : appId;
        throw code switch
        {
            "apps.app.not-found" => new ArgumentException($"App '{appId}' is not in the catalog."),
            "apps.app.system" => new InvalidOperationException($"{name} is a system app and cannot be uninstalled."),
            "apps.closure.system-app" => new InvalidOperationException($"The system app {name} needs it, so it cannot be uninstalled."),
            "apps.uninstalled-by-administrator" => new InvalidOperationException($"{name} was uninstalled by the administrator for every user."),
            "apps.session.required" => new InvalidOperationException("No user session is active."),
            _ => new InvalidOperationException($"The installation change failed ({error}).")
        };
    }

    private static AppInstallationScope ToInternal(HackInstallScope scope) =>
        scope == HackInstallScope.Machine ? AppInstallationScope.Machine : AppInstallationScope.User;

    private static HackAppInstallState ToPublic(AppInstallationState state) => state switch
    {
        AppInstallationState.UninstalledForUser => HackAppInstallState.UninstalledForUser,
        AppInstallationState.UninstalledForMachine => HackAppInstallState.UninstalledForMachine,
        _ => HackAppInstallState.Installed
    };
}
