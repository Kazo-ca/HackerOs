using HackerOs.AppSdk;
using System.IO.Compression;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.JSInterop;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// Dragging items out of the browser downloads them (ADR 0185 §4-5, Chrome and Edge, <c>DownloadURL</c>):
/// one file as it is, a folder or several items as a <c>.zip</c>. The browser takes the content only as
/// a URL written during <c>dragstart</c>, so it is prepared when the mouse button is pressed on an item
/// (the view's script reports the press): the size the OS gives decides first, then the content is read
/// or compressed into a blob of the page. Beyond <see cref="ExternalDragLimit"/> (about 50 MB) nothing is prepared and
/// the drag shows a bubble that points to Download; the drag into a HackerOS folder works as before.
/// </summary>
public partial class FileView
{
    /// <summary>The largest content dragged out of the browser, as the OS measures it.</summary>
    internal const long ExternalDragLimit = 50L * 1024 * 1024;

    /// <summary>The limit this view applies: <see cref="ExternalDragLimit"/>, lowered by tests.</summary>
    internal long ExternalDragLimitBytes { get; set; } = ExternalDragLimit;

    /// <summary>What the last press prepared: for which items, and whether they were too large.</summary>
    private ExternalDragState? _externalDrag;

    /// <summary>
    /// The mouse button went down on an item: the items a drag would carry (the selection when the item
    /// is in it, else the item alone) are measured, then read or compressed for the browser.
    /// </summary>
    /// <param name="path">The pressed item.</param>
    /// <param name="selectedPaths">The items selected at that moment.</param>
    [JSInvokable]
    public async Task PrepareExternalDragAsync(string path, string[] selectedPaths)
    {
        // Called from JavaScript, outside any frame: enter the app's frame first (ADR 0161 §1).
        using IDisposable? scope = AppContext is { } context ? HackExecutionContext.Enter(context) : null;
        if (!AllowDragDrop || string.IsNullOrEmpty(path))
        {
            return;
        }

        string[] paths = selectedPaths is not null && selectedPaths.Contains(path, StringComparer.Ordinal) ? selectedPaths : [path];
        List<FileViewItem> items = [.. paths.Select(FindItem).OfType<FileViewItem>()];
        if (items.Count == 0)
        {
            return;
        }

        string key = ExternalDragKey(items);
        string signature = ExternalDragSignature(items);
        if (_externalDrag is { } known && known.Key == key && known.Signature == signature)
        {
            // Pressed again on the same items, unchanged: what was prepared still holds.
            return;
        }

        ExternalDragState state = new(key, signature);
        _externalDrag = state;
        IJSObjectReference module = await EnsureJsModuleAsync();

        long? size = await MeasureItemsAsync(items);
        if (size is null || size > ExternalDragLimitBytes)
        {
            state.TooLarge = size is not null;
            state.Size = size ?? 0;
            await module.InvokeVoidAsync("clearPreparedDownload");
            return;
        }

        ExternalDragContent? content = await ReadExternalDragContentAsync(items);
        if (content is null || !ReferenceEquals(_externalDrag, state))
        {
            return;
        }

        await module.InvokeAsync<bool>("prepareDownload", key, content.Name, content.MediaType, content.Bytes);
    }

    /// <summary>The key of a drag of <paramref name="items"/>: their paths, sorted, one per line.</summary>
    internal static string ExternalDragKey(IEnumerable<FileViewItem> items) =>
        string.Join('\n', items.Select(static item => item.FullPath.Value).Order(StringComparer.Ordinal));

    /// <summary>
    /// The arguments a drag start passes to the script for <paramref name="items"/>: the key of the
    /// content prepared for them (the script writes <c>DownloadURL</c> only when it matches), and the
    /// bubble's text when they are too large.
    /// </summary>
    internal (string Key, string? Hint) ExternalDragArguments(IEnumerable<FileViewItem> items)
    {
        string key = ExternalDragKey(items);
        string? hint = _externalDrag is { TooLarge: true } state && state.Key == key
            ? L["Drag.TooLarge", FileViewDetails.FormatBytes(state.Size, L)].Value
            : null;
        return (key, hint);
    }

    /// <summary>The name and type the browser saves: the file itself, or a <c>.zip</c>.</summary>
    internal static (string Name, string MediaType, bool Zip) ExternalDragTarget(IReadOnlyList<FileViewItem> items)
    {
        if (items.Count == 1 && !items[0].IsDirectory)
        {
            string mediaType = items[0].Metadata is FileMetadata { MediaType: { Length: > 0 } type } ? type : "application/octet-stream";
            return (items[0].FileName, mediaType, false);
        }

        return (items.Count == 1 ? items[0].FileName + ".zip" : "Archive.zip", "application/zip", true);
    }

    private FileViewItem? FindItem(string path) =>
        Items.FirstOrDefault(item => string.Equals(item.FullPath.Value, path, StringComparison.Ordinal));

    private static string ExternalDragSignature(IEnumerable<FileViewItem> items) =>
        string.Join(',', items.OrderBy(static item => item.FullPath.Value, StringComparer.Ordinal).Select(static item => item.Metadata.Revision));

    /// <summary>The total size the OS gives for the items, or <see langword="null"/> when one cannot be measured.</summary>
    private async Task<long?> MeasureItemsAsync(IReadOnlyList<FileViewItem> items)
    {
        long total = 0;
        foreach (FileViewItem item in items)
        {
            if (!item.IsDirectory && item.Metadata is FileMetadata file)
            {
                total += file.Length;
            }
            else
            {
                FileSystemResult<FileSystemTreeSize> measured;
                try
                {
                    measured = await FileSystem.MeasureAsync(item.FullPath);
                }
                catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException or NotSupportedException)
                {
                    return null;
                }

                if (!measured.Succeeded)
                {
                    return null;
                }

                total += measured.Value!.Length;
            }

            if (total > ExternalDragLimitBytes)
            {
                // Over the limit already: the rest need not be measured.
                return total;
            }
        }

        return total;
    }

    /// <summary>The content handed to the browser, or <see langword="null"/> when it cannot be read.</summary>
    private async Task<ExternalDragContent?> ReadExternalDragContentAsync(IReadOnlyList<FileViewItem> items)
    {
        (string name, string mediaType, bool zip) = ExternalDragTarget(items);
        try
        {
            using MemoryStream buffer = new();
            if (!zip)
            {
                FileSystemResult<FileSystemContentReadHandle> read = await FileSystem.ReadAsync(new FileSystemReadRequest(items[0].FullPath));
                if (!read.Succeeded || read.Value is null)
                {
                    return null;
                }

                await using FileSystemContentReadHandle handle = read.Value;
                await handle.Content.CopyToAsync(buffer);
            }
            else
            {
                using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (FileViewItem item in items)
                    {
                        if (item.IsDirectory)
                        {
                            await FileExplorerZipService.AddDirectoryToZipAsync(FileSystem, archive, item.FullPath, item.FileName, CancellationToken.None);
                        }
                        else
                        {
                            await FileExplorerZipService.AddFileToZipAsync(FileSystem, archive, item.FullPath, item.FileName, CancellationToken.None);
                        }
                    }
                }
            }

            return new ExternalDragContent(name, mediaType, buffer.ToArray());
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException or NotSupportedException or InvalidDataException)
        {
            // Not readable now: the drag stays inside HackerOS.
            return null;
        }
    }

    /// <summary>What a press prepared.</summary>
    private sealed class ExternalDragState(string key, string signature)
    {
        public string Key { get; } = key;

        public string Signature { get; } = signature;

        public bool TooLarge { get; set; }

        public long Size { get; set; }
    }

    /// <summary>The bytes handed to the browser, with the name and type it saves them under.</summary>
    private sealed record ExternalDragContent(string Name, string MediaType, byte[] Bytes);
}
