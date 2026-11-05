using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// Produces the deterministic, desktop-safe application lists consumed by <see cref="AppLauncher"/>.
/// Keeping catalog policy here prevents the Razor component from drifting from manifest semantics
/// and makes category, search, and pin behavior independently testable.
/// </summary>
public static class LauncherAppProjection
{
    /// <summary>
    /// Builds one immutable launcher snapshot from the catalog and the current user preferences.
    /// </summary>
    /// <param name="manifests">Installed application manifests.</param>
    /// <param name="enablement">Registry that owns the current enabled/disabled state.</param>
    /// <param name="pinnedAppIds">
    /// Ordered persisted IDs. Unknown or unavailable IDs are intentionally ignored in the visual
    /// projection without being removed from the caller's persisted list.
    /// </param>
    /// <param name="selectedCategory">Selected manifest category, or <see langword="null"/> for all.</param>
    /// <param name="searchQuery">
    /// Optional global query. A non-empty query searches all launchable apps and takes precedence
    /// over the selected category, matching the behavior of a desktop Start menu.
    /// </param>
    /// <returns>A deterministic snapshot ready for rendering.</returns>
    public static LauncherAppProjectionResult Create(
        IEnumerable<AppManifest> manifests,
        IAppEnablementRegistry enablement,
        IEnumerable<string> pinnedAppIds,
        string? selectedCategory = null,
        string? searchQuery = null)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(enablement);
        ArgumentNullException.ThrowIfNull(pinnedAppIds);

        AppManifest[] launchableApps = manifests
            .Where(manifest => IsDesktopLaunchable(manifest, enablement))
            .OrderBy(manifest => manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(manifest => manifest.Id, StringComparer.Ordinal)
            .ToArray();

        string[] categories = launchableApps
            .Select(manifest => manifest.Presentation.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(category => category, StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, AppManifest> byId = launchableApps.ToDictionary(
            manifest => manifest.Id,
            StringComparer.Ordinal);
        HashSet<string> emittedPinIds = new(StringComparer.Ordinal);
        AppManifest[] pinnedApps = pinnedAppIds
            .Where(emittedPinIds.Add)
            .Select(appId => byId.GetValueOrDefault(appId))
            .Where(manifest => manifest is not null)
            .Cast<AppManifest>()
            .ToArray();

        IEnumerable<AppManifest> visibleApps = launchableApps;
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            string normalizedQuery = searchQuery.Trim();
            visibleApps = visibleApps.Where(manifest => MatchesSearch(manifest, normalizedQuery));
        }
        else if (!string.IsNullOrWhiteSpace(selectedCategory))
        {
            visibleApps = visibleApps.Where(manifest => StringComparer.OrdinalIgnoreCase.Equals(
                manifest.Presentation.Category,
                selectedCategory));
        }

        return new LauncherAppProjectionResult(
            launchableApps,
            pinnedApps,
            visibleApps.ToArray(),
            categories);
    }

    private static bool IsDesktopLaunchable(AppManifest manifest, IAppEnablementRegistry enablement)
    {
        if (manifest.Kind != AppKind.Window
            || manifest.Presentation.LaunchVisibility != AppLaunchVisibility.Visible
            || !enablement.IsEnabled(manifest.Id))
        {
            return false;
        }

        AppManifestPlatformResolution? platform = AppManifestPlatformSupport.Resolve(manifest);
        return platform?.SupportedPlatforms.Contains(WellKnownAppPlatforms.Desktop) == true;
    }

    private static bool MatchesSearch(AppManifest manifest, string query) =>
        manifest.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || manifest.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || manifest.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || manifest.Presentation.Category.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Represents one deterministic, render-ready Start menu application snapshot.</summary>
/// <param name="LaunchableApps">Every enabled, visible desktop window app, sorted by name.</param>
/// <param name="PinnedApps">Available pinned apps in the exact persisted order.</param>
/// <param name="VisibleApps">Apps matching the current category or global search.</param>
/// <param name="Categories">Dynamic manifest category identifiers in display order.</param>
public sealed record LauncherAppProjectionResult(
    IReadOnlyList<AppManifest> LaunchableApps,
    IReadOnlyList<AppManifest> PinnedApps,
    IReadOnlyList<AppManifest> VisibleApps,
    IReadOnlyList<string> Categories)
{
    /// <summary>Gets an empty projection suitable for the component's pre-initialization state.</summary>
    public static LauncherAppProjectionResult Empty { get; } = new([], [], [], []);
}
