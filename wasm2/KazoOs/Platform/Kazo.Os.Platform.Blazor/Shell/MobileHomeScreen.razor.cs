using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Globalization;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Shell;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>Persistent mobile launcher: paged Home, pinned dock and a searchable application drawer.</summary>
public partial class MobileHomeScreen
{
    /// <summary>Whether the shell's application drawer is currently open.</summary>
    [Parameter] public bool IsDrawerOpen { get; set; }
    /// <summary>Requests a drawer transition owned by the shell, so Back/Home can dismiss it.</summary>
    [Parameter] public EventCallback<bool> OnDrawerChanged { get; set; }
    /// <summary>Requests an authorized app launch from the owning mobile shell.</summary>
    [Parameter] public EventCallback<AppManifest> OnAppSelected { get; set; }

    [Inject] private AppCatalog Catalog { get; set; } = null!;
    [Inject] private IAppEnablementRegistry Enablement { get; set; } = null!;
    [Inject] private SessionLocaleService Locale { get; set; } = null!;
    [Inject] private IOsSettingsService Settings { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private IStringLocalizer<Strings> L { get; set; } = null!;
    private MobileHomeLayoutStore _store = null!;
    private MobileHomeLayout Layout => _store.Current;
    private AppManifest[] _apps = [];
    private Dictionary<string, AppManifest> _appsById = new(StringComparer.Ordinal);
    private ElementReference _root;
    private IJSObjectReference? _module;
    private DotNetObjectReference<MobileHomeScreen>? _reference;
    private CancellationTokenSource? _indicatorDelay;
    private bool _disposed;
    private bool _drawerWasOpen;
    private bool _editing;
    private bool _saving;
    private bool _mouseHelpers = true;
    private bool _indicatorsVisible = true;
    private int _page;
    private string _search = string.Empty;
    private string? _message;
    private string? _error;
    private string? _draggedId;
    private IEnumerable<AppManifest> FilteredApps => _apps.Where(app => string.IsNullOrWhiteSpace(_search)
        || Name(app).Contains(_search.Trim(), StringComparison.CurrentCultureIgnoreCase));
    private string Name(AppManifest app) => Catalog.Localize(app, Locale.Culture).Name;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _store = new MobileHomeLayoutStore(Settings);
        Enablement.Changed += CatalogChanged;
        Settings.DocumentChanged += SettingsChanged;
        RefreshApps();
        try
        {
            await _store.InitializeAsync();
            await ReadPreferencesAsync();
            await Settings.WatchAsync(MobileSettingsDocuments.Document);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _error = L["MobileHome.StorageError"];
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Kazo.Os.Platform.Blazor/mobileGestures.js");
            _reference = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("attachMobileGestures", _root, _reference,
                "data-home-gesture", nameof(ReportHomeGestureAsync), nameof(ReportHomeLongPress));
        }
        if (IsDrawerOpen != _drawerWasOpen && _module is not null)
            await _module.InvokeVoidAsync("setMobileDrawerOpen", _root, IsDrawerOpen);
        _drawerWasOpen = IsDrawerOpen;
    }

    private void RefreshApps()
    {
        _apps = Catalog.Manifests.Values.Where(app => app.Kind == AppKind.Window
            && app.Presentation.LaunchVisibility == AppLaunchVisibility.Visible && Enablement.IsEnabled(app.Id)
            && AppManifestPlatformSupport.Resolve(app)?.SupportedPlatforms.Contains(WellKnownAppPlatforms.Mobile) == true)
            .OrderBy(Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _appsById = _apps.ToDictionary(app => app.Id, StringComparer.Ordinal);
    }

    private void CatalogChanged(object? sender, EventArgs args) => _ = InvokeAsync(() => { RefreshApps(); StateHasChanged(); });
    private void SettingsChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(MobileSettingsDocuments.Document.DocumentId))
            _ = InvokeAsync(async () => { if (!_disposed) { await ReadPreferencesAsync(); StateHasChanged(); } });
    }
    private async Task ReadPreferencesAsync() => _mouseHelpers =
        (await Settings.ReadAsync(MobileSettingsDocuments.Document)).Values[MobileSettingsDocuments.MouseHelpersKey] == "true";

    private Task OpenDrawerAsync() { _search = string.Empty; return OnDrawerChanged.InvokeAsync(true); }
    private Task CloseDrawerAsync() => OnDrawerChanged.InvokeAsync(false);
    private void ToggleEdit() => _editing = !_editing;
    private void BeginEdit(string id) { _editing = true; _message = L["MobileHome.EditHint"]; }
    private async Task LaunchAsync(AppManifest app)
    {
        if (_editing && !IsDrawerOpen) return;
        await CloseDrawerAsync();
        await OnAppSelected.InvokeAsync(app);
    }

    private void Navigate(int delta) => ShowPage(Math.Clamp(_page + delta, 0, Layout.Pages.Count - 1));
    private void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, Layout.Pages.Count - 1);
        _indicatorsVisible = true;
        _indicatorDelay?.Cancel();
        _indicatorDelay?.Dispose();
        _indicatorDelay = new CancellationTokenSource();
        _ = HideIndicatorsAsync(_indicatorDelay.Token);
    }
    private async Task HideIndicatorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1800, cancellationToken);
            if (!_disposed) await InvokeAsync(() => { _indicatorsVisible = false; StateHasChanged(); });
        }
        catch (OperationCanceledException) { }
    }

    private async Task EditAsync(Func<MobileHomeLayout, bool> edit, Func<int>? destination = null)
    {
        if (_saving) return;
        _saving = true;
        _message = null;
        _error = null;
        try
        {
            if (await _store.EditAsync(edit))
            {
                ShowPage(destination?.Invoke() ?? _page);
                _message = L["MobileHome.Saved"];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _error = L["MobileHome.StorageError"];
        }
        finally { _saving = false; }
    }

    private Task AddHomeAsync(string id)
    {
        int destination = _page;
        return EditAsync(layout =>
        {
            if (!MobileHomeLayout.ValidId(id) || layout.Contains(id)) return false;
            if (layout.Pages[destination].Count >= MobileHomeLayout.PageCapacity)
            {
                destination = layout.Pages.FindIndex(page => page.Count < MobileHomeLayout.PageCapacity);
                if (destination < 0)
                {
                    if (layout.Pages.Count >= MobileHomeLayout.MaximumPages) return false;
                    destination = layout.Pages.Count;
                    layout.Pages.Add([]);
                }
            }
            layout.Pages[destination].Add(id);
            return true;
        }, () => destination);
    }
    private Task ToggleDockAsync(string id) => EditAsync(layout =>
    {
        if (layout.Dock.Remove(id)) return true;
        if (layout.Dock.Count >= MobileHomeLayout.DockCapacity || !MobileHomeLayout.ValidId(id)) return false;
        layout.Dock.Add(id);
        return true;
    });
    private Task RemoveAsync(string id) => EditAsync(layout => layout.Pages[_page].Remove(id));
    private Task AddPageAsync() => EditAsync(layout =>
    {
        if (layout.Pages.Count >= MobileHomeLayout.MaximumPages) return false;
        layout.Pages.Add([]);
        return true;
    }, () => Layout.Pages.Count - 1);
    private Task RemovePageAsync() => EditAsync(layout =>
    {
        if (layout.Pages.Count == 1 || layout.Pages[_page].Count != 0) return false;
        layout.Pages.RemoveAt(_page);
        return true;
    });
    private Task ReorderAsync(string id, int delta) => EditAsync(layout =>
    {
        List<string> page = layout.Pages[_page];
        int from = page.IndexOf(id);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= page.Count) return false;
        (page[from], page[to]) = (page[to], page[from]);
        return true;
    });
    private Task MoveToPageAsync(string id, int direction)
    {
        int source = _page;
        int target = source + direction;
        return EditAsync(layout =>
        {
            if (target < 0 || target >= MobileHomeLayout.MaximumPages || !layout.Pages[source].Contains(id)) return false;
            if (target == layout.Pages.Count) layout.Pages.Add([]);
            if (layout.Pages[target].Count >= MobileHomeLayout.PageCapacity) return false;
            layout.Pages[source].Remove(id);
            layout.Pages[target].Add(id);
            return true;
        }, () => target);
    }
    private Task DropOnAsync(string id) => EditAsync(layout =>
    {
        List<string> page = layout.Pages[_page];
        int from = _draggedId is null ? -1 : page.IndexOf(_draggedId);
        int to = page.IndexOf(id);
        _draggedId = null;
        if (from < 0 || to < 0 || from == to) return false;
        string moved = page[from];
        page.RemoveAt(from);
        page.Insert(to, moved);
        return true;
    });

    /// <summary>Interprets one completed pointer gesture; no state or VFS writes occur per movement.</summary>
    [JSInvokable]
    public async Task ReportHomeGestureAsync(string surface, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        if (surface == "pages" && Math.Abs(dx) >= 48 && Math.Abs(dx) > Math.Abs(dy) * 1.2) Navigate(dx < 0 ? 1 : -1);
        else if (surface == "open-drawer" && dy <= -48 && Math.Abs(dy) > Math.Abs(dx)) await OpenDrawerAsync();
        else if (surface == "drawer" && dy >= 48 && Math.Abs(dy) > Math.Abs(dx)) await CloseDrawerAsync();
        await InvokeAsync(StateHasChanged);
    }
    /// <summary>Enters Home editing after a touch hold; context-click is the mouse equivalent.</summary>
    [JSInvokable]
    public void ReportHomeLongPress(string appId)
    {
        if (!_appsById.ContainsKey(appId)) return;
        BeginEdit(appId);
        StateHasChanged();
    }
    private async Task HandleKeys(KeyboardEventArgs args)
    {
        if (args.Key == "Escape") { if (IsDrawerOpen) await CloseDrawerAsync(); else _editing = false; }
        else if (!IsDrawerOpen && args.Key == "ArrowLeft") Navigate(-1);
        else if (!IsDrawerOpen && args.Key == "ArrowRight") Navigate(1);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _indicatorDelay?.Cancel();
        _indicatorDelay?.Dispose();
        Enablement.Changed -= CatalogChanged;
        Settings.DocumentChanged -= SettingsChanged;
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("detachMobileGestures", _root); await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _reference?.Dispose();
    }
}
