using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Clipboard;
using HackerOs.Apps.FileExplorer.FileControl;
using HackerOs.AppSdk.Files;
using HackerOs.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// The file menus of the control, drawn by the OS context menu (plan §7, ADR 0155 §4): one empty menu
/// per control, filled at each opening for what was right-clicked (<see cref="ExplorerFileMenu"/>), then
/// handed to the host's <see cref="ContextMenuOpening"/> to add, hide or disable entries.
/// </summary>
public sealed partial class ExplorerFileView
{
    /// <summary>
    /// Fills the menu the OS is about to draw. The attached object is the right-clicked item's snapshot,
    /// or <see langword="null"/> for the folder's background.
    /// </summary>
    /// <remarks>
    /// The targets follow the selection rule of every desktop: an item outside the selection becomes
    /// the selection; an item inside it acts on the whole selection. Each entry acts on the targets
    /// captured here, not on whatever is selected when it is clicked.
    /// </remarks>
    private async Task OnMenuOpeningAsync(HackMenuOpeningEventArgs args)
    {
        if (_view is null)
        {
            args.Cancel = true;
            return;
        }

        FileViewItem? clicked = args.Item is HackFileViewItem snapshot && _items.TryGetValue(snapshot, out FileViewItem? item)
            ? item
            : null;

        IReadOnlyList<FileViewItem> targets = [];
        if (clicked is not null)
        {
            if (!clicked.IsSelected)
            {
                // The view's own right-click handler may run after the OS opened the menu: select now.
                clicked.Select();
            }

            targets = _view.SelectedItems.Count > 0 ? [.. _view.SelectedItems] : [clicked];
        }

        VirtualPath directory = _view.CurrentDirectory;
        IReadOnlyList<HackFileViewItem> snapshots = [.. targets.Select(Snapshot)];
        // ADR 0151, ADR 0160 §7: the background of a desktop is its own scope, for the apps' entries and the host.
        FileMenuScope scope = snapshots.Count == 0 && IsDesktop ? FileMenuScope.Desktop : ExplorerFileMenu.ScopeOf(snapshots);
        IReadOnlyList<HackAppInfo> handlers = snapshots is [{ IsDirectory: false }] && Has(HackFileViewFeatures.OpenWith)
            ? await GetHandlersAsync(targets[0])
            : [];

        ExplorerFileMenu.Build(
            args.Menu,
            new ExplorerFileMenuState(snapshots, Features, CanPaste, handlers),
            id => EventCallback.Factory.Create<object?>(this, _ => RunMenuCommandAsync(id, targets, directory)),
            key => L[key]);

        if (IsInArchive)
        {
            HideArchiveWriteCommands(args.Menu);
        }

        // ADR 0159 §5: tells the OS what this menu is drawn for, so it adds the entries apps registered.
        args.Menu.FileMenu = new HackFileMenuContext(scope, snapshots, directory.Value);

        if (ContextMenuOpening.HasDelegate)
        {
            HackFileMenuOpeningEventArgs hostArgs = new(scope, snapshots, directory.Value, args.Menu);
            await ContextMenuOpening.InvokeAsync(hostArgs);
            args.Cancel = hostArgs.Cancel;
        }
    }

    /// <summary>
    /// Runs one entry of the file menu on the targets captured when it opened; "Paste" and the
    /// background's "Properties" act on <paramref name="directory"/>, the folder shown at that time.
    /// </summary>
    private Task RunMenuCommandAsync(string id, IReadOnlyList<FileViewItem> targets, VirtualPath directory)
    {
        if (id.StartsWith(HackFileMenuItems.OpenWithAppPrefix, StringComparison.Ordinal))
        {
            return OpenWithAppAsync(id[HackFileMenuItems.OpenWithAppPrefix.Length..], targets[0]);
        }

        if (ArchiveWriteCommands.Contains(id) && IsInsideArchive(directory))
        {
            return RefuseInArchiveAsync(directory);
        }

        return id switch
        {
            HackFileMenuItems.Open => OpenItemsAsync(targets),
            HackFileMenuItems.OpenWithOther => ChooseAppForAsync(targets[0]),
            HackFileMenuItems.Cut => PutOnClipboardAsync(targets, HackClipboardFileOperation.Cut),
            HackFileMenuItems.Copy => PutOnClipboardAsync(targets, HackClipboardFileOperation.Copy),
            HackFileMenuItems.PasteInto => PasteIntoAsync(targets[0].FullPath),
            HackFileMenuItems.Paste => PasteIntoAsync(directory),
            HackFileMenuItems.Rename => RenameAsync(targets[0]),
            HackFileMenuItems.Delete => DeleteItemsAsync(targets, permanent: false),
            HackFileMenuItems.DeletePermanently => DeleteItemsAsync(targets, permanent: true),
            HackFileMenuItems.NewFolder => _view?.CreateFolderInteractiveAsync() ?? Task.CompletedTask,
            HackFileMenuItems.Refresh => _view?.RefreshAsync() ?? Task.CompletedTask,
            HackFileMenuItems.Properties => ShowPropertiesAsync(targets.Count == 1 ? targets[0].FullPath.Value : directory.Value),
            _ => Task.CompletedTask
        };
    }

    /// <summary>The apps that declare the file, the default first (ADR 0121); none when the host may not read the catalog.</summary>
    private async Task<IReadOnlyList<HackAppInfo>> GetHandlersAsync(FileViewItem item)
    {
        try
        {
            return await HostContext.Catalog.GetHandlersAsync(item.FullPath, mediaType: null, HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            return [];
        }
    }
}
