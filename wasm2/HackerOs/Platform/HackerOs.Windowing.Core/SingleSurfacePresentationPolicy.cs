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
    /// <see langword="null"/> when nothing qualifies. When that window has an owner-modal dialog
    /// (a file dialog, a message box...), the dialog is the surface, full screen above its owner
    /// (ADR 0171 §3), even before it got the focus and even if a previous pass minimized it.
    /// </summary>
    public static WindowId? SelectPrimary(IReadOnlyList<WindowRuntimeState> windows) =>
        SelectSurfaceStack(windows) is { Count: > 0 } stack ? stack[^1].Id : null;

    /// <summary>
    /// Gets the windows shown on the single surface, from the bottom up: the selected app window, then
    /// its owner-modal dialogs in order (a dialog opened from a dialog comes last). The owners stay
    /// rendered under their dialog so the app that awaits the dialog keeps its state (ADR 0171 §3);
    /// only the last one is interactive. Empty when nothing qualifies.
    /// </summary>
    public static IReadOnlyList<WindowRuntimeState> SelectSurfaceStack(IReadOnlyList<WindowRuntimeState> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        WindowRuntimeState? selected = windows.FirstOrDefault(
            window => window.IsFocused && window.VisualState != WindowVisualState.Minimized)
            ?? windows
                .Where(window => window.VisualState != WindowVisualState.Minimized)
                .OrderByDescending(window => window.ZOrder)
                .FirstOrDefault();
        if (selected is null)
        {
            return [];
        }

        // A focused dialog's owner is the app window the stack starts from.
        while (selected.Modality == WindowModality.OwnerModal
            && selected.OwnerId is WindowId ownerId
            && windows.FirstOrDefault(window => window.Id == ownerId) is { } owner)
        {
            selected = owner;
        }

        List<WindowRuntimeState> stack = [selected];
        HashSet<WindowId> seen = [selected.Id];
        while (windows
            .Where(window => window.Modality == WindowModality.OwnerModal && window.OwnerId == stack[^1].Id)
            .OrderByDescending(window => window.ZOrder)
            .FirstOrDefault() is { } dialog
            && seen.Add(dialog.Id))
        {
            stack.Add(dialog);
        }

        return stack;
    }

    /// <summary>
    /// Gets every window that must be minimized to keep <paramref name="primaryId"/> the only visible surface.
    /// The owners of a primary dialog are kept: they are shown under it (<see cref="SelectSurfaceStack"/>).
    /// </summary>
    public static IReadOnlyList<WindowId> WindowsToMinimize(IReadOnlyList<WindowRuntimeState> windows, WindowId? primaryId)
    {
        ArgumentNullException.ThrowIfNull(windows);

        HashSet<WindowId> kept = [.. OwnerChain(windows, primaryId)];
        return [.. windows
            .Where(window => !kept.Contains(window.Id) && window.VisualState != WindowVisualState.Minimized)
            .Select(window => window.Id)];
    }

    /// <summary>
    /// Gets the windows of the surface that still need a <see cref="MaximizeWindowCommand"/>: the primary
    /// and the owners shown under it, so a dialog fills the screen like its app.
    /// </summary>
    public static IReadOnlyList<WindowId> WindowsToMaximize(IReadOnlyList<WindowRuntimeState> windows, WindowId? primaryId)
    {
        ArgumentNullException.ThrowIfNull(windows);

        HashSet<WindowId> chain = [.. OwnerChain(windows, primaryId)];
        return [.. windows
            .Where(window => chain.Contains(window.Id) && window.VisualState != WindowVisualState.Maximized)
            .Select(window => window.Id)];
    }

    /// <summary>The primary and, when it is an owner-modal dialog, its owners up to the app window.</summary>
    private static IEnumerable<WindowId> OwnerChain(IReadOnlyList<WindowRuntimeState> windows, WindowId? primaryId)
    {
        HashSet<WindowId> seen = [];
        WindowRuntimeState? current = primaryId is WindowId id ? windows.FirstOrDefault(window => window.Id == id) : null;
        while (current is not null && seen.Add(current.Id))
        {
            yield return current.Id;
            current = current.Modality == WindowModality.OwnerModal && current.OwnerId is WindowId ownerId
                ? windows.FirstOrDefault(window => window.Id == ownerId)
                : null;
        }
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
