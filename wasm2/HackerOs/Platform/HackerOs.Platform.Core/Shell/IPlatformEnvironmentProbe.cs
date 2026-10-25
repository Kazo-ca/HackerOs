using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Shell;

/// <summary>
/// Raw, unopinionated environment signals a platform probe implementation collects. Kept separate
/// from the decision itself (<see cref="PlatformEnvironmentPolicy"/>) so the decision logic is
/// testable without a browser.
/// </summary>
/// <param name="LogicalWidth">Viewport width in CSS pixels.</param>
/// <param name="LogicalHeight">Viewport height in CSS pixels.</param>
/// <param name="PointerIsCoarse">Whether the primary pointer is coarse (touch-like) per <c>(pointer: coarse)</c>.</param>
/// <param name="HasHover">Whether the primary input can hover per <c>(hover: hover)</c>.</param>
/// <param name="MaxTouchPoints">Maximum simultaneous touch points the device reports.</param>
/// <param name="IsStandalone">Whether the app is running as an installed/standalone PWA.</param>
public sealed record PlatformEnvironmentSignals(
    int LogicalWidth,
    int LogicalHeight,
    bool PointerIsCoarse,
    bool HasHover,
    int MaxTouchPoints,
    bool IsStandalone);

/// <summary>
/// The environment probe's current reasoned conclusion. Kept loggable/observable per
/// doc/en/wasm/dev/old/mobile-interface-platform-plan.md §6.2 — auto-detection must never be a silent black box.
/// </summary>
/// <param name="Signals">Raw signals the decision was made from.</param>
/// <param name="SuggestedPlatform">The platform automatic detection currently suggests.</param>
/// <param name="Reason">Human-readable explanation of why, for diagnostics/logging.</param>
public sealed record PlatformEnvironmentSnapshot(
    PlatformEnvironmentSignals Signals,
    AppPlatformId SuggestedPlatform,
    string Reason);

/// <summary>
/// Observes the host environment to suggest an active platform when the user has not made an
/// explicit choice (see <c>UiPlatformPreferenceService</c>). Never the sole authority — an explicit
/// user choice always wins; this is only consulted for the "Auto" case.
/// </summary>
public interface IPlatformEnvironmentProbe
{
    /// <summary>Gets the current reasoned snapshot. Valid only after <see cref="InitializeAsync"/> completes.</summary>
    PlatformEnvironmentSnapshot Current { get; }

    /// <summary>Raised when the environment changes in a way that could change <see cref="Current"/>.</summary>
    event Action? Changed;

    /// <summary>Starts observing the environment and populates the first <see cref="Current"/> snapshot.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Pure decision logic over <see cref="PlatformEnvironmentSignals"/>, isolated from any browser
/// interop so it is unit-testable in isolation (see doc/en/wasm/dev/old/mobile-interface-platform-plan.md §6.2:
/// "a Desktop window resize must not be mistaken for a device change").
/// </summary>
public static class PlatformEnvironmentPolicy
{
    /// <summary>
    /// Width, in CSS pixels, below which a coarse/no-hover pointer environment is treated as
    /// mobile-shaped. Deliberately conservative and combined with pointer/touch signals — width
    /// alone never decides (a narrowed Desktop browser window must stay Desktop).
    /// </summary>
    public const int NarrowViewportThreshold = 600;

    /// <summary>Decides the suggested platform from raw environment signals, with a stated reason.</summary>
    public static PlatformEnvironmentSnapshot Decide(PlatformEnvironmentSignals signals)
    {
        // Touch capability and pointer coarseness are the load-bearing signals — a real mobile
        // device reports MaxTouchPoints > 0 and a coarse, non-hovering primary pointer. A Desktop
        // browser window resized narrow still reports MaxTouchPoints == 0 / a fine, hovering
        // pointer, so it never flips here regardless of width.
        bool looksLikeTouchDevice = signals.MaxTouchPoints > 0 && signals.PointerIsCoarse && !signals.HasHover;

        if (!looksLikeTouchDevice)
        {
            return new PlatformEnvironmentSnapshot(
                signals,
                WellKnownAppPlatforms.Desktop,
                "Desktop: pointer is fine/hover-capable or the device reports no touch points.");
        }

        if (signals.LogicalWidth <= NarrowViewportThreshold)
        {
            return new PlatformEnvironmentSnapshot(
                signals,
                WellKnownAppPlatforms.Mobile,
                $"Mobile: touch-capable coarse pointer with no hover and a viewport width ({signals.LogicalWidth}px) at or under the {NarrowViewportThreshold}px threshold.");
        }

        return new PlatformEnvironmentSnapshot(
            signals,
            WellKnownAppPlatforms.Desktop,
            $"Desktop: touch-capable coarse pointer, but viewport width ({signals.LogicalWidth}px) is above the {NarrowViewportThreshold}px mobile threshold (e.g. a touch-screen laptop).");
    }
}
