using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace HackerOs.Windowing.Blazor;

/// <summary>
/// Renders one window here, then moves its element into <see cref="Target"/>: the window layer of
/// another screen (ADR 0178 §3). The anchor stays behind, so the parent's keyed list of windows is
/// never disturbed, and the window's components are never recreated when it changes screen.
/// </summary>
/// <remarks>
/// The script keeps the element by its own key: once moved, the element may live in another
/// document, or in a layer that was just removed, and still comes back under its anchor.
/// </remarks>
public sealed class WindowPortal : ComponentBase, IAsyncDisposable
{
    private readonly string _key = Guid.NewGuid().ToString("N");
    private ElementReference _content;
    private IJSObjectReference? _module;
    private string? _placedLayerId;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    /// <summary>The layer that shows the window, or <see langword="null"/> to show it here.</summary>
    [Parameter]
    public ElementReference? Target { get; set; }

    /// <summary>The window.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "window-portal-anchor");
        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "class", "window-portal");
        builder.AddElementReferenceCapture(4, element => _content = element);
        builder.AddContent(5, ChildContent);
        builder.CloseElement();
        builder.CloseElement();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (firstRender)
            {
                _module = await JSRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    "./_content/HackerOs.Windowing.Blazor/WindowPortal.js");
                await _module.InvokeVoidAsync("register", _key, _content);
            }

            string? layerId = Target?.Id;
            if (_module is null || layerId == _placedLayerId)
            {
                return;
            }

            await _module.InvokeVoidAsync("place", _key, Target);
            _placedLayerId = layerId;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[WindowPortal] Failed to move the window: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("release", _key);
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The page is gone with its windows.
        }
    }
}
