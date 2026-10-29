using HackerOs.AppSdk;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace HackerOs.Apps.FileExplorer;

/// <summary>The resize handle of the Places panel (ADR 0185 §1).</summary>
public sealed partial class ExplorerSidebarResizer
{
    /// <summary>How far one arrow key press moves the edge, in pixels.</summary>
    internal const int KeyboardStep = 16;

    private ElementReference _handle;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ExplorerSidebarResizer>? _callbacks;
    private int? _appliedWidth;

    /// <summary>Gets or sets the panel's current width, in pixels.</summary>
    [Parameter]
    public int Width { get; set; } = FileExplorerSettings.DefaultSidebarWidth;

    /// <summary>Gets or sets the callback told the new width at the end of a drag, a double click or a key press.</summary>
    [Parameter]
    public EventCallback<int> WidthChanged { get; set; }

    /// <summary>Gets or sets the CSS selector of the panel the handle resizes, looked up from the handle's parent.</summary>
    [Parameter, EditorRequired]
    public string PanelSelector { get; set; } = string.Empty;

    /// <summary>Gets or sets the handle's accessible name.</summary>
    [Parameter]
    public string Label { get; set; } = string.Empty;

    /// <inheritdoc />
    /// <remarks>
    /// The panel's width is set by the script (components carry no inline style): on the first render,
    /// then whenever <see cref="Width"/> changes.
    /// </remarks>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (firstRender)
            {
                _module = await JSRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/HackerOs.Apps.FileExplorer/ExplorerSidebarResizer.razor.js");
                _callbacks = DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync(
                    "attach", _handle, PanelSelector, _callbacks, FileExplorerSettings.MinSidebarWidth, FileExplorerSettings.MaxSidebarWidth);
            }

            if (_module is not null && _appliedWidth != Width)
            {
                _appliedWidth = Width;
                await _module.InvokeVoidAsync("setWidth", _handle, PanelSelector, Width);
            }
        }
        catch (JSException)
        {
            // No script: the panel keeps its CSS width.
        }
    }

    /// <summary>Called by the script at the end of a drag with the width the panel was given.</summary>
    [JSInvokable]
    public async Task OnResized(int width)
    {
        // Called from JavaScript, outside any frame: enter the app's frame first (ADR 0161 §1).
        using IDisposable? scope = AppContext is { } context ? HackExecutionContext.Enter(context) : null;
        _appliedWidth = FileExplorerSettings.ClampSidebarWidth(width);
        await CommitAsync(width);
    }

    /// <summary>Gets the width after a key press on the focused handle, or <see langword="null"/> for any other key.</summary>
    internal static int? WidthAfterKey(int width, string key) => key switch
    {
        "ArrowLeft" => FileExplorerSettings.ClampSidebarWidth(width - KeyboardStep),
        "ArrowRight" => FileExplorerSettings.ClampSidebarWidth(width + KeyboardStep),
        "Home" => FileExplorerSettings.MinSidebarWidth,
        "End" => FileExplorerSettings.MaxSidebarWidth,
        _ => null
    };

    private Task OnKeyDownAsync(KeyboardEventArgs args) =>
        WidthAfterKey(Width, args.Key) is { } width ? CommitAsync(width) : Task.CompletedTask;

    private Task ResetAsync() => CommitAsync(FileExplorerSettings.DefaultSidebarWidth);

    private Task CommitAsync(int width)
    {
        Width = FileExplorerSettings.ClampSidebarWidth(width);
        StateHasChanged();
        return WidthChanged.InvokeAsync(Width);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detach", _handle);
                await _module.DisposeAsync();
            }
            catch (Exception exception) when (exception is JSDisconnectedException or JSException or ObjectDisposedException)
            {
                // The page is going away.
            }
        }

        _callbacks?.Dispose();
    }
}
