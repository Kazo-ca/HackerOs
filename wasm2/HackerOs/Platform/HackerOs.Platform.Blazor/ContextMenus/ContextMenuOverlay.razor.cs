using HackerOs.AppSdk.Blazor.ContextMenus;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.ContextMenus;

/// <summary>How the shell draws the context menu.</summary>
public enum ContextMenuPresentation
{
    /// <summary>Floating panels at the pointer, a submenu beside its parent row (desktop).</summary>
    Popup,

    /// <summary>A full-width sheet at the bottom; a submenu replaces the level, with a Back entry (mobile).</summary>
    Sheet
}

/// <summary>
/// Draws the OS context menu (ADR 0150 §6) above every window, from <see cref="ContextMenuService"/>,
/// and connects the browser's requests to it through <c>ContextMenuOverlay.razor.js</c>. One instance
/// lives in each shell (desktop and mobile).
/// </summary>
public sealed partial class ContextMenuOverlay : ComponentBase
{
    private const string ModulePath = "./_content/HackerOs.Platform.Blazor/ContextMenus/ContextMenuOverlay.razor.js";

    private readonly ElementReference[] _panels = new ElementReference[HackMenuDefinition.MaxDepth];
    private readonly string _idPrefix = $"hcm{Guid.NewGuid():N}"[..11];
    private ElementReference _layer;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ContextMenuOverlay>? _selfReference;
    private ContextMenuSession? _session;
    private ContextMenuNavigator? _navigator;
    private ContextMenuSession? _shownSession;
    private bool _restoreFocusOnClose = true;
    private bool _disposed;

    [Inject]
    private ContextMenuService Service { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = default!;

    /// <summary>Gets or sets how the menu is drawn: floating panels, or the mobile sheet.</summary>
    [Parameter]
    public ContextMenuPresentation Presentation { get; set; } = ContextMenuPresentation.Popup;

    private bool IsSheet => Presentation == ContextMenuPresentation.Sheet;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Service.Changed += OnServiceChanged;
        Sync();
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
                await _module.InvokeVoidAsync("attachContextMenus", _selfReference, _layer);
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException)
            {
                // No browser (component tests, prerendering): menus open from code only.
                _module = null;
            }
        }

        // The session can close while this method awaits the browser: work on the one rendered.
        if (_session is { } session && _navigator is not null)
        {
            if (!ReferenceEquals(session, _shownSession))
            {
                _shownSession = session;
                _restoreFocusOnClose = true;
                await InvokeModuleAsync("menuOpened");
            }

            if (!IsSheet && ReferenceEquals(session, _session))
            {
                await InvokeModuleAsync("placeMenu", _layer, session.ClientX, session.ClientY);
            }

            if (ReferenceEquals(session, _session))
            {
                await FocusActiveAsync();
            }
        }
        else if (_shownSession is not null)
        {
            _shownSession = null;
            await InvokeModuleAsync("menuClosed", _restoreFocusOnClose);
        }
    }

    /// <summary>The script found an attached element under a right click, a long press, the Menu key or Shift+F10.</summary>
    /// <param name="token">The element's token.</param>
    /// <param name="clientX">Horizontal position.</param>
    /// <param name="clientY">Vertical position.</param>
    /// <param name="origin"><c>pointer</c>, <c>touch</c> or <c>keyboard</c>.</param>
    [JSInvokable]
    public Task<bool> OnRequestedAsync(string token, double clientX, double clientY, string origin) =>
        Service.OpenFromTokenAsync(token, clientX, clientY, origin switch
        {
            "keyboard" => ContextMenuOrigin.Keyboard,
            "touch" => ContextMenuOrigin.Touch,
            _ => ContextMenuOrigin.Pointer,
        });

    /// <summary>An outside press, a scroll, a resize or the loss of focus: the menu closes, focus stays where it went.</summary>
    [JSInvokable]
    public void OnDismissed()
    {
        _restoreFocusOnClose = false;
        Service.Close();
    }

    /// <summary>Elements carrying these tokens left the page: their attached objects are forgotten.</summary>
    /// <param name="tokens">The tokens.</param>
    [JSInvokable]
    public void OnTokensReleased(string[] tokens) => Service.Release(tokens ?? []);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Service.Changed -= OnServiceChanged;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detachContextMenus");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _selfReference?.Dispose();
    }

    private string RowId(int level, int row) => $"{_idPrefix}-{level}-{row}";

    private string? ActiveDescendant(int level) =>
        _navigator is { } navigator && level < navigator.Levels.Count && navigator.Levels[level].Highlight >= 0
            ? RowId(level, navigator.Levels[level].Highlight)
            : null;

    private string MenuLabel(ContextMenuSession session) =>
        string.IsNullOrWhiteSpace(session.Label) ? L["ContextMenu.Label"] : session.Label;

    private void OnServiceChanged()
    {
        if (!_disposed)
        {
            _ = InvokeAsync(() =>
            {
                Sync();
                StateHasChanged();
            });
        }
    }

    /// <summary>Follows the service: a new session starts a new navigator.</summary>
    private void Sync()
    {
        ContextMenuSession? current = Service.Current;
        if (ReferenceEquals(current, _session))
        {
            return;
        }

        _session = current;
        _navigator = current is null
            ? null
            : new ContextMenuNavigator(current.Root, highlightFirst: current.Origin == ContextMenuOrigin.Keyboard);
    }

    private void Hover(int level, int row)
    {
        // The sheet has no hover: a finger taps.
        if (!IsSheet && _navigator is { } navigator)
        {
            navigator.Hover(level, row);

            // ADR 0179 §6: a disabled entry (the window's own screen) still names its screen.
            Service.SetHighlighted(level < navigator.Levels.Count && row < navigator.Levels[level].Level.Rows.Count
                ? navigator.Levels[level].Level.Rows[row].Item
                : null);
        }
    }

    private void Unhover(int level, int row)
    {
        if (!IsSheet && _navigator is { } navigator && level < navigator.Levels.Count
            && row < navigator.Levels[level].Level.Rows.Count
            && ReferenceEquals(Service.Highlighted, navigator.Levels[level].Level.Rows[row].Item))
        {
            Service.SetHighlighted(null);
        }
    }

    private Task ClickAsync(int level, int row) =>
        _navigator is null ? Task.CompletedTask : ApplyAsync(_navigator.Click(level, row));

    private async Task HandleKeyAsync(KeyboardEventArgs args)
    {
        if (_navigator is null)
        {
            return;
        }

        ContextMenuNavigation navigation = _navigator.HandleKey(args.Key);
        PublishKeyboardHighlight();
        await ApplyAsync(navigation);
    }

    private void Back()
    {
        _navigator?.Back();
        PublishKeyboardHighlight();
    }

    /// <summary>The entry the keyboard highlights in the deepest open level, for the screen veil (ADR 0179 §6).</summary>
    private void PublishKeyboardHighlight()
    {
        if (_navigator is { } navigator && navigator.Active is { Highlight: >= 0 } active
            && active.Highlight < active.Level.Rows.Count)
        {
            Service.SetHighlighted(active.Level.Rows[active.Highlight].Item);
        }
        else
        {
            Service.SetHighlighted(null);
        }
    }

    private Task DismissAsync()
    {
        _restoreFocusOnClose = false;
        Service.Close();
        return Task.CompletedTask;
    }

    private async Task ApplyAsync(ContextMenuNavigation navigation)
    {
        switch (navigation)
        {
            case ContextMenuNavigation.Close:
                Service.Close();
                break;
            case ContextMenuNavigation.Invoke when _navigator?.Chosen is { } chosen:
                // Focus goes back to the element first, so an action that opens a dialog or an
                // inline editor keeps the focus it takes.
                _shownSession = null;
                await InvokeModuleAsync("menuClosed", true);
                await Service.InvokeAsync(chosen);
                break;
        }
    }

    private async Task FocusActiveAsync()
    {
        if (_navigator is null)
        {
            return;
        }

        int active = _navigator.Levels.Count - 1;
        try
        {
            await _panels[active].FocusAsync(preventScroll: true);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // Not rendered in a browser.
        }
    }

    private async Task InvokeModuleAsync(string identifier, params object?[] arguments)
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync(identifier, arguments);
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
