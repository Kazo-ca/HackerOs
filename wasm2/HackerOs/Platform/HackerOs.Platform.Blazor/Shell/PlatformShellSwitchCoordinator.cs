using HackerOs.App.Abstractions;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Platform.Core.Shell;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// Drives the controlled platform-switch sequence, per doc/wasm/dev/old/mobile-interface-platform-plan.md §6.3
/// (<c>MOB-008</c>): confirm the dirty state of every window that would need to stop, stop each such
/// window's owning instance with the typed <see cref="ProcessExitReason.PlatformChanged"/> reason and
/// remove its window, then persist the new preference through <see cref="UiPlatformPreferenceService"/>.
/// That service's <see cref="UiPlatformPreferenceService.Changed"/> event is what actually swaps the
/// rendered shell (see <c>App.razor</c>) — this coordinator only clears the way and commits the choice.
/// </summary>
/// <remarks>
/// <para>
/// Per plan §6.3's own qualifier ("ne pas remplacer à chaud le type de composant d'une instance
/// existante... le changement de point d'entrée passe par un arrêt/re-lancement contrôlé"), a window
/// only needs to stop when its app's resolved entry point actually differs between the current and
/// target platform — or the app isn't supported on the target platform at all. A window whose app
/// declares one shared entry point for both platforms (e.g. <c>HackerOs.Samples.PlatformApp</c>)
/// carries over untouched: <see cref="WindowRuntime"/> is a DI singleton shared by
/// <c>DesktopShell</c> and <c>MobileShell</c>, so the next shell simply re-presents the same running
/// window (chromed vs. chromeless) without restarting anything.
/// </para>
/// <para>
/// Deliberately out of scope for this slice: restarting <see cref="AppKind.Service"/> instances
/// whose contract requires it (§6.3 step 5's "ou les redémarrer si leur contrat l'exige") — no
/// service in this codebase yet declares such a requirement, so every running service is left
/// untouched. Re-resolving the launcher/file-association/intent candidate lists against the new
/// active platform (§6.3 step 8) is also deferred: no shipped app manifest declares a Mobile-only
/// entry point yet, so every app that can launch today launches identically regardless of active
/// platform.
/// </para>
/// </remarks>
public sealed class PlatformShellSwitchCoordinator(
    WindowRuntime windowRuntime,
    AppLifecycleOrchestrator orchestrator,
    WindowCloseGuardRegistry closeGuards,
    UiPlatformPreferenceService platformPreference,
    AppCatalog catalog)
{
    /// <summary>
    /// Requests an explicit platform choice. When it would change the active platform, every window
    /// whose app needs a different entry point on the target platform (or isn't supported there at
    /// all) is confirmed and stopped before the choice is persisted; windows whose app shares one
    /// entry point across both platforms carry over untouched.
    /// </summary>
    /// <returns><see langword="false"/> when a dirty window rejected the confirmation and nothing was changed.</returns>
    public async Task<bool> RequestExplicitAsync(AppPlatformId targetPlatform, CancellationToken cancellationToken = default)
    {
        if (platformPreference.Current.ActivePlatform == targetPlatform)
        {
            // No window disruption needed for a same-platform re-selection (e.g. Auto -> explicit
            // Desktop while already active on Desktop); still persist the explicit override itself.
            await platformPreference.SetExplicitAsync(targetPlatform, cancellationToken).ConfigureAwait(false);
            return true;
        }

        IReadOnlyList<WindowRuntimeState> windowsNeedingRestart = SelectWindowsNeedingRestart(targetPlatform);
        if (!await ClearWindowsAsideAsync(windowsNeedingRestart, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await platformPreference.SetExplicitAsync(targetPlatform, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Requests reverting to automatic platform detection. Every open window is confirmed and
    /// stopped first — unlike <see cref="RequestExplicitAsync"/>, the resulting active platform isn't
    /// knowable in advance without re-running environment detection, so no window can be proven safe
    /// to carry over.
    /// </summary>
    /// <returns><see langword="false"/> when a dirty window rejected the confirmation and nothing was changed.</returns>
    public async Task<bool> RequestAutoAsync(CancellationToken cancellationToken = default)
    {
        if (platformPreference.Current.SelectionSource == UiPlatformSelectionSource.Auto)
        {
            return true;
        }

        if (!await ClearWindowsAsideAsync(windowRuntime.Windows, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await platformPreference.ClearToAutoAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Selects every open window whose app either doesn't support <paramref name="targetPlatform"/>
    /// or resolves to a different entry point there than on the current active platform.
    /// </summary>
    private IReadOnlyList<WindowRuntimeState> SelectWindowsNeedingRestart(AppPlatformId targetPlatform)
    {
        AppPlatformId currentPlatform = platformPreference.Current.ActivePlatform;
        List<WindowRuntimeState> windowsNeedingRestart = [];

        foreach (WindowRuntimeState window in windowRuntime.Windows)
        {
            if (!catalog.Manifests.TryGetValue(window.AppId, out AppManifest? manifest))
            {
                // Not in the live catalog (shouldn't happen for a running window) -- restart rather
                // than guess that it's safe to carry over.
                windowsNeedingRestart.Add(window);
                continue;
            }

            AppManifestPlatformResolution? resolution = AppManifestPlatformSupport.Resolve(manifest);
            AppEntryPointManifest? currentEntryPoint = resolution?.EntryPointsByPlatform.GetValueOrDefault(currentPlatform);
            AppEntryPointManifest? targetEntryPoint = resolution?.EntryPointsByPlatform.GetValueOrDefault(targetPlatform);

            if (targetEntryPoint is null || !targetEntryPoint.Equals(currentEntryPoint))
            {
                windowsNeedingRestart.Add(window);
            }
        }

        return windowsNeedingRestart;
    }

    private async Task<bool> ClearWindowsAsideAsync(IReadOnlyList<WindowRuntimeState> windows, CancellationToken cancellationToken)
    {
        foreach (WindowRuntimeState window in windows)
        {
            if (!await closeGuards.ConfirmCloseAsync(window.Id, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        foreach (WindowRuntimeState window in windows)
        {
            await orchestrator.StopAsync(
                AppInstanceId.FromGuid(window.OwnerInstanceId.Value),
                ProcessExitReason.PlatformChanged).ConfigureAwait(false);
            windowRuntime.Apply(new ForceWindowCloseCommand(window.Id));
        }

        return true;
    }
}
