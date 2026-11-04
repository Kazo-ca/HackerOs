using Kazo.Os.MobileShell.Blazor;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// Pure projection from authoritative window state to the Mobile Recent surface's item shape
/// (plan §7.5, <c>MOB-011</c>), matching the style of
/// <see cref="Kazo.Os.Windowing.Core.SingleSurfacePresentationPolicy"/>: no DI, no host
/// dependency, trivially unit-testable.
/// </summary>
public static class MobileRecentProjection
{
    /// <summary>
    /// Builds the Recent list ordered most-recently-focused first, using <see cref="WindowRuntimeState.ZOrder"/>
    /// as the recency signal — the same monotonic focus counter <c>WindowRuntime</c> already
    /// maintains, so no separate recency tracking is introduced.
    /// </summary>
    /// <remarks>
    /// Does not filter by live app enablement or platform compatibility: no app in the current
    /// catalog declares a Mobile-only entry point, and disabling an app while one of its windows is
    /// still open is an edge case unhandled anywhere else in the codebase either — see plan §16.5's
    /// identical reasoning for deferring the same filter in the launcher/intent surfaces.
    /// </remarks>
    public static IReadOnlyList<MobileRecentItem> BuildItems(IReadOnlyList<WindowRuntimeState> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        return [.. windows
            .OrderByDescending(window => window.ZOrder)
            .Select(window => new MobileRecentItem(window.Id, window.Title, window.IconAssetPath, window.IsFocused))];
    }
}
