namespace HackerOs.App.Abstractions;

/// <summary>
/// Describes how a platform's shell presents system navigation, per
/// doc/wasm/dev/old/mobile-interface-platform-plan.md §3.2/§7.2. Closed set: unlike <see cref="AppPlatformId"/>
/// itself, the finite ways a shell can offer navigation chrome are enumerable today; a future
/// platform introducing a genuinely new navigation paradigm can add a member here without touching
/// <see cref="AppPlatformId"/>'s open registry.
/// </summary>
public enum PlatformSystemNavigationKind
{
    /// <summary>No system navigation chrome; the app owns its entire surface.</summary>
    None,

    /// <summary>A persistent desktop taskbar (see <c>HackerOs.Taskbar.Blazor</c>).</summary>
    DesktopTaskbar,

    /// <summary>An Android-inspired bottom bar with Back/Home/Recent (see plan §7.2).</summary>
    AndroidStyleSystemBar
}

/// <summary>
/// Describes how text input reaches an app on a platform, per plan §9.1. Closed set for the same
/// reason as <see cref="PlatformSystemNavigationKind"/>.
/// </summary>
public enum PlatformKeyboardStrategy
{
    /// <summary>Only a physical keyboard is assumed; no on-screen keyboard is ever shown by the shell.</summary>
    PhysicalOnly,

    /// <summary>The shell can present a HackerOS-owned virtual keyboard instead of the native one.</summary>
    VirtualHackerOsKeyboard
}

/// <summary>
/// Describes one platform's shell capabilities, per plan §3.2 (<c>MOB-002</c>). Registered once per
/// <see cref="AppPlatformId"/> in an <see cref="IAppPlatformCapabilityRegistry"/>; application and
/// shared shell code should query these capabilities through the registry rather than branch on the
/// platform identifier directly, per §3.2's "ne pas multiplier les tests directs" guidance.
/// </summary>
public sealed record AppPlatformCapabilities
{
    /// <summary>Creates validated platform capabilities.</summary>
    public AppPlatformCapabilities(
        AppPlatformId platformId,
        string shellFamily,
        bool supportsFloatingWindows,
        int? maxVisiblePrimarySurfaces,
        bool supportsMove,
        bool supportsResize,
        bool supportsMinimize,
        bool supportsMaximize,
        PlatformSystemNavigationKind systemNavigation,
        bool hasApplicationBar,
        PlatformKeyboardStrategy keyboardStrategy,
        double? minimumViewportWidth = null,
        bool requiresSafeAreaInsets = false)
    {
        if (string.IsNullOrWhiteSpace(shellFamily))
        {
            throw new ArgumentException("Shell family must be non-empty.", nameof(shellFamily));
        }

        if (maxVisiblePrimarySurfaces is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxVisiblePrimarySurfaces),
                "When bounded, the visible primary surface count must be at least 1.");
        }

        if (minimumViewportWidth is <= 0 or double.NaN)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumViewportWidth), "Minimum viewport width must be positive.");
        }

        PlatformId = platformId;
        ShellFamily = shellFamily;
        SupportsFloatingWindows = supportsFloatingWindows;
        MaxVisiblePrimarySurfaces = maxVisiblePrimarySurfaces;
        SupportsMove = supportsMove;
        SupportsResize = supportsResize;
        SupportsMinimize = supportsMinimize;
        SupportsMaximize = supportsMaximize;
        SystemNavigation = systemNavigation;
        HasApplicationBar = hasApplicationBar;
        KeyboardStrategy = keyboardStrategy;
        MinimumViewportWidth = minimumViewportWidth;
        RequiresSafeAreaInsets = requiresSafeAreaInsets;
    }

    /// <summary>Gets the platform this descriptor applies to.</summary>
    public AppPlatformId PlatformId { get; }

    /// <summary>Gets a short identifier for the shell implementation family (e.g. <c>desktop-window-manager</c>).</summary>
    public string ShellFamily { get; }

    /// <summary>Gets whether the shell supports floating, independently positioned windows.</summary>
    public bool SupportsFloatingWindows { get; }

    /// <summary>Gets the maximum number of primary surfaces visible at once, or <see langword="null"/> when unbounded.</summary>
    public int? MaxVisiblePrimarySurfaces { get; }

    /// <summary>Gets whether the shell lets the user move a surface.</summary>
    public bool SupportsMove { get; }

    /// <summary>Gets whether the shell lets the user resize a surface.</summary>
    public bool SupportsResize { get; }

    /// <summary>Gets whether the shell lets the user minimize a surface.</summary>
    public bool SupportsMinimize { get; }

    /// <summary>Gets whether the shell lets the user maximize/restore a surface.</summary>
    public bool SupportsMaximize { get; }

    /// <summary>Gets the system navigation chrome this platform's shell provides.</summary>
    public PlatformSystemNavigationKind SystemNavigation { get; }

    /// <summary>Gets whether an app's own application bar (e.g. a Desktop-only Back button, per plan §8) is shown.</summary>
    public bool HasApplicationBar { get; }

    /// <summary>Gets how text input reaches an app on this platform.</summary>
    public PlatformKeyboardStrategy KeyboardStrategy { get; }

    /// <summary>Gets the minimum logical viewport width this platform's shell is designed for, when constrained.</summary>
    public double? MinimumViewportWidth { get; }

    /// <summary>Gets whether surfaces on this platform must respect <c>env(safe-area-inset-*)</c>.</summary>
    public bool RequiresSafeAreaInsets { get; }
}

/// <summary>
/// Looks up registered <see cref="AppPlatformCapabilities"/> by <see cref="AppPlatformId"/>. The
/// platform catalog belongs to the policy/build system (plan §3.1), not to a switch compiled into
/// each application.
/// </summary>
public interface IAppPlatformCapabilityRegistry
{
    /// <summary>Gets every currently registered platform identifier.</summary>
    IReadOnlyCollection<AppPlatformId> KnownPlatforms { get; }

    /// <summary>Attempts to look up a registered platform's capabilities.</summary>
    /// <param name="platformId">Platform to look up.</param>
    /// <param name="capabilities">Registered capabilities when found.</param>
    /// <returns><see langword="true"/> when the platform is registered.</returns>
    bool TryGet(AppPlatformId platformId, out AppPlatformCapabilities? capabilities);
}

/// <summary>
/// Mutable, in-memory <see cref="IAppPlatformCapabilityRegistry"/>. Registering the same
/// <see cref="AppPlatformId"/> twice is a build/startup configuration error and throws immediately
/// rather than silently overwriting a platform's capabilities.
/// </summary>
public sealed class AppPlatformCapabilityRegistry : IAppPlatformCapabilityRegistry
{
    private readonly Dictionary<AppPlatformId, AppPlatformCapabilities> _byPlatform = [];

    /// <inheritdoc />
    public IReadOnlyCollection<AppPlatformId> KnownPlatforms => _byPlatform.Keys;

    /// <summary>Registers a platform's capabilities.</summary>
    /// <param name="capabilities">Capabilities to register.</param>
    /// <exception cref="ArgumentException">The platform is already registered.</exception>
    public void Register(AppPlatformCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!_byPlatform.TryAdd(capabilities.PlatformId, capabilities))
        {
            throw new ArgumentException(
                $"Platform '{capabilities.PlatformId}' is already registered.",
                nameof(capabilities));
        }
    }

    /// <inheritdoc />
    public bool TryGet(AppPlatformId platformId, out AppPlatformCapabilities? capabilities) =>
        _byPlatform.TryGetValue(platformId, out capabilities);

    /// <summary>Creates a registry pre-populated with the built-in Desktop and Mobile platforms.</summary>
    public static AppPlatformCapabilityRegistry CreateWithWellKnownPlatforms()
    {
        AppPlatformCapabilityRegistry registry = new();
        registry.Register(WellKnownAppPlatformCapabilities.Desktop);
        registry.Register(WellKnownAppPlatformCapabilities.Mobile);
        return registry;
    }
}

/// <summary>Built-in capability descriptors for the platforms shipped by HackerOS itself.</summary>
public static class WellKnownAppPlatformCapabilities
{
    /// <summary>Floating windows, a persistent taskbar, pointer/keyboard-first input.</summary>
    public static AppPlatformCapabilities Desktop { get; } = new(
        platformId: WellKnownAppPlatforms.Desktop,
        shellFamily: "desktop-window-manager",
        supportsFloatingWindows: true,
        maxVisiblePrimarySurfaces: null,
        supportsMove: true,
        supportsResize: true,
        supportsMinimize: true,
        supportsMaximize: true,
        systemNavigation: PlatformSystemNavigationKind.DesktopTaskbar,
        hasApplicationBar: true,
        keyboardStrategy: PlatformKeyboardStrategy.PhysicalOnly);

    /// <summary>A single full-screen surface, Android-style system nav bar, touch-first input.</summary>
    public static AppPlatformCapabilities Mobile { get; } = new(
        platformId: WellKnownAppPlatforms.Mobile,
        shellFamily: "mobile-single-surface",
        supportsFloatingWindows: false,
        maxVisiblePrimarySurfaces: 1,
        supportsMove: false,
        supportsResize: false,
        supportsMinimize: false,
        supportsMaximize: false,
        systemNavigation: PlatformSystemNavigationKind.AndroidStyleSystemBar,
        hasApplicationBar: false,
        keyboardStrategy: PlatformKeyboardStrategy.VirtualHackerOsKeyboard,
        requiresSafeAreaInsets: true);
}
