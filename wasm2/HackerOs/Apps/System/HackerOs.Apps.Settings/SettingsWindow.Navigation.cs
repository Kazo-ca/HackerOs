using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace HackerOs.Apps.Settings;

/// <summary>One page of the side menu (ADR 0170): its id for <c>--page=</c>, its group, its label key and its icon.</summary>
internal sealed record SettingsPage(string Id, string GroupKey, string LabelKey, string Icon);

/// <summary>
/// The side menu of Settings (ADR 0170): a vertical list of grouped pages that the user collapses to an icon
/// rail or expands with labels. In a narrow window (a phone), the menu stays a rail and opens over the page.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>Id of the "Appearance" page, the first one (<c>--page=appearance</c>).</summary>
    internal const string AppearancePageId = "appearance";

    /// <summary>Id of the "Desktop" page (<c>--page=desktop</c>, ADR 0160 §8).</summary>
    internal const string DesktopPageId = "desktop";

    /// <summary>Id of the "Backup" page, only listed when the host supports it (ADR 0099).</summary>
    internal const string BackupPageId = "backup";

    /// <summary>Id of the "Application" page, only listed for <c>--app=&lt;id&gt;</c> (ADR 0096 §4).</summary>
    internal const string ApplicationPageId = "application";

    /// <summary>Every page in menu order; <see cref="PagesFor"/> drops the optional ones.</summary>
    internal static IReadOnlyList<SettingsPage> AllPages { get; } =
    [
        new(AppearancePageId, "Nav.Group.Personalization", "Tab.Appearance", Icons.Material.Filled.Palette),
        new(DesktopPageId, "Nav.Group.Personalization", "Tab.Desktop", Icons.Material.Filled.DesktopWindows),
        new("accessibility", "Nav.Group.Personalization", "Tab.Accessibility", Icons.Material.Filled.Accessibility),
        new("language", "Nav.Group.Personalization", "Tab.Language", Icons.Material.Filled.Translate),
        new("file-associations", "Nav.Group.Applications", "Tab.FileAssociations", Icons.Material.Filled.Extension),
        new("capabilities", "Nav.Group.Applications", "Tab.CapabilityGrants", Icons.Material.Filled.Security),
        new("permissions", "Nav.Group.Applications", "Tab.Permissions", Icons.Material.Filled.AdminPanelSettings),
        new("installed-apps", "Nav.Group.Applications", "Tab.InstalledApps", Icons.Material.Filled.Apps),
        new("os-components", "Nav.Group.Applications", "Tab.OsComponents", Icons.Material.Filled.Widgets),
        new("file-menus", "Nav.Group.Applications", "Tab.FileMenus", Icons.Material.Filled.MenuOpen),
        new(ApplicationPageId, "Nav.Group.Applications", "Tab.Application", Icons.Material.Filled.Info),
        new("server", "Nav.Group.Network", "Tab.ServerConnection", Icons.Material.Filled.Cloud),
        new("sync", "Nav.Group.Network", "Tab.Synchronization", Icons.Material.Filled.Sync),
        new("users", "Nav.Group.System", "Tab.Users", Icons.Material.Filled.People),
        new("memory", "Nav.Group.System", "Tab.Memory", Icons.Material.Filled.Memory),
        new(BackupPageId, "Nav.Group.System", "Tab.Backup", Icons.Material.Filled.Backup),
        new("about", "Nav.Group.System", "Tab.About", Icons.Material.Filled.Info),
    ];

    private string _activePage = AppearancePageId;

    // Wide window: the menu is in the flow, expanded with labels unless the user collapses it.
    private bool _navCollapsed;

    // Narrow window: the menu is an icon rail that the user opens over the page; choosing a page closes it.
    private bool _navOverlayOpen;

    private string? _pendingNavFocus;
    private readonly Dictionary<string, ElementReference> _navItemRefs = [];

    /// <summary>The pages of the menu, without Backup when the host lacks it and without Application when no app is named.</summary>
    private IReadOnlyList<SettingsPage> VisiblePages => PagesFor(
        HackerOs.AppSdk.Administration.HackSystemBackup.IsSupportedFor(AppContext),
        _appPageId is not null);

    /// <summary>The menu's pages for a host with or without backup, and with or without an app page.</summary>
    internal static IReadOnlyList<SettingsPage> PagesFor(bool backupSupported, bool hasAppPage) =>
        AllPages.Where(page => page.Id switch
        {
            BackupPageId => backupSupported,
            ApplicationPageId => hasAppPage,
            _ => true
        }).ToList();

    /// <summary>
    /// Reads <c>--page=&lt;id&gt;</c> (any page id but <c>application</c>, which <c>--app=</c> opens);
    /// the last known page wins, an unknown one is ignored.
    /// </summary>
    internal static string? ReadPageArgument(IReadOnlyList<string> arguments) =>
        arguments
            .Where(static argument => argument.StartsWith(PageArgumentPrefix, StringComparison.Ordinal))
            .Select(static argument => argument[PageArgumentPrefix.Length..].Trim().ToLowerInvariant())
            .LastOrDefault(static id => id != ApplicationPageId && AllPages.Any(page => page.Id == id));

    private void SelectPage(string pageId)
    {
        _activePage = pageId;
        _navOverlayOpen = false;
    }

    private void ToggleNavCollapsed() => _navCollapsed = !_navCollapsed;

    private void ToggleNavOverlay() => _navOverlayOpen = !_navOverlayOpen;

    private void CloseNavOverlay() => _navOverlayOpen = false;

    private string NavClass =>
        "settings-nav" + (_navCollapsed ? " is-collapsed" : string.Empty) + (_navOverlayOpen ? " is-overlay-open" : string.Empty);

    private string NavItemClass(SettingsPage page) =>
        page.Id == _activePage ? "settings-nav-item is-active" : "settings-nav-item";

    private static string NavItemDomId(SettingsPage page) => "settings-nav-" + page.Id;

    /// <summary>Arrow keys, Home and End move between the pages of the vertical tab list (WAI-ARIA tabs pattern).</summary>
    private void OnNavKeyDown(KeyboardEventArgs args)
    {
        IReadOnlyList<SettingsPage> pages = VisiblePages;
        int current = Math.Max(0, pages.ToList().FindIndex(page => page.Id == _activePage));
        int? next = args.Key switch
        {
            "ArrowDown" => (current + 1) % pages.Count,
            "ArrowUp" => (current - 1 + pages.Count) % pages.Count,
            "Home" => 0,
            "End" => pages.Count - 1,
            _ => null
        };
        if (next is { } index)
        {
            _activePage = pages[index].Id;
            _pendingNavFocus = pages[index].Id;
        }
    }

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        if (_pendingNavFocus is { } id && _navItemRefs.TryGetValue(id, out ElementReference element))
        {
            _pendingNavFocus = null;
            await element.FocusAsync();
        }
    }
}
