using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.Platform.Blazor.ContextMenus;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// The OS menu of an app of the Start menu and its drag to the desktop (ADR 0160 §6): "Open", "Pin" or
/// "Unpin", "Send to ▸ Desktop", and a drag carrying <c>{"appId"}</c> that the desktop (or any folder of
/// the file control) turns into a <c>.desktop</c> shortcut.
/// </summary>
public partial class AppLauncher : IAsyncDisposable
{
    private const string AppMenuName = "hackeros.start-menu.app";

    private ContextMenuScope? _menus;
    private IDisposable? _menuRegistration;
    private IJSObjectReference? _dragModule;
    private DotNetObjectReference<AppLauncher>? _dragCallbacks;
    private ElementReference _overlayElement;

    /// <summary>Gets the services, to find the optional OS menu service.</summary>
    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    /// <summary>Gets the browser, for the drag script.</summary>
    [Inject]
    private IJSRuntime JavaScript { get; set; } = null!;

    /// <summary>
    /// Gets or sets what "Send to ▸ Desktop" runs: the desktop shell hands the app to the running
    /// desktop, which writes the shortcut. Unset (no desktop), the entry is disabled.
    /// </summary>
    [Parameter]
    public EventCallback<AppManifest> OnSendToDesktop { get; set; }

    /// <summary>Creates the launcher's menu scope: the shell's own, run with no app's rights.</summary>
    private void InitializeMenus()
    {
        if (Services.GetService(typeof(ContextMenuService)) is not ContextMenuService service)
        {
            return;
        }

        _menus = service.CreateScope(null);
        _menuRegistration = _menus.Register(new KazoMenuRegistration(
            AppMenuName,
            static () => new KazoMenuDefinition(AppMenuName),
            EventCallback.Factory.Create<KazoMenuOpeningEventArgs>(this, OnAppMenuOpening)));
    }

    /// <summary>The attributes that open the app's menu on its row (none without the OS menu).</summary>
    private IReadOnlyDictionary<string, object> AppMenuAttributes(AppManifest app)
    {
        Dictionary<string, object> attributes = _menus is null ? [] : new(_menus.Attach(AppMenuName, app.Id));
        // Read by AppLauncher.razor.js: the row can be dragged to the desktop or a folder.
        attributes["draggable"] = "true";
        attributes["data-app-drag-id"] = app.Id;
        return attributes;
    }

    private void OnAppMenuOpening(KazoMenuOpeningEventArgs args)
    {
        if (args.Item is not string appId || FindLaunchableApp(appId) is not { } app)
        {
            args.Cancel = true;
            return;
        }

        KazoMenuDefinition menu = args.Menu;
        menu.Groups.Clear();
        KazoMenuItemDefinition open = menu.AddGroup().Add("open", L["Launcher.Menu.Open"]);
        open.Icon = KazoIcons.Vector(IconLibrary.Lucide, "play");
        open.OnClick = EventCallback.Factory.Create<object?>(this, _ => LaunchAppAsync(app));

        bool pinned = IsPinned(app.Id);
        KazoMenuItemDefinition pin = menu.AddGroup().Add(pinned ? "unpin" : "pin", pinned ? L["Launcher.Unpin"] : L["Launcher.Pin"]);
        pin.Icon = KazoIcons.Vector(IconLibrary.Lucide, pinned ? "pin-off" : "pin");
        pin.IsDisabled = !CanManagePins;
        pin.OnClick = EventCallback.Factory.Create<object?>(this, _ => TogglePinAsync(app.Id));

        KazoMenuItemDefinition sendTo = menu.AddGroup().Add("send-to", L["Launcher.Menu.SendTo"]);
        sendTo.Icon = KazoIcons.Vector(IconLibrary.Lucide, "send");
        KazoMenuGroupDefinition targets = new();
        KazoMenuItemDefinition desktop = targets.Add("send-to-desktop", L["Launcher.Menu.Desktop"]);
        desktop.Icon = KazoIcons.Vector(IconLibrary.Lucide, "monitor");
        desktop.IsDisabled = !OnSendToDesktop.HasDelegate;
        desktop.OnClick = EventCallback.Factory.Create<object?>(this, _ => OnSendToDesktop.InvokeAsync(app));
        sendTo.Children.Add(targets);
    }

    /// <summary>Installs the drag of the app rows (the payload must be written during <c>dragstart</c>, in the browser).</summary>
    private async Task AttachAppDragAsync()
    {
        try
        {
            _dragModule = await JavaScript.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Kazo.Os.Platform.Blazor/Shell/AppLauncher.razor.js");
            _dragCallbacks = DotNetObjectReference.Create(this);
            await _dragModule.InvokeVoidAsync("attachAppDrag", _overlayElement, _dragCallbacks);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or NotSupportedException)
        {
            // A renderer without a browser (a test): the rows are not draggable.
        }
    }

    /// <summary>A row was dropped somewhere (or the drag cancelled): the menu closes, as Windows' does.</summary>
    [JSInvokable]
    public Task OnAppDragEnded(bool dropped) => dropped ? InvokeAsync(CloseAsync) : Task.CompletedTask;

    /// <inheritdoc />
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        _menuRegistration?.Dispose();
        _menus?.Dispose();
        _dragCallbacks?.Dispose();
        if (_dragModule is not null)
        {
            try
            {
                await _dragModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going away.
            }
        }
    }
}
