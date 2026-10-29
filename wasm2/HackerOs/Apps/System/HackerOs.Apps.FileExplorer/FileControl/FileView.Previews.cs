using HackerOs.AppSdk;
using HackerOs.App.Abstractions;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.JSInterop;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// The preview of a folder's content on its icon, in Icons mode (ADR 0185 §14): an open folder with
/// the icons of its first entries. A folder's content is read only once its tile is on screen (the
/// view's script reports it), then kept per folder revision. Each previewed folder is watched (its
/// immediate children, as the folder shown): a change inside it reads the preview again, while the
/// previous one stays drawn. The watches end when the view leaves the folder.
/// </summary>
public partial class FileView
{
    /// <summary>How many entries a preview shows.</summary>
    internal const int FolderPreviewSize = 4;

    /// <summary>Previews kept before the oldest are dropped.</summary>
    private const int FolderPreviewCacheSize = 512;

    private readonly Dictionary<string, FolderPreview> _folderPreviews = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingPreviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreviewWatch> _previewWatches = new(StringComparer.Ordinal);

    /// <summary>Folders watched for their previews at once, at most (the tiles on screen, in practice).</summary>
    private const int MaxPreviewWatches = 64;

    /// <summary>
    /// The preview to draw on a folder's tile: <see langword="null"/> before its content was read (a plain
    /// folder icon), an empty list for an empty folder. A preview of an older revision is returned while
    /// the new one is read.
    /// </summary>
    internal IReadOnlyList<ShellIconDescriptor>? GetFolderPreview(FileViewItem item)
    {
        if (!item.IsDirectory || !_folderPreviews.TryGetValue(item.FullPath.Value, out FolderPreview? preview))
        {
            return null;
        }

        if (preview.Revision != item.Metadata.Revision)
        {
            _ = LoadFolderPreviewAsync(item);
        }

        return preview.Icons;
    }

    /// <summary>The view's script reports the folder tiles that came on screen: their previews are read.</summary>
    /// <param name="paths">The folders' paths.</param>
    [JSInvokable]
    public async Task LoadFolderPreviewsAsync(string[] paths)
    {
        // Called from JavaScript, outside any frame: enter the app's frame first (ADR 0161 §1).
        using IDisposable? scope = AppContext is { } context ? HackExecutionContext.Enter(context) : null;
        HashSet<string> wanted = new(paths ?? [], StringComparer.Ordinal);
        bool changed = false;
        foreach (FileViewItem item in Items.Where(item => item.IsDirectory && wanted.Contains(item.FullPath.Value)).ToList())
        {
            changed |= await LoadFolderPreviewAsync(item, render: false);
        }

        if (changed)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Reads one folder's first entries, unless its preview is current or being read.</summary>
    private async Task<bool> LoadFolderPreviewAsync(FileViewItem item, bool render = true, bool force = false)
    {
        string path = item.FullPath.Value;
        long revision = item.Metadata.Revision;
        if ((!force && _folderPreviews.TryGetValue(path, out FolderPreview? known) && known.Revision == revision) || !_loadingPreviews.Add(path))
        {
            return false;
        }

        try
        {
            FileSystemResult<FileSystemDirectorySnapshot> listing;
            try
            {
                listing = await FileSystem.EnumerateAsync(new FileSystemEnumerateRequest(item.FullPath));
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException or NotSupportedException)
            {
                return false;
            }

            if (!listing.Succeeded)
            {
                // A folder that cannot be read keeps its plain icon.
                return false;
            }

            if (_folderPreviews.Count >= FolderPreviewCacheSize)
            {
                _folderPreviews.Clear();
            }

            _folderPreviews[path] = new FolderPreview(revision, PickFolderPreview(listing.Value!.Path, listing.Value.Entries, EffectiveIconProvider));
            await WatchPreviewedFolderAsync(item.FullPath);
            if (render)
            {
                await InvokeAsync(StateHasChanged);
            }

            return true;
        }
        finally
        {
            _loadingPreviews.Remove(path);
        }
    }

    /// <summary>Watches a previewed folder's children, so a change inside it reads its preview again.</summary>
    private async Task WatchPreviewedFolderAsync(VirtualPath folder)
    {
        if (Watch is null || _previewWatches.ContainsKey(folder.Value) || _previewWatches.Count >= MaxPreviewWatches)
        {
            return;
        }

        ITopicChannelSubscription<FileSystemChangeEvent> subscription;
        try
        {
            subscription = await Watch.WatchAsync(folder, FileSystemWatchScope.ImmediateChildren);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Not watchable (a host without watches, no right): the preview follows the folder's revision only.
            return;
        }

        CancellationTokenSource stop = new();
        _previewWatches[folder.Value] = new PreviewWatch(subscription, stop);
        _ = FollowPreviewedFolderAsync(folder.Value, subscription, stop.Token);
    }

    private async Task FollowPreviewedFolderAsync(string path, ITopicChannelSubscription<FileSystemChangeEvent> subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TopicMessage<FileSystemChangeEvent> _ in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    if (Items.FirstOrDefault(item => item.IsDirectory && item.FullPath.Value == path) is { } item)
                    {
                        await LoadFolderPreviewAsync(item, force: true);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The view left the folder, or is gone.
        }
    }

    /// <summary>Ends the watches of the previewed folders (the view shows another folder, or is disposed).</summary>
    private async Task StopPreviewWatchesAsync()
    {
        List<PreviewWatch> watches = [.. _previewWatches.Values];
        _previewWatches.Clear();
        foreach (PreviewWatch watch in watches)
        {
            watch.Stop.Cancel();
            watch.Stop.Dispose();
            await watch.Subscription.DisposeAsync();
        }
    }

    /// <summary>
    /// The icons of the first entries of a folder, as the explorer sorts them (folders first, then by
    /// name); hidden entries (a leading dot) are left out.
    /// </summary>
    internal static IReadOnlyList<ShellIconDescriptor> PickFolderPreview(
        VirtualPath folder,
        IEnumerable<FileSystemDirectoryItem> entries,
        IShellIconProvider icons) =>
        [.. entries
            .Where(static entry => !entry.Name.Value.StartsWith('.'))
            .OrderBy(static entry => entry.Metadata.Kind == FileSystemEntryKind.Directory ? 0 : 1)
            .ThenBy(static entry => entry.Name.Value, StringComparer.OrdinalIgnoreCase)
            .Take(FolderPreviewSize)
            .Select(entry =>
            {
                string name = entry.Name.Value;
                int dot = name.LastIndexOf('.');
                bool isDirectory = entry.Metadata.Kind == FileSystemEntryKind.Directory;
                return icons.Resolve(new FileViewIconRequest(
                    CombineChild(folder, name),
                    isDirectory,
                    !isDirectory && dot > 0 ? name[dot..].ToLowerInvariant() : null,
                    entry.Metadata is FileMetadata file ? file.MediaType : null));
            })];

    /// <summary>The watch of one previewed folder.</summary>
    private sealed record PreviewWatch(ITopicChannelSubscription<FileSystemChangeEvent> Subscription, CancellationTokenSource Stop);

    /// <summary>A folder's preview at one revision of the folder.</summary>
    private sealed record FolderPreview(long Revision, IReadOnlyList<ShellIconDescriptor> Icons);
}
