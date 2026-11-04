using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.AppSdk.IO;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The entries the explorer window adds to the file menu of its control (ADR 0185 §10-13): "Compress to
/// .zip…", "Download", "Copy path" and "Send to ▸", in a group just before Properties. Only the explorer's
/// own windows (desktop and phone) have them: the desktop and the file dialogs keep the control's menu.
/// </summary>
public partial class FileExplorerWindow
{
    /// <summary>Menu id of "Compress to .zip…".</summary>
    internal const string CompressMenuItem = "explorer.compress";

    /// <summary>Menu id of "Download".</summary>
    internal const string DownloadMenuItem = "explorer.download";

    /// <summary>Menu id of "Copy path".</summary>
    internal const string CopyPathMenuItem = "explorer.copy-path";

    /// <summary>Menu id of the "Send to" submenu.</summary>
    internal const string SendToMenuItem = "explorer.send-to";

    /// <summary>Menu id of "Send to ▸ Desktop".</summary>
    internal const string SendToDesktopMenuItem = "explorer.send-to.desktop";

    /// <summary>Menu id of "Send to ▸ Compressed folder".</summary>
    internal const string SendToZipMenuItem = "explorer.send-to.zip";

    /// <summary>Menu id of "Send to ▸ My computer".</summary>
    internal const string SendToComputerMenuItem = "explorer.send-to.computer";

    /// <summary>Prefix of the menu id of "Send to ▸ &lt;network drive&gt;", followed by its mount point.</summary>
    internal const string SendToDrivePrefix = "explorer.send-to.drive:";

    /// <summary>Adds the window's entries to a menu over items (none on the folder's background).</summary>
    private protected async Task OnFileMenuOpeningAsync(KazoFileMenuOpeningEventArgs args)
    {
        if (args.Items.Count == 0 || _showTrash)
        {
            return;
        }

        IReadOnlyList<KazoFileViewItem> items = args.Items;
        IReadOnlyList<KazoDriveInfo> drives = await ReadNetworkDrivesAsync();
        KazoMenuGroupDefinition group = BuildMenuGroup(items, drives, run => EventCallback.Factory.Create<object?>(this, _ => run()), key => L[key], InArchive);

        int properties = IndexOfGroup(args.Menu, KazoFileMenuItems.Properties);
        if (properties < 0)
        {
            args.Menu.Groups.Add(group);
        }
        else
        {
            args.Menu.Groups.Insert(properties, group);
        }
    }

    /// <summary>
    /// The window's group of entries over <paramref name="items"/>: compress, download, copy the path,
    /// then "Send to ▸" the desktop, a compressed folder, this computer and each network drive ready.
    /// </summary>
    /// <param name="items">The items the menu acts on.</param>
    /// <param name="drives">The network drives ready to receive a copy.</param>
    /// <param name="click">The click callback of an entry, given what it runs.</param>
    /// <param name="text">The explorer's texts.</param>
    /// <param name="readOnlyArchive">The items are in a read-only archive (ADR 0185 §9): no Compress, no Send to.</param>
    internal KazoMenuGroupDefinition BuildMenuGroup(
        IReadOnlyList<KazoFileViewItem> items,
        IReadOnlyList<KazoDriveInfo> drives,
        Func<Func<Task>, EventCallback<object?>> click,
        Func<string, string> text,
        bool readOnlyArchive = false)
    {
        KazoMenuGroupDefinition group = new();
        // In a read-only archive (ADR 0185 §9) only what reads the items stays: Download and Copy path.
        if (!readOnlyArchive)
        {
            Entry(group, CompressMenuItem, text("Menu.Compress"), "archive", click(() => CompressAsync(items)));
        }

        Entry(group, DownloadMenuItem, text("Menu.Download"), "download", click(() => DownloadAsync(items)));
        Entry(group, CopyPathMenuItem, text("Menu.CopyPath"), "clipboard-copy", click(() => CopyPathsAsync(items)));
        if (readOnlyArchive)
        {
            return group;
        }

        KazoMenuItemDefinition sendTo = group.Add(SendToMenuItem, text("Menu.SendTo"));
        sendTo.Icon = Icon("send");
        KazoMenuGroupDefinition local = new();
        Entry(local, SendToDesktopMenuItem, text("Menu.SendTo.Desktop"), "monitor", click(() => SendToDesktopAsync(items)));
        Entry(local, SendToZipMenuItem, text("Menu.SendTo.Zip"), "archive", click(() => CompressToDefaultNameAsync(items)));
        Entry(local, SendToComputerMenuItem, text("Menu.SendTo.Computer"), "hard-drive-download", click(() => DownloadAsync(items)));
        sendTo.Children.Add(local);

        if (drives.Count > 0)
        {
            KazoMenuGroupDefinition network = new();
            foreach (KazoDriveInfo drive in drives)
            {
                string mountPoint = drive.Name;
                string label = string.Format(System.Globalization.CultureInfo.CurrentCulture, text("Menu.SendTo.Drive"), DriveLabel(mountPoint));
                Entry(network, SendToDrivePrefix + mountPoint, label, "hard-drive", click(() => CopyToDriveAsync(items, mountPoint)));
            }

            sendTo.Children.Add(network);
        }

        return group;
    }

    /// <summary>The name a network drive shows: the last segment of its mount point (<c>/mnt/photos</c> is <c>photos</c>).</summary>
    internal static string DriveLabel(string mountPoint)
    {
        string trimmed = mountPoint.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>The default archive name: the one item's name, or <c>Archive</c>, with <c>.zip</c>.</summary>
    internal static string DefaultArchiveName(IReadOnlyList<KazoFileViewItem> items) =>
        items.Count == 1 ? items[0].Name + ".zip" : "Archive.zip";

    /// <summary>The first free name among <c>name.zip</c>, <c>name (2).zip</c>, … given the names taken.</summary>
    internal static string UniqueArchiveName(string defaultName, Func<string, bool> isTaken)
    {
        string stem = defaultName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? defaultName[..^".zip".Length] : defaultName;
        for (int attempt = 1; ; attempt++)
        {
            string name = attempt == 1 ? stem + ".zip" : $"{stem} ({attempt}).zip";
            if (!isTaken(name))
            {
                return name;
            }
        }
    }

    /// <summary>"Copy path": the absolute paths of the items, one per line, as text on the clipboard.</summary>
    private protected async Task CopyPathsAsync(IReadOnlyList<KazoFileViewItem> items)
    {
        try
        {
            await KazoClipboard.SetTextAsync(string.Join('\n', items.Select(static item => item.Path)), AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            ReportError(L["Error.CopyPath", exception.Message]);
        }
    }

    /// <summary>"Send to ▸ Desktop": a shortcut to each item on the desktop (ADR 0185 §13), not a copy.</summary>
    private protected async Task SendToDesktopAsync(IReadOnlyList<KazoFileViewItem> items)
    {
        string desktop = KazoEnvironment.GetFolderPath(KazoSpecialFolder.Desktop);
        try
        {
            await KazoDirectory.CreateDirectoryAsync(desktop, AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError(L["Error.SendToDesktop", desktop, exception.Message]);
            return;
        }

        List<string> failures = [];
        foreach (KazoFileViewItem item in items)
        {
            try
            {
                await KazoDesktopLink.CreateLinkAsync(desktop, item.Path, item.Name, AppContext.CancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add(L["Error.SendToDesktop", item.Name, exception.Message]);
            }
        }

        if (failures.Count > 0)
        {
            ReportError(string.Join("; ", failures));
        }
    }

    /// <summary>"Send to ▸ Compressed folder": a <c>.zip</c> next to the items, named after them, without asking.</summary>
    private protected async Task CompressToDefaultNameAsync(IReadOnlyList<KazoFileViewItem> items)
    {
        if (_files is null || items.Count == 0) return;

        VirtualPath folder = VirtualPath.Parse(_files.CurrentDirectory);
        HashSet<string> taken = new(_files.Items.Select(static item => item.Name), StringComparer.Ordinal);
        string name = UniqueArchiveName(DefaultArchiveName(items), taken.Contains);
        await CompressToAsync(items, CombineChild(folder, name));
    }

    /// <summary>"Send to ▸ &lt;network drive&gt;": a copy of the items at the root of the drive.</summary>
    private protected async Task CopyToDriveAsync(IReadOnlyList<KazoFileViewItem> items, string mountPoint)
    {
        KazoFileTransferResult result = await TrackOperation(KazoFileOperations.CopyAsync(
            [.. items.Select(static item => item.Path)], mountPoint, AppContext.CancellationToken));
        ReportTransfer(result, TransferVerb.Copy);
        StateHasChanged();
    }

    /// <summary>The network drives ready for a copy; none when the drives cannot be read.</summary>
    private async Task<IReadOnlyList<KazoDriveInfo>> ReadNetworkDrivesAsync()
    {
        try
        {
            KazoDriveInfo[] drives = await KazoDriveInfo.GetDrivesAsync(AppContext.CancellationToken);
            return [.. drives.Where(static drive => drive.DriveType == KazoDriveType.Network && drive.IsReady)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    private static int IndexOfGroup(KazoMenuDefinition menu, string id)
    {
        for (int index = 0; index < menu.Groups.Count; index++)
        {
            if (menu.Groups[index].Items.Any(item => item.Id == id))
            {
                return index;
            }
        }

        return -1;
    }

    private static void Entry(KazoMenuGroupDefinition group, string id, string title, string icon, EventCallback<object?> onClick)
    {
        KazoMenuItemDefinition entry = group.Add(id, title);
        entry.Icon = Icon(icon);
        entry.OnClick = onClick;
    }

    private static KazoIcon Icon(string name) => KazoIcons.Vector(IconLibrary.Lucide, name);
}
