using HackerOs.Windowing.Abstractions;

namespace HackerOs.Windowing.Core;

/// <summary>
/// Pure decision logic for a "single full-screen surface" presentation mode, per
/// doc/en/wasm/dev/old/mobile-interface-platform-plan.md §7.1 (<c>MOB-009</c>): exactly one window is visible and
/// filling the work area at a time; every other window is hidden. Deliberately independent of
/// <see cref="WindowRuntime"/> and Blazor — a caller (e.g. a Blazor host component) reads these
/// decisions and issues the corresponding <see cref="WindowCommand"/>s itself, the same separation
/// <see cref="WindowRuntime"/> already keeps between deciding and applying state.
/// </summary>
public static class SingleSurfacePresentationPolicy
{
    /// <summary>
    /// Selects the window that should be the single visible surface: the focused, non-minimized
    /// window if one exists, otherwise the frontmost non-minimized window, otherwise
    /// <see langword="null"/> when nothing qualifies.
    /// </summary>
    public static WindowId? SelectPrimary(IReadOnlyList<WindowRuntimeState> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        WindowRuntimeState? focused = windows.FirstOrDefault(
            window => window.IsFocused && window.VisualState != WindowVisualState.Minimized);
        if (focused is not null)
        {
            return focused.Id;
        }

        WindowRuntimeState? frontmost = windows
            .Where(window => window.VisualState != WindowVisualState.Minimized)
            .OrderByDescending(window => window.ZOrder)
            .FirstOrDefault();
        return frontmost?.Id;
    }

    /// <summary>Gets every window that must be minimized to keep <paramref name="primaryId"/> the only visible surface.</summary>
    public static IReadOnlyList<WindowId> WindowsToMinimize(IReadOnlyList<WindowRuntimeState> windows, WindowId? primaryId)
    {
        ArgumentNullException.ThrowIfNull(windows);

        return [.. windows
            .Where(window => window.Id != primaryId && window.VisualState != WindowVisualState.Minimized)
            .Select(window => window.Id)];
    }

    /// <summary>
    /// Gets whether <paramref name="primaryId"/> still needs a <see cref="MaximizeWindowCommand"/> to
    /// fill the work area — reuses the existing Maximize-fills-work-area mechanism
    /// (<see cref="WindowRuntime"/>) rather than computing full-screen geometry separately.
    /// </summary>
    public static bool ShouldMaximizePrimary(IReadOnlyList<WindowRuntimeState> windows, WindowId? primaryId) =>
        primaryId is WindowId id
        && windows.FirstOrDefault(window => window.Id == id) is { VisualState: not WindowVisualState.Maximized };
}
