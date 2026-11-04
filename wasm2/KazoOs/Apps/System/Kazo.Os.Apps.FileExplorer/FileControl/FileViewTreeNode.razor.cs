using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Apps.FileExplorer.FileControl.Icons;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// Code-behind for one recursive Tree node (<c>FV-004</c>): expansion/lazy-load plus the same select/
/// activate/rename/context-menu handlers <see cref="FileViewDetails"/> and <see cref="FileViewIcons"/> use.
/// </summary>
public partial class FileViewTreeNode
{
    private string? _renameBuffer;

    /// <summary>The owning <see cref="FileView"/>, forwarded unchanged to every recursive child node.</summary>
    [Parameter, EditorRequired]
    public FileView Owner { get; set; } = null!;

    /// <summary>
    /// The <see cref="FileViewTree"/> at the root of this recursion, forwarded unchanged to every
    /// recursive child node — needed for <c>FV-011</c>'s ArrowUp/ArrowDown, since only the root knows the
    /// full flattened visible order (<see cref="FileViewTree.GetVisibleNodesInOrder"/>).
    /// </summary>
    [Parameter, EditorRequired]
    public FileViewTree Root { get; set; } = null!;

    /// <summary>The node this instance renders.</summary>
    [Parameter, EditorRequired]
    public FileViewTreeNodeState Node { get; set; } = null!;

    /// <summary>1-based tree depth, used for <c>aria-level</c> and child indentation.</summary>
    [Parameter]
    public int Depth { get; set; } = 1;

    // ARIA states are strings: Blazor writes a true bool attribute as aria-expanded="" (invalid) and drops a
    // false one, so the value is spelled out; a file (not a directory) has no aria-expanded at all.
    private string? AriaExpanded => Node.Item.IsDirectory ? (Node.IsExpanded ? "true" : "false") : null;

    /// <summary>
    /// Roving tabindex (<c>FV-011</c>): only the active node — the selection, or the first root node when
    /// nothing is selected yet — is ever a Tab stop, so Tab moves past the whole tree in one step instead
    /// of stopping at every node. Arrow keys move the active node within the tree.
    /// </summary>
    internal bool IsTabStop() =>
        Node.Item.IsSelected || (Owner.SelectedItem is null && ReferenceEquals(Node, Root.RootNodes.FirstOrDefault()));

    internal async Task ToggleExpandAsync()
    {
        if (!Node.Item.IsDirectory)
        {
            return;
        }

        if (Node.IsExpanded)
        {
            Node.IsExpanded = false;
            return;
        }

        if (Node.Children is null)
        {
            Node.IsLoading = true;
            StateHasChanged();
            FileSystemResult<FileSystemDirectorySnapshot> result = await Owner.FileSystem.EnumerateAsync(
                new FileSystemEnumerateRequest(Node.Item.FullPath));
            Node.IsLoading = false;
            if (!result.Succeeded)
            {
                return;
            }

            Node.Children = [.. result.Value!.Entries.Select(entry => new FileViewTreeNodeState
            {
                Item = new FileViewItem(
                    CombineChild(Node.Item.FullPath, entry.Name.Value),
                    entry.Name.Value,
                    entry.Metadata.Kind == FileSystemEntryKind.Directory,
                    entry.Metadata)
                {
                    Owner = Owner
                }
            })];
        }

        Node.IsExpanded = true;
    }

    private static VirtualPath CombineChild(VirtualPath parent, string name) =>
        VirtualPath.Parse(parent.Value == "/" ? $"/{name}" : $"{parent.Value}/{name}");

    internal void OnRowClick(MouseEventArgs args) => Node.Item.Select(additive: args.CtrlKey);

    internal Task OnRowDoubleClickAsync() => Owner.ActivateItemAsync(Node.Item);

    internal Task OnRowContextMenuAsync(MouseEventArgs args) => Owner.OnItemContextMenuAsync(args, Node.Item);

    internal Task OnRowKeyDownAsync(KeyboardEventArgs args) => args.Key switch
    {
        "Enter" => Owner.ActivateItemAsync(Node.Item),
        "F2" => RenameAsync(),
        "Delete" => DeleteAsync(args.ShiftKey),
        "ArrowRight" => ExpandKeyAsync(),
        "ArrowLeft" => CollapseKey(),
        "ArrowDown" => MoveActiveAsync(1),
        "ArrowUp" => MoveActiveAsync(-1),
        _ => Task.CompletedTask
    };

    private Task ExpandKeyAsync() =>
        Node.Item.IsDirectory && !Node.IsExpanded ? ToggleExpandAsync() : Task.CompletedTask;

    private Task MoveActiveAsync(int direction)
    {
        IReadOnlyList<FileViewTreeNodeState> visible = Root.GetVisibleNodesInOrder();
        int index = IndexOf(visible, Node) + direction;
        return index >= 0 && index < visible.Count ? Owner.MoveActiveItemAsync(visible[index].Item) : Task.CompletedTask;
    }

    private static int IndexOf(IReadOnlyList<FileViewTreeNodeState> nodes, FileViewTreeNodeState target)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (ReferenceEquals(nodes[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    internal Task OnRowDragStartAsync(DragEventArgs args) => Owner.OnItemDragStartAsync(Node.Item, args);

    internal Task OnRowDropAsync(DragEventArgs args) => Owner.OnItemDropAsync(Node.Item, args);

    private Task CollapseKey()
    {
        if (Node.IsExpanded)
        {
            Node.IsExpanded = false;
        }

        return Task.CompletedTask;
    }

    private Task RenameAsync()
    {
        Node.Item.Select();
        Node.Item.Rename();
        return Task.CompletedTask;
    }

    private Task DeleteAsync(bool permanent)
    {
        Node.Item.Select();
        return Owner.DeleteSelectedAsync(permanent);
    }

    private string GetRenameBuffer() => _renameBuffer ??= Node.Item.FileName;

    private void SetRenameBuffer(string value) => _renameBuffer = value;

    private Task OnRenameKeyDownAsync(KeyboardEventArgs args) => args.Key switch
    {
        "Enter" => CommitRenameAsync(),
        "Escape" => CancelRenameAsync(),
        _ => Task.CompletedTask
    };

    private Task CommitRenameAsync()
    {
        string value = _renameBuffer ?? Node.Item.FileName;
        _renameBuffer = null;
        return Owner.CommitRenameAsync(Node.Item, value);
    }

    private Task CancelRenameAsync()
    {
        _renameBuffer = null;
        Owner.CancelRename(Node.Item);
        return Task.CompletedTask;
    }

    private ShellIconDescriptor ResolveIcon(FileViewItem item)
    {
        if (item.IconOverride is not null)
        {
            return item.IconOverride;
        }

        string? extension = GetExtension(item.FileName);
        string? mediaType = item.Metadata is FileMetadata file ? file.MediaType : null;
        return Owner.EffectiveIconProvider.Resolve(new FileViewIconRequest(item.FullPath, item.IsDirectory, extension, mediaType));
    }

    private static string? GetExtension(string fileName)
    {
        int lastDot = fileName.LastIndexOf('.');
        return lastDot > 0 ? fileName[lastDot..].ToLowerInvariant() : null;
    }
}
