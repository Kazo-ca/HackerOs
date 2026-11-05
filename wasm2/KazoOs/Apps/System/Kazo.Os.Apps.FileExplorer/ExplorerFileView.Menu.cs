using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.Apps.FileExplorer.FileControl;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Apps.FileExplorer;

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
    private async Task OnMenuOpeningAsync(KazoMenuOpeningEventArgs args)
    {
        if (_view is null)
        {
            args.Cancel = true;
            return;
        }

        FileViewItem? clicked = args.Item is KazoFileViewItem snapshot && _items.TryGetValue(snapshot, out FileViewItem? item)
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
        IReadOnlyList<KazoFileViewItem> snapshots = [.. targets.Select(Snapshot)];
        // ADR 0151, ADR 0160 §7: the background of a desktop is its own scope, for the apps' entries and the host.
        FileMenuScope scope = snapshots.Count == 0 && IsDesktop ? FileMenuScope.Desktop : ExplorerFileMenu.ScopeOf(snapshots);
        IReadOnlyList<KazoAppInfo> handlers = snapshots is [{ IsDirectory: false }] && Has(KazoFileViewFeatures.OpenWith)
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
        args.Menu.FileMenu = new KazoFileMenuContext(scope, snapshots, directory.Value);

        if (ContextMenuOpening.HasDelegate)
        {
            KazoFileMenuOpeningEventArgs hostArgs = new(scope, snapshots, directory.Value, args.Menu);
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
        if (id.StartsWith(KazoFileMenuItems.OpenWithAppPrefix, StringComparison.Ordinal))
        {
            return OpenWithAppAsync(id[KazoFileMenuItems.OpenWithAppPrefix.Length..], targets[0]);
        }

        if (ArchiveWriteCommands.Contains(id) && IsInsideArchive(directory))
        {
            return RefuseInArchiveAsync(directory);
        }

        return id switch
        {
            KazoFileMenuItems.Open => OpenItemsAsync(targets),
            KazoFileMenuItems.OpenWithOther => ChooseAppForAsync(targets[0]),
            KazoFileMenuItems.Cut => PutOnClipboardAsync(targets, KazoClipboardFileOperation.Cut),
            KazoFileMenuItems.Copy => PutOnClipboardAsync(targets, KazoClipboardFileOperation.Copy),
            KazoFileMenuItems.PasteInto => PasteIntoAsync(targets[0].FullPath),
            KazoFileMenuItems.Paste => PasteIntoAsync(directory),
            KazoFileMenuItems.Rename => RenameAsync(targets[0]),
            KazoFileMenuItems.Delete => DeleteItemsAsync(targets, permanent: false),
            KazoFileMenuItems.DeletePermanently => DeleteItemsAsync(targets, permanent: true),
            KazoFileMenuItems.NewFolder => _view?.CreateFolderInteractiveAsync() ?? Task.CompletedTask,
            KazoFileMenuItems.Refresh => _view?.RefreshAsync() ?? Task.CompletedTask,
            KazoFileMenuItems.Properties => ShowPropertiesAsync(targets.Count == 1 ? targets[0].FullPath.Value : directory.Value),
            _ => Task.CompletedTask
        };
    }

    /// <summary>The apps that declare the file, the default first (ADR 0121); none when the host may not read the catalog.</summary>
    private async Task<IReadOnlyList<KazoAppInfo>> GetHandlersAsync(FileViewItem item)
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
