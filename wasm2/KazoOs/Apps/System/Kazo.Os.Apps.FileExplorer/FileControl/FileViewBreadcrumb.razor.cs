using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Apps.FileExplorer.FileControl.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// Clickable path of a <see cref="FileView"/>'s current directory, each segment a navigation button
/// and a drop target (ADR 0058, section 1). Place it anywhere in the host's chrome, bound to the view.
/// </summary>
public partial class FileViewBreadcrumb
{
    private ElementReference _element;
    private FileView? _subscribedTo;
    private bool _attached;

    /// <summary>The view whose directory is shown and navigated. Required.</summary>
    [Parameter, EditorRequired]
    public FileView Owner { get; set; } = null!;

    /// <summary>Label of the root segment. Defaults to <c>/</c>.</summary>
    [Parameter]
    public string RootLabel { get; set; } = "/";

    /// <summary>
    /// Gets the segments from the root down to the current directory: <c>/home/benoit</c> gives
    /// <c>/</c>, <c>home</c>, <c>benoit</c>.
    /// </summary>
    internal IReadOnlyList<(string Label, VirtualPath Path)> Segments => Split(Owner.CurrentDirectory, RootLabel);

    /// <summary>Splits a path into breadcrumb segments.</summary>
    internal static IReadOnlyList<(string Label, VirtualPath Path)> Split(VirtualPath directory, string rootLabel)
    {
        List<(string, VirtualPath)> segments = [(rootLabel, VirtualPath.Parse("/"))];
        string value = directory.Value ?? "/";
        string current = string.Empty;
        foreach (string part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            segments.Add((part, VirtualPath.Parse(current)));
        }

        return segments;
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_subscribedTo, Owner))
        {
            if (_subscribedTo is not null)
            {
                _subscribedTo.OnPathChange -= OnPathChanged;
            }

            _subscribedTo = Owner;
            Owner.OnPathChange += OnPathChanged;
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_attached && Owner.AllowDragDrop)
        {
            _attached = true;
            await Owner.AttachDropZoneAsync(_element);
        }
    }

    private void OnPathChanged(object? sender, FileViewPathChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);

    private async Task NavigateAsync(VirtualPath path)
    {
        if (!Equals(path, Owner.CurrentDirectory))
        {
            await Owner.NavigateAsync(path);
        }
    }

    private Task DropAsync(VirtualPath path, DragEventArgs args) => Owner.DropOntoAsync(path, args);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribedTo is not null)
        {
            _subscribedTo.OnPathChange -= OnPathChanged;
        }
    }
}
