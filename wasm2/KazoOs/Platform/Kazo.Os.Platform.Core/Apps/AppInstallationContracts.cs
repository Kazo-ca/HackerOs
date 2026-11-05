using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Apps;

/// <summary>Installation state of one catalog app for the session user (ADR 0048 section 4).</summary>
public enum AppInstallationState
{
    /// <summary>Installed: listed in neither <c>apps.config</c> file, nor depending on an uninstalled app.</summary>
    Installed = 1,

    /// <summary>Uninstalled in the session user's own <c>~/.config/kazoos/apps.config</c> (or by its dependencies there).</summary>
    UninstalledForUser = 2,

    /// <summary>Uninstalled in <c>/etc/kazoos/apps.config</c> (or by its dependencies there): only an administrator reinstalls it.</summary>
    UninstalledForMachine = 3,

    /// <summary>System app declared by the host (ADR 0048 section 1): never uninstallable.</summary>
    System = 4
}

/// <summary>Which <c>apps.config</c> file an uninstall or reinstall writes (ADR 0048 section 3).</summary>
public enum AppInstallationScope
{
    /// <summary><c>~/.config/kazoos/apps.config</c>: the session user only.</summary>
    User = 1,

    /// <summary><c>/etc/kazoos/apps.config</c>: every user of the machine; needs administrator rights on the file.</summary>
    Machine = 2
}

/// <summary>Outcome of <see cref="IAppInstallationService.UninstallAsync"/> or <see cref="IAppInstallationService.ReinstallAsync"/>.</summary>
/// <param name="Success">Whether the file was written (or nothing needed writing).</param>
/// <param name="AppIds">Every app ID whose line the request wrote or removed, in the order applied.</param>
/// <param name="Errors">Stable error codes (<c>apps.*</c>) when <paramref name="Success"/> is <see langword="false"/>.</param>
/// <param name="StoppedProcesses">
/// For an uninstall: the processes it stopped, each marked when it was forced after its grace period
/// (ADR 0097, section 5); <see langword="null"/> is read as none.
/// </param>
public sealed record AppInstallationResult(
    bool Success,
    IReadOnlyList<string> AppIds,
    IReadOnlyList<string> Errors,
    IReadOnlyList<AppProcessInfo>? StoppedProcesses = null)
{
    /// <summary>Builds a failed result with one error code.</summary>
    /// <param name="error">Stable error code.</param>
    public static AppInstallationResult Failed(string error) => new(false, [], [error]);
}

/// <summary>What an uninstall would do (ADR 0097, section 5).</summary>
/// <param name="AppIds">Every app it removes, dependents first, then the app itself.</param>
/// <param name="RunningProcesses">The running processes of those apps.</param>
public sealed record AppUninstallPlan(IReadOnlyList<string> AppIds, IReadOnlyList<AppProcessInfo> RunningProcesses);

/// <summary>
/// Uninstalls and reinstalls catalog apps through the two <c>apps.config</c> files (ADR 0048).
/// The files are the only source of truth: editing them with <c>nano</c> has the same effect as
/// calling this service, and the service feeds <c>AppEnablementRegistry</c>, the single launch
/// checkpoint.
/// </summary>
public interface IAppInstallationService
{
    /// <summary>Raised after the effective state changed (a call here, an external file edit, a login or a logout).</summary>
    event EventHandler? Changed;

    /// <summary>Gets the effective state of one catalog app for the session user.</summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <exception cref="ArgumentException">The app is not in the catalog.</exception>
    AppInstallationState GetState(string appId);

    /// <summary>
    /// Gets every app an uninstall of <paramref name="appId"/> would also uninstall: the apps that
    /// depend on it through non-optional dependencies, dependents first, then <paramref name="appId"/>.
    /// The confirmation dialog lists them.
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    IReadOnlyList<string> GetUninstallClosure(string appId);

    /// <summary>
    /// Tells, without changing anything, what an uninstall of <paramref name="appId"/> would do
    /// (ADR 0097, section 5): its closure (<see cref="GetUninstallClosure"/>) and the running
    /// processes of those apps, which the uninstall would stop.
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The app is not in the catalog.</exception>
    ValueTask<AppUninstallPlan> PlanUninstallAsync(string appId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new AppUninstallPlan(GetUninstallClosure(appId), []));

    /// <summary>
    /// Uninstalls an app and its dependents in one scope, stopping their running instances. No data
    /// is deleted, except the browser database (<c>kazoweb.&lt;id&gt;</c>) of an uninstalled web app
    /// (ADR 0088, section 4). A system app, or a closure containing one, is refused.
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="scope">File to write.</param>
    /// <param name="writer">
    /// Gateway whose rights decide a machine-scope write, typically the calling app's own
    /// <c>AppContext.FileSystem</c>, so a denied write raises the administrator approval prompt of
    /// ADR 0042. When <see langword="null"/>, the session user's own rights apply. Ignored for the
    /// user scope, always written as the session user.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write (a <c>KazoUnauthorizedAccessException</c>).</exception>
    ValueTask<AppInstallationResult> UninstallAsync(
        string appId,
        AppInstallationScope scope,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reinstalls an app and its uninstalled non-optional dependencies in one scope. A user cannot
    /// reinstall for themselves an app uninstalled for the machine.
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="scope">File to write.</param>
    /// <param name="writer">See <see cref="UninstallAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write (a <c>KazoUnauthorizedAccessException</c>).</exception>
    ValueTask<AppInstallationResult> ReinstallAsync(
        string appId,
        AppInstallationScope scope,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default);
}
