using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.Platform.Blazor.ContextMenus;
using HackerOs.Platform.Core.Shell;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>
/// Draws the window manager's keyboard surfaces (ADR 0095 §3) and connects the browser's key events
/// to <see cref="WindowKeyboardCommands"/> through <c>WindowManagerOverlays.razor.js</c>. The window
/// menus open in the OS context menu (ADR 0179 §3), which gives them the « Autre écran » submenu.
/// </summary>
public sealed partial class WindowManagerOverlays : ComponentBase
{
    /// <summary>Name of the window menu in the OS context menu (<c>data-context-menu</c>).</summary>
    internal const string WindowMenuName = "window-menu";

    /// <summary>Name of the Maximize/Restore button's menu.</summary>
    internal const string MaximizeMenuName = "window-maximize-menu";

    /// <summary>Id of the « Autre écran » entry.</summary>
    internal const string OtherScreenItemId = "window.other-screen";

    /// <summary>Id of the « Nouvel écran » entry.</summary>
    internal const string NewScreenItemId = "window.new-screen";

    /// <summary>Id prefix of the « Écran N » entries, followed by the number (the screen veil reads it).</summary>
    internal const string ScreenItemIdPrefix = "window.screen.";

    private const string ModulePath = "./_content/HackerOs.Platform.Blazor/Windows/WindowManagerOverlays.razor.js";

    private readonly List<IDisposable> _registrations = [];
    private IJSObjectReference? _module;
    private DotNetObjectReference<WindowManagerOverlays>? _selfReference;
    private ContextMenuScope? _menuScope;
    private WindowMenuState? _openedMenu;
    private bool _disposed;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    private ContextMenuService ContextMenus { get; set; } = default!;

    /// <summary>Gets or sets the window manager's keyboard commands.</summary>
    [Parameter, EditorRequired]
    public WindowKeyboardCommands Commands { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Commands.Changed += OnCommandsChanged;
        Commands.ToggleFullScreen ??= ToggleFullScreenAsync;
        _menuScope = ContextMenus.CreateScope(null);
        _registrations.Add(_menuScope.Register(new HackMenuRegistration(WindowMenuName, () => BuildMenu(WindowMenuKind.Window), default)));
        _registrations.Add(_menuScope.Register(new HackMenuRegistration(MaximizeMenuName, () => BuildMenu(WindowMenuKind.MaximizeButton), default)));
        ContextMenus.Changed += OnContextMenuChanged;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);
                _selfReference = DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("attachWindowKeyboard", _selfReference, BindingsForBrowser());
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException)
            {
                // No browser (component tests, prerendering): shortcuts are simply not caught.
                _module = null;
            }

            await Commands.StartAsync();
        }
    }

    /// <summary>Runs a shortcut the browser caught (an index into the bindings it was given).</summary>
    /// <param name="action">The bound action.</param>
    [JSInvokable]
    public Task OnShortcutAsync(int action) =>
        Enum.IsDefined((WindowKeyboardAction)action)
            ? Commands.ExecuteAsync((WindowKeyboardAction)action)
            : Task.CompletedTask;

    /// <summary>Handles a key the browser forwarded while the window manager holds the keyboard.</summary>
    [JSInvokable]
    public void OnGrabbedKey(string key, string code, bool control, bool alt, bool shift, bool super) =>
        Commands.HandleGrabbedKey(new WindowKeyInput(key, code, control, alt, shift, super));

    /// <summary>Alt was released after a switch shortcut.</summary>
    [JSInvokable]
    public void OnAltReleased() => Commands.HandleAltReleased();

    /// <summary>The browser entered or left full screen, with or without the keyboard lock.</summary>
    [JSInvokable]
    public void OnHostKeyboardState(bool fullScreen, bool locked) => Commands.SetHostKeyboardState(fullScreen, locked);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Commands.Changed -= OnCommandsChanged;
        ContextMenus.Changed -= OnContextMenuChanged;
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _menuScope?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detachWindowKeyboard");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _selfReference?.Dispose();
    }

    private static string ItemId(WindowId id) => $"window-switcher-{id.Value:N}";

    private static string Initial(string title) =>
        string.IsNullOrWhiteSpace(title) ? "?" : title.Trim()[..1].ToUpperInvariant();

    private object[] BindingsForBrowser() =>
    [
        .. Commands.ActiveBindings.Select(static binding => new
        {
            action = (int)binding.Action,
            ctrl = binding.Chord.Control,
            alt = binding.Chord.Alt,
            shift = binding.Chord.Shift,
            meta = binding.Chord.Super,
            key = binding.Chord.DomKey,
            code = binding.Chord.DomCode,
            holdsAlt = binding.Chord.Alt
                && binding.Action is WindowKeyboardAction.SwitchNext or WindowKeyboardAction.SwitchPrevious,
        }),
    ];

    private string GrabMode() => Commands.Mode switch
    {
        WindowKeyboardMode.Switching => "switching",
        WindowKeyboardMode.Moving => "moving",
        WindowKeyboardMode.Resizing => "resizing",
        _ => "none",
    };

    private void OnCommandsChanged()
    {
        if (_disposed)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            if (_module is not null)
            {
                try
                {
                    await _module.InvokeVoidAsync("updateWindowKeyboard", BindingsForBrowser(), GrabMode());
                }
                catch (JSDisconnectedException)
                {
                }
            }

            await SyncMenuAsync();
            StateHasChanged();
        });
    }

    /// <summary>Follows <see cref="WindowKeyboardCommands.Menu"/>: opens it in the OS context menu, or closes it there.</summary>
    private async Task SyncMenuAsync()
    {
        if (_menuScope is null)
        {
            return;
        }

        WindowMenuState? menu = Commands.Menu;
        if (menu is null)
        {
            _openedMenu = null;
            _menuScope.Close();
            return;
        }

        if (ReferenceEquals(menu, _openedMenu))
        {
            return;
        }

        _openedMenu = menu;
        bool opened = await ContextMenus.OpenAsync(
            _menuScope,
            menu.Kind == WindowMenuKind.MaximizeButton ? MaximizeMenuName : WindowMenuName,
            menu.WindowId,
            menu.X,
            menu.Y,
            menu.FromKeyboard ? ContextMenuOrigin.Keyboard : ContextMenuOrigin.Pointer);
        if (!opened && ReferenceEquals(Commands.Menu, menu))
        {
            Commands.CloseMenu();
        }
    }

    /// <summary>The OS context menu closed, or another menu replaced the window menu: the window menu is closed too.</summary>
    private void OnContextMenuChanged()
    {
        if (_disposed || _openedMenu is null || ContextMenus.Current?.Scope == _menuScope)
        {
            return;
        }

        _openedMenu = null;
        Commands.CloseMenu();
    }

    /// <summary>Builds the open window menu (ADR 0179 §3-5): its commands, then « Autre écran ▸ ».</summary>
    private HackMenuDefinition BuildMenu(WindowMenuKind kind)
    {
        HackMenuDefinition definition = new(kind == WindowMenuKind.MaximizeButton ? MaximizeMenuName : WindowMenuName)
        {
            Label = L["WindowMenu.Label"],
        };
        if (_openedMenu is not { } menu)
        {
            return definition;
        }

        WindowMenuDefinitions.AddWindowEntries(definition, Commands, L, menu.WindowId, kind, this);
        return definition;
    }

    private async Task ToggleFullScreenAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("toggleFullScreen");
        }
    }
}
