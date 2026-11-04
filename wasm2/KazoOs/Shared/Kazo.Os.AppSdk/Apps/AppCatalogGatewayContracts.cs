using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Apps;

/// <summary>
/// Read access to the app catalog for one app instance (ADR 0056, section 2), behind
/// <see cref="KazoApps"/>. Needs no capability: the list of installed apps is public, like
/// <c>/usr/share/applications</c>. The default handlers (ADR 0121) are the exception.
/// </summary>
public interface IAppCatalogGateway
{
    /// <summary>
    /// Raised after an app was installed, uninstalled or reinstalled for the session user (an
    /// <c>apps.config</c> change, ADR 0048). Handlers run inside the subscribing app's ambient scope.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>Gets every app installed for the session user, sorted by name. Uninstalled apps are hidden (ADR 0048).</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<IReadOnlyList<KazoAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the installed window apps that declare they can open <paramref name="path"/> ("Open
    /// with"): the default first (the user's own in <c>~/.config/hackeros/file-associations.json</c>,
    /// else the one of <c>/etc/hackeros/file-associations.json</c>, ADR 0121), then the others by name.
    /// </summary>
    /// <param name="path">File to open.</param>
    /// <param name="mediaType">MIME type when known, matched besides the extension.</param>
    /// <param name="cancellationToken">Cancels the read of the associations file.</param>
    ValueTask<IReadOnlyList<KazoAppInfo>> GetHandlersAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every configured default handler of the user (ADR 0121, section 3): the entries of
    /// <c>~/.config/hackeros/file-associations.json</c> first, then those of
    /// <c>/etc/hackeros/file-associations.json</c>. A user entry hides the system entry with the
    /// same target and action, so the first row of a pair is the one used. Needs
    /// <c>file-associations.read</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="AppGatewayAccessDeniedException">The app lacks <c>file-associations.read</c>.</exception>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0121.</exception>
    ValueTask<IReadOnlyList<KazoFileAssociation>> GetDefaultHandlersAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This catalog gateway cannot read the default handlers.");

    /// <summary>
    /// Sets or removes the user's own default handler of one file type or URI scheme (ADR 0121,
    /// section 3), in <c>~/.config/hackeros/file-associations.json</c>; the machine-wide file is
    /// never written here. Needs <c>file-associations.read</c> and <c>filesystem.user-home.write</c>.
    /// </summary>
    /// <param name="target">Extension with its dot (<c>.md</c>) or media type (<c>x-scheme-handler/https</c>).</param>
    /// <param name="action"><c>open</c> or <c>edit</c>.</param>
    /// <param name="appId">New default, which must declare the target and action; <see langword="null"/> removes the user's choice.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="AppGatewayAccessDeniedException">The app lacks one of the two capabilities.</exception>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0121.</exception>
    ValueTask<KazoDefaultHandlerStatus> SetUserDefaultHandlerAsync(
        string target, string action, string? appId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This catalog gateway cannot change the default handlers.");
}

/// <summary>Installation state of an app for the session user (ADR 0048, section 4).</summary>
public enum KazoAppInstallState
{
    /// <summary>Installed, or a system app.</summary>
    Installed = 0,

    /// <summary>Uninstalled in the user's own <c>~/.config/hackeros/apps.config</c>, or by a dependency there.</summary>
    UninstalledForUser = 1,

    /// <summary>Uninstalled in <c>/etc/hackeros/apps.config</c> for every user: only an administrator reinstalls it.</summary>
    UninstalledForMachine = 2,

    /// <summary>
    /// Shipped by the host but refused by the OS (ADR 0098 §3): built for an App SDK range that does not
    /// contain the running SDK, or requiring such an app. It never launches; <see cref="KazoAppInstallInfo.Problem"/> says why.
    /// </summary>
    Incompatible = 3,

    /// <summary>
    /// An asset of the app did not match the hash of its manifest when first read (ADR 0098 §5); the
    /// asset was refused. <see cref="KazoAppInstallInfo.Problem"/> names it.
    /// </summary>
    Damaged = 4
}

/// <summary>Which <c>apps.config</c> file an uninstall or reinstall writes (ADR 0048, section 3).</summary>
public enum KazoInstallScope
{
    /// <summary><c>~/.config/hackeros/apps.config</c>: the session user only.</summary>
    User = 0,

    /// <summary><c>/etc/hackeros/apps.config</c>: every user; an administrator must authenticate (ADR 0055, section 5).</summary>
    Machine = 1
}

/// <summary>One catalog app with its installation state (ADR 0056, section 3).</summary>
/// <param name="App">Public description of the app.</param>
/// <param name="State">Installation state for the session user.</param>
/// <param name="UninstallClosure">
/// Every app an uninstall would remove: the apps that need it through non-optional dependencies,
/// dependents first, then the app itself. A confirmation dialog lists them.
/// </param>
public sealed record KazoAppInstallInfo(KazoAppInfo App, KazoAppInstallState State, IReadOnlyList<string> UninstallClosure)
{
    /// <summary>
    /// Gets why the app is <see cref="KazoAppInstallState.Incompatible"/> (the SDK range it asked for
    /// and the one the system runs) or <see cref="KazoAppInstallState.Damaged"/> (the altered asset),
    /// or <see langword="null"/> for any other state (ADR 0098 §3, §5).
    /// </summary>
    public string? Problem { get; init; }
}

/// <summary>
/// Uninstall and reinstall for one app instance (ADR 0056, section 3), behind
/// <see cref="KazoAppManagement"/>. Every member requires the dangerous capability
/// <c>apps.manage</c>; the machine scope also requires an administrator through the OS
/// authentication agent, unless the process already runs as root.
/// </summary>
public interface IAppManagementGateway
{
    /// <summary>Gets every catalog app (installed or not) with its state, sorted by name.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<IReadOnlyList<KazoAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Uninstalls an app and its dependents; returns every app ID written, in order.</summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="scope">File to write.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    ValueTask<IReadOnlyList<string>> UninstallAsync(
        string appId, KazoInstallScope scope, CancellationToken cancellationToken = default);

    /// <summary>Reinstalls an app and its uninstalled dependencies; returns every app ID written, in order.</summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="scope">File to write.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    ValueTask<IReadOnlyList<string>> ReinstallAsync(
        string appId, KazoInstallScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells, without changing anything, what an uninstall of <paramref name="appId"/> would do
    /// (ADR 0097, section 5): every app it removes and the running processes of those apps.
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0097.</exception>
    ValueTask<KazoUninstallPlan> PlanUninstallAsync(string appId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot plan an uninstall.");

    /// <summary>
    /// Uninstalls like <see cref="UninstallAsync"/> and reports the processes it stopped, each
    /// marked when it had to be forced after its grace period (ADR 0097, section 5).
    /// </summary>
    /// <param name="appId">Exact catalog app ID.</param>
    /// <param name="scope">File to write.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    async ValueTask<KazoUninstallReport> UninstallWithReportAsync(
        string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
        new(await UninstallAsync(appId, scope, cancellationToken).ConfigureAwait(false), []);

    /// <summary>Reads <c>/etc/hackeros/app-sources.config</c> (ADR 0182 §3).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0182.</exception>
    ValueTask<KazoPackageSources> GetPackageSourcesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot install packages.");

    /// <summary>Lists the apps installed from a package (ADR 0182 §5).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0182.</exception>
    ValueTask<IReadOnlyList<KazoInstalledPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot install packages.");

    /// <summary>Downloads and reads the <c>index.json</c> of a repository (ADR 0182 §2).</summary>
    /// <param name="source"><c>github:owner/repo</c>, <c>owner/repo</c> or <c>https://github.com/owner/repo</c>.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0182.</exception>
    ValueTask<KazoPackageInfo> GetPackageInfoAsync(string source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot install packages.");

    /// <summary>
    /// Installs or updates an app from its repository (ADR 0182 §5, ADR 0189): for the session
    /// user without an administrator from a trusted source, otherwise for the system after an
    /// administrator's two-step approval. The app appears at the next start of the OS.
    /// </summary>
    /// <param name="source">The repository, optionally followed by <c>@version</c>.</param>
    /// <param name="scope">For whom; <see cref="KazoPackageScope.Auto"/> picks the personal install when allowed.</param>
    /// <param name="cancellationToken">Cancels the download, the approval and the writes.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0182.</exception>
    ValueTask<KazoPackageInstallReport> InstallPackageAsync(
        string source, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot install packages.");

    /// <summary>
    /// Removes an app installed from a package: the session user's own copy without an
    /// administrator, a system one after the two-step approval. Settings stay; it leaves the
    /// catalog at once, or at the next start of the OS when it is loaded in the page (ADR 0214 §1).
    /// </summary>
    /// <param name="appId">The installed app.</param>
    /// <param name="scope">Which copy; <see cref="KazoPackageScope.Auto"/> takes the user's own, else the system's.</param>
    /// <param name="cancellationToken">Cancels the approval and the writes.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0182.</exception>
    ValueTask<KazoInstalledPackage> RemovePackageAsync(
        string appId, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot install packages.");

    /// <summary>
    /// Puts back the version the last update of an installed app kept (ADR 0214 §4.1), with the
    /// approvals an install of the same scope needs, and keeps the version it leaves.
    /// </summary>
    /// <param name="appId">The installed app.</param>
    /// <param name="scope">Which copy; <see cref="KazoPackageScope.Auto"/> takes the user's own, else the system's.</param>
    /// <param name="cancellationToken">Cancels the approval and the writes.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0214.</exception>
    ValueTask<KazoPackageInstallReport> RollbackPackageAsync(
        string appId, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot go back to a previous version.");

    /// <summary>
    /// Finishes or undoes the installs an interruption left, then deletes unfinished folders
    /// (<c>hackpkg repair</c>, ADR 0214 §3.5): the session user's own apps, and the system's after
    /// an administrator authenticated when <paramref name="system"/> is set.
    /// </summary>
    /// <param name="system">Also repairs the apps installed for the system.</param>
    /// <param name="cancellationToken">Cancels the authentication and the writes.</param>
    /// <returns>One line per repaired app.</returns>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0214.</exception>
    ValueTask<IReadOnlyList<string>> RepairPackagesAsync(bool system, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot repair installed apps.");

    /// <summary>Adds a catalog or a GitHub source to <c>app-sources.config</c>, after an administrator authenticated (ADR 0189 §1).</summary>
    /// <param name="source"><c>name=https://…/catalog.json</c>, <c>github:owner</c> or <c>github:owner/repo</c>.</param>
    /// <param name="trusted">Whether its apps install for one user without an administrator.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0189.</exception>
    ValueTask<KazoPackageSources> AddPackageSourceAsync(string source, bool trusted, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot change the app sources.");

    /// <summary>Removes a source from <c>app-sources.config</c>, after an administrator authenticated.</summary>
    /// <param name="source">A catalog name (<c>catalog:name</c>) or a GitHub owner or repository.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0189.</exception>
    ValueTask<KazoPackageSources> RemovePackageSourceAsync(string source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot change the app sources.");

    /// <summary>Reads the administrator's block list, <c>/etc/hackeros/app-policy.config</c> (ADR 0189 §5).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0189.</exception>
    ValueTask<KazoPackagePolicy> GetPackagePolicyAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway has no app block list.");

    /// <summary>Blocks or unblocks an app, a category or a capability, after an administrator authenticated.</summary>
    /// <param name="kind">Kind of rule.</param>
    /// <param name="value">App ID, category or capability.</param>
    /// <param name="blocked"><see langword="true"/> to block, <see langword="false"/> to unblock.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0189.</exception>
    ValueTask<KazoPackagePolicy> SetPackagePolicyAsync(
        KazoPackageBlockKind kind, string value, bool blocked, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway has no app block list.");

    /// <summary>Reads every catalog of <c>[catalogs]</c> (ADR 0188 §6, ADR 0212 §2).</summary>
    /// <param name="refresh">Downloads again even when a copy younger than five minutes exists.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0212.</exception>
    ValueTask<IReadOnlyList<KazoAppCatalog>> GetPackageCatalogsAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot read app catalogs.");

    /// <summary>Downloads the long Markdown description of an app of a catalog.</summary>
    /// <param name="catalog">Catalog name.</param>
    /// <param name="appId">The app.</param>
    /// <param name="version">One of its versions.</param>
    /// <param name="language">Language wanted; English when it has none in that language.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0212.</exception>
    ValueTask<string?> GetCatalogDescriptionAsync(
        string catalog, string appId, string version, string language, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot read app catalogs.");

    /// <summary>Lists the installed apps whose source publishes a newer version (ADR 0188 §3).</summary>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0212.</exception>
    ValueTask<IReadOnlyList<KazoPackageUpdate>> GetPackageUpdatesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot check for updates.");

    /// <summary>Restarts the OS so the installed, updated or removed apps take effect (ADR 0212 §3).</summary>
    /// <param name="cancellationToken">Cancels the request before the restart started.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0212.</exception>
    ValueTask RestartForPackagesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot restart the OS.");

    /// <summary>Lists the theme packs installed for the system and for the session user (ADR 0213 §3).</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0213.</exception>
    ValueTask<IReadOnlyList<KazoInstalledThemePack>> GetInstalledThemePacksAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot install theme packs.");

    /// <summary>
    /// Installs or updates a theme pack of a catalog (ADR 0213 §4): for the session user without an
    /// administrator from a trusted catalog, otherwise for the system after the two-step approval.
    /// It applies at once, without a restart.
    /// </summary>
    /// <param name="source"><c>catalog:&lt;name&gt;/&lt;packId&gt;</c> or a pack id alone, optionally followed by <c>@version</c>.</param>
    /// <param name="scope">For whom; <see cref="KazoPackageScope.Auto"/> picks the personal install when allowed.</param>
    /// <param name="cancellationToken">Cancels the download, the approval and the writes.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0213.</exception>
    ValueTask<KazoThemePackInstallReport> InstallThemePackAsync(
        string source, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot install theme packs.");

    /// <summary>Removes an installed theme pack: the user's own copy without an administrator, a system one after the two-step approval.</summary>
    /// <param name="packId">The installed pack.</param>
    /// <param name="scope">Which copy; <see cref="KazoPackageScope.Auto"/> takes the user's own, else the system's.</param>
    /// <param name="cancellationToken">Cancels the approval and the writes.</param>
    /// <exception cref="NotSupportedException">The gateway predates ADR 0213.</exception>
    ValueTask<KazoInstalledThemePack> RemoveThemePackAsync(
        string packId, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app-management gateway cannot install theme packs.");
}

/// <summary>One running process of an app, as an uninstall lists it (ADR 0097, section 5).</summary>
/// <param name="Pid">Process ID.</param>
/// <param name="AppId">App the process runs.</param>
/// <param name="Name">The app's name.</param>
/// <param name="UserName">Login name of the user it runs as.</param>
/// <param name="Forced">In a report: it did not finish its stop work within its grace period and was cut off.</param>
public sealed record KazoRunningProcessInfo(long Pid, string AppId, string Name, string UserName, bool Forced = false);

/// <summary>What an uninstall would do (ADR 0097, section 5), for its confirmation dialog.</summary>
/// <param name="AppIds">Every app it removes, dependents first, then the app itself.</param>
/// <param name="RunningProcesses">The running processes of those apps, which the uninstall stops.</param>
public sealed record KazoUninstallPlan(IReadOnlyList<string> AppIds, IReadOnlyList<KazoRunningProcessInfo> RunningProcesses);

/// <summary>What an uninstall did (ADR 0097, section 5).</summary>
/// <param name="AppIds">Every app uninstalled, dependents first.</param>
/// <param name="StoppedProcesses">Every process it stopped; <see cref="KazoRunningProcessInfo.Forced"/> marks the ones cut off.</param>
public sealed record KazoUninstallReport(IReadOnlyList<string> AppIds, IReadOnlyList<KazoRunningProcessInfo> StoppedProcesses)
{
    /// <summary>Gets the stopped processes that had to be forced after their grace period.</summary>
    public IReadOnlyList<KazoRunningProcessInfo> ForcedProcesses => [.. StoppedProcesses.Where(static process => process.Forced)];
}
