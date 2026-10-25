using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.Apps.FileExplorer.FileControl.ContextMenu;
using HackerOs.Apps.FileExplorer.FileControl.Events;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using HackerOs.AppSdk.Icons;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// Builds and opens one context menu: default well-known items for the given scope, then every matching
/// <see cref="IFileViewContextMenuProvider"/> in <see cref="FileView.ContextMenuProviders"/> order, then
/// <see cref="FileView.ContextMenuOpening"/> as the final host-level adjustment point. See
/// <c>doc/en/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md#context-menu-customization</c>.
/// </summary>
public partial class FileViewContextMenu
{
    private MudMenu? _menu;
    private FileViewMenuItemCollection? _currentItems;

    /// <summary>The owning <see cref="FileView"/> whose state this menu acts on.</summary>
    [Parameter, EditorRequired]
    public FileView Owner { get; set; } = null!;

    internal async Task OpenAsync(MouseEventArgs args, FileViewContextMenuScope scope, FileViewItem? item)
    {
        FileViewMenuItemCollection items = new();
        AddDefaultItems(items, scope, item);

        FileViewContextMenuContext context = new(scope, item);
        foreach (IFileViewContextMenuProvider provider in Owner.ContextMenuProviders)
        {
            if (Applies(provider, scope, item))
            {
                provider.Customize(context, items);
            }
        }

        Owner.RaiseContextMenuOpening(new FileViewContextMenuOpeningEventArgs(scope, item, items));

        _currentItems = items;
        StateHasChanged();
        if (_menu is not null)
        {
            await _menu.OpenMenuAsync(args);
        }
    }

    private Task RequestOnSelectionAsync(FileViewItem item, FileViewClipboardAction action)
    {
        if (!item.IsSelected)
        {
            item.Select();
        }

        return Owner.RequestClipboardAsync(action, Owner.CurrentDirectory);
    }

    private static bool Applies(IFileViewContextMenuProvider provider, FileViewContextMenuScope scope, FileViewItem? item) =>
        provider.Scope switch
        {
            FileViewContextMenuScope.Background => scope == FileViewContextMenuScope.Background,
            FileViewContextMenuScope.Directory => scope == FileViewContextMenuScope.Directory,
            FileViewContextMenuScope.File => scope == FileViewContextMenuScope.File,
            FileViewContextMenuScope.FileType =>
                scope is FileViewContextMenuScope.File or FileViewContextMenuScope.Directory
                && item is not null
                && provider.Matches(item),
            _ => false
        };

    private void AddDefaultItems(FileViewMenuItemCollection items, FileViewContextMenuScope scope, FileViewItem? item)
    {
        switch (scope)
        {
            case FileViewContextMenuScope.Background:
                items.Add(new FileViewMenuItem(
                    "new-folder", Owner.L["Menu.NewFolder"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "folder-plus"),
                    Owner.CreateFolderInteractiveAsync));
                if (Owner.ClipboardRequested.HasDelegate)
                {
                    items.Add(new FileViewMenuItem(
                        "paste", Owner.L["Menu.Paste"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "clipboard-paste"),
                        () => Owner.RequestClipboardAsync(FileViewClipboardAction.Paste, Owner.CurrentDirectory),
                        IsDisabled: !Owner.CanPaste));
                }

                items.Add(new FileViewMenuItem(
                    "refresh", Owner.L["Menu.Refresh"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "refresh-cw"),
                    () => Owner.RefreshAsync()));
                break;

            case FileViewContextMenuScope.Directory:
            case FileViewContextMenuScope.File:
                if (item is not null)
                {
                    items.Add(new FileViewMenuItem(
                        "open", Owner.L["Menu.Open"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "folder-open"),
                        () => Owner.ActivateItemAsync(item)));
                    if (Owner.ClipboardRequested.HasDelegate)
                    {
                        // Like a desktop file manager: a right-click on an unselected item acts on it alone.
                        items.Add(new FileViewMenuItem(
                            "cut", Owner.L["Menu.Cut"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "scissors"),
                            () => RequestOnSelectionAsync(item, FileViewClipboardAction.Cut),
                            IsSeparatorBefore: true));
                        items.Add(new FileViewMenuItem(
                            "copy", Owner.L["Menu.Copy"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "copy"),
                            () => RequestOnSelectionAsync(item, FileViewClipboardAction.Copy)));
                        if (item.IsDirectory)
                        {
                            items.Add(new FileViewMenuItem(
                                "paste-into", Owner.L["Menu.PasteInto"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "clipboard-paste"),
                                () => Owner.RequestClipboardAsync(FileViewClipboardAction.Paste, item.FullPath),
                                IsDisabled: !Owner.CanPaste));
                        }
                    }

                    items.Add(new FileViewMenuItem(
                        "rename", Owner.L["Menu.Rename"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "pencil"),
                        () =>
                        {
                            item.Select();
                            item.Rename();
                            return Task.CompletedTask;
                        }));
                    items.Add(new FileViewMenuItem(
                        "delete", Owner.L["Menu.Delete"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "trash-2"),
                        () =>
                        {
                            item.Select();
                            return Owner.DeleteSelectedAsync();
                        }));

                    // Only meaningful when the host turns the plain "Delete" into a trash move (ADR 0052).
                    if (Owner.DeleteRequested.HasDelegate)
                    {
                        items.Add(new FileViewMenuItem(
                            "delete-permanently", Owner.L["Menu.DeletePermanently"], ShellIconDescriptor.Vector(IconLibrary.Lucide, "octagon-x"),
                            () =>
                            {
                                item.Select();
                                return Owner.DeleteSelectedAsync(permanent: true);
                            }));
                    }
                }

                break;
        }
    }
}
