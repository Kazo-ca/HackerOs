using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.Apps.FileExplorer.FileControl;
using Kazo.Os.Apps.FileExplorer.FileControl.Events;
using Kazo.Os.Apps.FileExplorer.FileControl.Icons;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Events;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The desktop mode of the control and its <c>.desktop</c> shortcuts (ADR 0152, ADR 0160 §2, §5-6): the
/// Home and Trash icons with their menus and drops, the positions handed to the host, and the shortcuts
/// shown with their app's icon in every mode.
/// </summary>
public sealed partial class ExplorerFileView
{
    /// <summary>The explorer's own id: opening the Trash icon starts it on <c>trash:///</c>.</summary>
    private const string FileExplorerAppId = "org.kazoos.file-explorer";

    private readonly string _systemMenuName = "kazoos.file-view.desktop." + Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, ShortcutState> _shortcuts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _pendingShortcuts = new(StringComparer.Ordinal);
    private FileViewDesktopBinding? _desktop;
    private IDisposable? _systemMenu;
    private IReadOnlyDictionary<string, KazoAppInfo>? _apps;
    private bool _loadingShortcuts;
    private bool _trashWatched;
    private CancellationTokenSource? _trashWatchCts;
    private ITopicChannelSubscription<FileSystemChangeEvent>? _trashWatch;

    /// <summary>Gets or sets the <c>[view]</c> options of the desktop mode.</summary>
    [Parameter]
    public KazoDesktopOptions? DesktopOptions { get; set; }

    /// <summary>Gets or sets the saved cell of each desktop icon.</summary>
    [Parameter]
    public IReadOnlyDictionary<string, KazoDesktopCell>? DesktopPositions { get; set; }

    /// <summary>Gets or sets the callback receiving the positions to save.</summary>
    [Parameter]
    public EventCallback<IReadOnlyDictionary<string, KazoDesktopCell>> DesktopPositionsChanged { get; set; }

    private bool IsDesktop => Mode == KazoFileViewMode.Desktop;

    /// <summary>Wires the shortcut decoration and, on a desktop, the binding of the grid (on each parameter change).</summary>
    private void ConfigureDesktop()
    {
        _binding.Decorate = DecorateShortcut;
        if (!IsDesktop)
        {
            _binding.Desktop = null;
            return;
        }

        if (_desktop is null)
        {
            _desktop = new FileViewDesktopBinding
            {
                SavePositions = positions => DesktopPositionsChanged.InvokeAsync(positions),
                AttachSystemMenu = key => Menus.Attach(_systemMenuName, key),
                OpenSystemItem = OpenSystemItemAsync,
                DropOnSystemItem = DropOnSystemItemAsync
            };
            _systemMenu = Menus.Define(_systemMenuName, static _ => { }, OnSystemMenuOpeningAsync);
        }

        _desktop.Options = DesktopOptions ?? KazoDesktopOptions.Default;
        _desktop.Positions = DesktopPositions ?? new Dictionary<string, KazoDesktopCell>();
        _desktop.HomeLabel = L["Desktop.Home"];
        _desktop.TrashLabel = L["Desktop.Trash"];
        _desktop.GridLabel = L["Desktop.Grid"];
        _binding.Desktop = _desktop;
    }

    /// <summary>On a desktop, reads whether the trash holds something and follows it.</summary>
    private async Task StartDesktopAsync()
    {
        if (!IsDesktop || _trashWatched)
        {
            return;
        }

        _trashWatched = true;
        using IDisposable scope = KazoExecutionContext.Enter(HostContext);
        await RefreshTrashStateAsync();
        try
        {
            string files = KazoTrash.TrashDirectory + "/files";
            _trashWatch = await HostContext.Watch.WatchAsync(VirtualPath.Parse(files), FileSystemWatchScope.ImmediateChildren, HostContext.CancellationToken);
            _trashWatchCts = new CancellationTokenSource();
            _ = WatchTrashAsync(_trashWatch, _trashWatchCts.Token);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // No watch (the trash does not exist yet, or a host without one): the icon follows this
            // control's own deletions only.
        }
    }

    private async Task WatchTrashAsync(ITopicChannelSubscription<FileSystemChangeEvent> subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TopicMessage<FileSystemChangeEvent> _ in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    using IDisposable scope = KazoExecutionContext.Enter(HostContext);
                    await RefreshTrashStateAsync();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The control is gone.
        }
    }

    private async Task StopDesktopAsync()
    {
        _systemMenu?.Dispose();
        _trashWatchCts?.Cancel();
        _trashWatchCts?.Dispose();
        _trashWatchCts = null;
        if (_trashWatch is not null)
        {
            await _trashWatch.DisposeAsync();
            _trashWatch = null;
        }
    }

    /// <summary>Draws the Trash icon full or empty (ADR 0160 §5).</summary>
    private async Task RefreshTrashStateAsync()
    {
        if (_desktop is null)
        {
            return;
        }

        bool full;
        try
        {
            full = (await KazoTrash.GetItemsAsync(HostContext.CancellationToken)).Count > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AppGatewayAccessDeniedException or NotSupportedException)
        {
            return;
        }

        if (full != _desktop.IsTrashFull)
        {
            _desktop.IsTrashFull = full;
            StateHasChanged();
        }
    }

    private string HomeDirectory => Kazo.Os.AppSdk.KazoUser.Describe(HostContext).HomeDirectory;

    // ---- Home and Trash ------------------------------------------------------------------------------

    /// <summary>Opens Home in a new explorer window, the Trash in the explorer's trash view.</summary>
    private async Task OpenSystemItemAsync(string key)
    {
        try
        {
            if (key == KazoDesktopItems.Home)
            {
                await HostContext.Intents.OpenFileAsync(VirtualPath.Parse(HomeDirectory), "inode/directory", HostContext.CancellationToken);
            }
            else if (key == KazoDesktopItems.Trash)
            {
                await HostContext.Intents.LaunchAsync(FileExplorerAppId, [FileExplorerWindow.TrashArgument], HostContext.CancellationToken);
            }
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            await ReportErrorAsync(L["Error.LaunchDenied"]);
        }
    }

    /// <summary>A drop on Home moves (or copies) into the home folder; a drop on the Trash trashes the entries.</summary>
    private async Task DropOnSystemItemAsync(string key, FileViewDropRequestedEventArgs drop)
    {
        if (key == KazoDesktopItems.Home)
        {
            await TransferDropAsync(drop, VirtualPath.Parse(HomeDirectory));
            return;
        }

        if (key != KazoDesktopItems.Trash || drop.Entries.Count == 0 || !Has(KazoFileViewFeatures.Delete))
        {
            return;
        }

        List<string> failures = [];
        async Task TrashAllAsync()
        {
            foreach (FileViewDraggedEntry entry in drop.Entries)
            {
                try
                {
                    await KazoTrash.MoveToTrashAsync(entry.Path.Value, HostContext.CancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add(L["Error.TrashMove", entry.Path.Value, exception.Message]);
                }
            }
        }

        await TrackOperation(TrashAllAsync());
        _view?.ClearSelect();
        await RefreshAfterChangeAsync();
        await RefreshTrashStateAsync();
        if (failures.Count > 0)
        {
            await ReportErrorAsync(string.Join("; ", failures));
        }
    }

    /// <summary>The menu of the Home or Trash icon (ADR 0160 §5): no file entries, no app entries.</summary>
    private async Task OnSystemMenuOpeningAsync(KazoMenuOpeningEventArgs args)
    {
        if (args.Item is not string key || !KazoDesktopItems.IsSystemItem(key))
        {
            args.Cancel = true;
            return;
        }

        if (key == KazoDesktopItems.Trash)
        {
            await RefreshTrashStateAsync();
        }

        args.Menu.Groups.Clear();
        KazoMenuGroupDefinition main = args.Menu.AddGroup();
        SystemEntry(main, KazoFileMenuItems.Open, L["Menu.Open"], "folder-open", () => OpenSystemItemAsync(key));
        if (key == KazoDesktopItems.Trash)
        {
            KazoMenuItemDefinition empty = SystemEntry(main, KazoFileMenuItems.EmptyTrash, L["Menu.EmptyTrash"], "trash-2", EmptyTrashAsync);
            empty.IsDisabled = _desktop is not { IsTrashFull: true };
        }

        if (Has(KazoFileViewFeatures.Properties))
        {
            SystemEntry(args.Menu.AddGroup(), KazoFileMenuItems.Properties, L["Menu.Properties"], "info",
                () => ShowPropertiesAsync(key == KazoDesktopItems.Home ? HomeDirectory : KazoTrash.TrashDirectory));
        }
    }

    private KazoMenuItemDefinition SystemEntry(KazoMenuGroupDefinition group, string id, string title, string icon, Func<Task> run)
    {
        KazoMenuItemDefinition entry = group.Add(id, title);
        entry.Icon = KazoIcons.Vector(IconLibrary.Lucide, icon);
        entry.OnClick = EventCallback.Factory.Create<object?>(this, _ => run());
        return entry;
    }

    /// <summary>"Empty Trash", after a confirmation: everything in it is deleted for good.</summary>
    private async Task EmptyTrashAsync()
    {
        if (Dialogs is not { } dialogs)
        {
            await ReportErrorAsync(L["Error.DialogsUnavailable"]);
            return;
        }

        MessageBoxDialogResult answer = await dialogs.MessageBoxAsync(
            HostContext,
            new MessageBoxDialogRequest { Title = L["Dialog.EmptyTrash.Title"], Content = L["Dialog.EmptyTrash.Content"], DialogType = MessageBoxType.YesNo },
            HostContext.CancellationToken);
        if (answer.Result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await TrackOperation(KazoTrash.EmptyAsync(HostContext.CancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ReportErrorAsync(L["Error.EmptyTrash", exception.Message]);
        }

        await RefreshTrashStateAsync();
    }

    // ---- Shortcuts (.desktop) ------------------------------------------------------------------------

    /// <summary>
    /// Shows a <c>.desktop</c> shortcut with its name and its app's icon, greyed when the app is not
    /// installed (ADR 0152 §5). The file is read once per revision, after the listing: the item is drawn
    /// as a plain file until then.
    /// </summary>
    private void DecorateShortcut(FileViewItem item)
    {
        if (item.IsDirectory || !KazoDesktopEntry.IsDesktopFile(item.FileName))
        {
            return;
        }

        string path = item.FullPath.Value;
        if (_shortcuts.TryGetValue(path, out ShortcutState? state) && state.Revision == item.Metadata.Revision)
        {
            if (state.Entry is { } entry)
            {
                KazoAppInfo? app = _apps?.GetValueOrDefault(entry.AppId);
                item.DisplayName = entry.Name;
                item.IconOverride = AppShortcutIcon(entry.AppId, app?.Category);
                item.IsDimmed = app is null;
            }
            else if (state.Link is { } link)
            {
                // ADR 0185 §13: a shortcut to a file or a folder, greyed when its target is gone.
                item.DisplayName = link.Name;
                item.IconOverride = ShellIconDescriptor.Vector(IconLibrary.Lucide, state.LinkToFolder == true ? "folder-symlink" : "file-symlink");
                item.IsDimmed = state.LinkToFolder is null;
            }

            return;
        }

        long size = item.Metadata is FileMetadata file ? file.Length : 0;
        if (size > KazoDesktopEntry.MaxFileSize)
        {
            _shortcuts[path] = new ShortcutState(item.Metadata.Revision, null);
            return;
        }

        _pendingShortcuts[path] = item.Metadata.Revision;
        if (!_loadingShortcuts)
        {
            _loadingShortcuts = true;
            _ = InvokeAsync(async () =>
            {
                // Runs outside any event of the host: its rights are made ambient for the reads.
                using IDisposable scope = KazoExecutionContext.Enter(HostContext);
                await LoadShortcutsAsync();
            });
        }
    }

    /// <summary>Reads the shortcuts listed but not read yet, and the catalog, then redraws the items.</summary>
    private async Task LoadShortcutsAsync()
    {
        try
        {
            // After the listing that asked for them has finished its render.
            await Task.Yield();
            _apps = await ReadInstalledAppsAsync();
            while (_pendingShortcuts.Count > 0)
            {
                (string path, long revision) = _pendingShortcuts.First();
                _pendingShortcuts.Remove(path);
                _shortcuts[path] = await ReadShortcutAsync(path, revision);
            }
        }
        finally
        {
            _loadingShortcuts = false;
        }

        _view?.RefreshFilter();
    }

    private async Task<IReadOnlyDictionary<string, KazoAppInfo>> ReadInstalledAppsAsync()
    {
        try
        {
            IReadOnlyList<KazoAppInfo> apps = await HostContext.Catalog.GetInstalledAsync(HostContext.CancellationToken);
            return apps.GroupBy(static app => app.Id, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            return new Dictionary<string, KazoAppInfo>(StringComparer.Ordinal);
        }
    }

    /// <summary>Reads a <c>.desktop</c> file: an app shortcut, a link to a file or folder (with its target's kind), or neither.</summary>
    private async Task<ShortcutState> ReadShortcutAsync(string path, long revision)
    {
        string content;
        try
        {
            content = await KazoFile.ReadAllTextAsync(path, HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ShortcutState(revision, null);
        }

        if (KazoDesktopEntry.TryParse(content, out KazoDesktopEntry? entry))
        {
            return new ShortcutState(revision, entry);
        }

        if (KazoDesktopLink.TryParse(content, out KazoDesktopLink? link))
        {
            return new ShortcutState(revision, null, link, await IsFolderAsync(link.Target));
        }

        return new ShortcutState(revision, null);
    }

    /// <summary>Whether <paramref name="path"/> is a folder (<see langword="true"/>), a file (<see langword="false"/>), or not there (<see langword="null"/>).</summary>
    private async Task<bool?> IsFolderAsync(string path)
    {
        try
        {
            if (await KazoDirectory.ExistsAsync(path, HostContext.CancellationToken))
            {
                return true;
            }

            return await KazoFile.ExistsAsync(path, HostContext.CancellationToken) ? false : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opening a shortcut launches its app with its arguments (ADR 0152 §5), or opens the file or folder
    /// it links to (ADR 0185 §13); <see langword="false"/> when the file is not a shortcut, which then
    /// opens as any file.
    /// </summary>
    private async Task<bool> TryLaunchShortcutAsync(FileViewItem item)
    {
        if (item.IsDirectory || !KazoDesktopEntry.IsDesktopFile(item.FileName))
        {
            return false;
        }

        ShortcutState state = _shortcuts.TryGetValue(item.FullPath.Value, out ShortcutState? known) && known.Revision == item.Metadata.Revision
            ? known
            : await ReadShortcutAsync(item.FullPath.Value, item.Metadata.Revision);
        if (state.Link is { } link)
        {
            await OpenLinkAsync(link);
            return true;
        }

        if (state.Entry is not { } entry)
        {
            return false;
        }

        _apps ??= await ReadInstalledAppsAsync();
        if (!_apps.ContainsKey(entry.AppId))
        {
            // The catalog may have changed since it was read: ask once more before refusing.
            _apps = await ReadInstalledAppsAsync();
        }

        if (!_apps.ContainsKey(entry.AppId))
        {
            await ReportErrorAsync(L["Error.AppMissing", entry.AppId]);
            return true;
        }

        try
        {
            await HostContext.Intents.LaunchAsync(entry.AppId, entry.Arguments, HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            await ReportErrorAsync(L["Error.LaunchDenied"]);
        }

        return true;
    }

    /// <summary>
    /// Opens the target of a link shortcut: a folder is entered (in a new explorer window on a desktop),
    /// a file opens with its app. The target is checked again: it may have moved since the listing.
    /// </summary>
    private async Task OpenLinkAsync(KazoDesktopLink link)
    {
        bool? folder = await IsFolderAsync(link.Target);
        if (folder is null)
        {
            await ReportErrorAsync(L["Error.LinkTargetMissing", link.Target]);
            return;
        }

        VirtualPath target = VirtualPath.Parse(link.Target);
        if (folder == false)
        {
            await OpenPathAsync(target);
        }
        else if (IsDesktop || _view is null)
        {
            try
            {
                await HostContext.Intents.OpenFileAsync(target, "inode/directory", HostContext.CancellationToken);
            }
            catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
            {
                await ReportErrorAsync(L["Error.OpenDenied"]);
            }
        }
        else
        {
            try
            {
                await _view.NavigateAsync(target);
            }
            catch (InvalidOperationException exception)
            {
                await ReportErrorAsync(exception.Message);
            }
        }
    }

    /// <summary>An app dropped from the Start menu: a shortcut to it in <paramref name="destination"/> (ADR 0160 §6).</summary>
    private async Task CreateShortcutsAsync(IReadOnlyList<string> appIds, VirtualPath destination)
    {
        if (appIds.Count == 0)
        {
            return;
        }

        _apps = await ReadInstalledAppsAsync();
        foreach (string appId in appIds)
        {
            string name = _apps.TryGetValue(appId, out KazoAppInfo? app) ? app.Name : appId;
            try
            {
                await KazoDesktopEntry.CreateShortcutAsync(destination.Value, appId, name, cancellationToken: HostContext.CancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await ReportErrorAsync(L["Error.Shortcut", name, exception.Message]);
            }
        }
    }

    /// <summary>
    /// A <c>.desktop</c> file read at one revision: an app shortcut (<see cref="Entry"/>), a link
    /// (<see cref="Link"/>, with <see cref="LinkToFolder"/> <see langword="null"/> when its target is gone),
    /// or neither for a file that is not a shortcut.
    /// </summary>
    private sealed record ShortcutState(long Revision, KazoDesktopEntry? Entry, KazoDesktopLink? Link = null, bool? LinkToFolder = null);
}
