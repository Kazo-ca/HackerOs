using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.App.Abstractions;
using HackerOs.Apps.FileExplorer.FileControl.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// Wraps host content to accept drops of Kazo OS entries and host files (ADR 0058, section 1),
/// through the owner <see cref="FileView"/>'s drop pipeline (same payload, same checks).
/// </summary>
public partial class FileViewDropZone
{
    private ElementReference _element;
    private bool _attached;

    /// <summary>The view whose drop pipeline reads the payload. Required.</summary>
    [Parameter, EditorRequired]
    public FileView Owner { get; set; } = null!;

    /// <summary>
    /// Target directory. When set and <see cref="Dropped"/> is not, the drop is handled as a drop on
    /// that folder (the owner's <see cref="FileView.DropRequested"/>, or its own move/copy).
    /// </summary>
    [Parameter]
    public VirtualPath? Destination { get; set; }

    /// <summary>Receives the drop instead, for a target that is not a directory (the Trash).</summary>
    [Parameter]
    public EventCallback<FileViewDropRequestedEventArgs> Dropped { get; set; }

    /// <summary>Extra CSS classes of the wrapper.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>Wrapped content.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_attached && Owner.AllowDragDrop)
        {
            _attached = true;
            await Owner.AttachDropZoneAsync(_element);
        }
    }

    private async Task DropAsync(DragEventArgs args)
    {
        if (!Dropped.HasDelegate)
        {
            if (Destination is { } destination)
            {
                await Owner.DropOntoAsync(destination, args);
            }

            return;
        }

        FileViewDropRequestedEventArgs? drop = await Owner.ReadDropAsync(Destination, args);
        if (drop is null)
        {
            return;
        }

        try
        {
            await Dropped.InvokeAsync(drop);
        }
        finally
        {
            await Owner.ReleaseHostFilesAsync(drop);
        }
    }
}
