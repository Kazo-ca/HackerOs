using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.AppSdk.Blazor.Display;

/// <summary>
/// Renders <see cref="ChildContent"/> into a detached display surface (ADR 0078 §4): a portal to a
/// window of the host browser. The content stays an ordinary part of this component tree (same
/// cascading values, same app context, same event handlers); only its DOM lives in the surface.
/// </summary>
/// <remarks>
/// Without a surface (<see cref="Surface"/> is <see langword="null"/>) the content simply renders
/// here: an owner that always renders its content through this component keeps the content's
/// components, and their state, when it detaches and reattaches (ADR 0084 §7).
/// Changing <see cref="Surface"/> moves the content to the new surface. Removing the component
/// gives the content back before the renderer deletes it. When the surface ends (closed by the
/// user, lost), the content is moved back here, under the anchor: the owner normally reacts to
/// <see cref="IDisplaySurface.Changed"/> by removing this component.
/// </remarks>
public sealed partial class DisplaySurfaceContent : IAsyncDisposable
{
    private ElementReference _content;
    private IDisplaySurface? _mounted;

    /// <summary>The surface that shows the content, or <see langword="null"/> to show it here.</summary>
    [Parameter]
    public IDisplaySurface? Surface { get; set; }

    /// <summary>The content to show in the surface.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (ReferenceEquals(_mounted, Surface))
        {
            return;
        }

        if (_mounted is not null)
        {
            await _mounted.UnmountAsync(_content);
        }

        _mounted = Surface;
        if (Surface is { State: DisplaySurfaceState.Open })
        {
            await Surface.MountAsync(_content);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_mounted is null)
        {
            return;
        }

        try
        {
            await _mounted.UnmountAsync(_content);
        }
        catch (JSDisconnectedException)
        {
            // The page is gone with its surfaces: nothing to give back.
        }

        _mounted = null;
    }
}
