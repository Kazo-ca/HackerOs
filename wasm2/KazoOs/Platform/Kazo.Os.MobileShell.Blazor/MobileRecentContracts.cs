using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.MobileShell.Blazor;

/// <summary>
/// One entry in the Mobile Recent surface (plan §7.5, <c>MOB-011</c>): title and icon only — there
/// is no thumbnail/preview-capture mechanism anywhere in the codebase yet, so a safe preview is
/// achieved by omission rather than by an active protection flag.
/// </summary>
public sealed record MobileRecentItem(WindowId Id, string Title, string? IconAssetPath, bool IsFocused);

/// <summary>
/// Host-supplied labels for the Recent surface's accessible text, mirroring
/// <see cref="MobileSystemNavigationBarOptions"/>. A label left <see langword="null"/> (the default)
/// uses the package's own text, in the host's session language (ADR 0104).
/// </summary>
public sealed record MobileRecentSurfaceOptions
{
    /// <summary>Gets the accessible dialog title; "Recent" by default.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the message shown when no instance is open; "No recent apps" by default.</summary>
    public string? EmptyMessage { get; init; }

    /// <summary>
    /// Gets the prefix combined with an item's title for its close button's accessible name;
    /// by default the package's "Close {title}".
    /// </summary>
    public string? CloseLabelPrefix { get; init; }
}
