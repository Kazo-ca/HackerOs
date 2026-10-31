using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.IO;
using HackerOs.Apps.FileExplorer.Archives;
using HackerOs.Apps.FileExplorer.FileControl;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// A <c>.zip</c> opened as a read-only folder (ADR 0185 §6-9), in the File Explorer's own windows only:
/// a double click enters the archive, a file of the archive opens from a copy extracted to <c>/tmp</c>,
/// a drag or a paste out of it extracts, and every change inside it is refused with a clear message.
/// </summary>
/// <remarks>
/// The explorer window opts in by cascading <see cref="ArchiveBrowsingName"/>; the same control shown
/// by another app, a file dialog or the desktop never sees inside an archive (§7).
/// </remarks>
public sealed partial class ExplorerFileView
{
    /// <summary>The name of the cascading <see langword="bool"/> by which an explorer window lets its control browse archives.</summary>
    internal const string ArchiveBrowsingName = "hackeros.file-explorer.archive-browsing";

    /// <summary>The folder of <c>/tmp</c> a file opened from an archive is extracted into (§7).</summary>
    internal const string TemporaryFolder = "/tmp";

    /// <summary>The commands that would change the archive, hidden from its menus and refused (§9).</summary>
    private static readonly HashSet<string> ArchiveWriteCommands = new(StringComparer.Ordinal)
    {
        HackFileMenuItems.Cut,
        HackFileMenuItems.Paste,
        HackFileMenuItems.PasteInto,
        HackFileMenuItems.Rename,
        HackFileMenuItems.Delete,
        HackFileMenuItems.DeletePermanently,
        HackFileMenuItems.NewFolder,
        HackFileMenuItems.Properties
    };

    private IAppFileSystemWatchGateway? _watch;
    private ZipArchiveWatchGateway? _archiveWatch;

    /// <summary>Gets or sets whether the window showing this control lets it browse archives (the File Explorer's own windows).</summary>
    [CascadingParameter(Name = ArchiveBrowsingName)]
    internal bool BrowseArchives { get; set; }

    /// <summary>Gets the archive reader of this control, or <see langword="null"/> when it does not browse archives.</summary>
    internal ZipArchiveBrowser? Archives => FileSystem is ZipArchiveFileSystemGateway archives ? archives.Archives : null;

    /// <summary>Gets the file system as the view sees it, archives included: the window reads the selection's content through it (Download).</summary>
    internal IAppFileSystemGateway ViewFileSystem => FileSystem;

    /// <summary>Gets whether the folder shown is an archive or a folder inside one.</summary>
    internal bool IsInArchive => _view is not null && IsInsideArchive(_view.CurrentDirectory);

    /// <summary>Whether this control browses archives and <paramref name="path"/> is one or lies inside one.</summary>
    internal bool IsInsideArchive(VirtualPath path) => Archives?.IsInsideArchive(path) == true;

    /// <summary>Whether this control browses archives: an explorer window, not its desktop view.</summary>
    private bool BrowsesArchives => BrowseArchives && !IsDesktop;

    /// <summary>The folder watch given to the view: a folder inside an archive is watched through the archive's folder.</summary>
    private IAppFileSystemWatchGateway WatchGateway
    {
        get
        {
            IAppFileSystemWatchGateway watch = HostContext.Watch;
            if (Archives is not { } archives)
            {
                return watch;
            }

            if (_archiveWatch is null || !ReferenceEquals(_watch, watch))
            {
                _watch = watch;
                _archiveWatch = new ZipArchiveWatchGateway(watch, archives);
            }

            return _archiveWatch;
        }
    }

    /// <summary>
    /// A file activated in an explorer window: an archive is entered as a folder (§6), a file of an
    /// archive is extracted to <c>/tmp</c> and opened from there (§7).
    /// </summary>
    /// <returns><see langword="true"/> when the archive rules handled the item.</returns>
    private async Task<bool> TryOpenArchiveItemAsync(FileViewItem item)
    {
        if (_view is null || Archives is not { } archives)
        {
            return false;
        }

        VirtualPath path = item.FullPath;
        if (await archives.LocateAsync(path, HostContext.CancellationToken) is { IsRoot: false } location)
        {
            if (await ExtractToTemporaryAsync(location, item.FileName) is not { } copy)
            {
                return true;
            }

            // An archive inside an archive opens from its copy, as a folder too.
            if (ZipArchiveBrowser.IsArchiveName(item.FileName))
            {
                await EnterArchiveAsync(copy);
            }
            else
            {
                await OpenPathAsync(copy);
            }

            return true;
        }

        if (!ZipArchiveBrowser.IsArchiveName(item.FileName))
        {
            return false;
        }

        await EnterArchiveAsync(path);
        return true;
    }

    /// <summary>"Open with" an app of the menu: a file of an archive opens from its extracted copy (§7).</summary>
    private async Task OpenWithAppAsync(string appId, FileViewItem item)
    {
        if (await PathToOpenAsync(item) is { } path)
        {
            await LaunchWithAsync(appId, path.Value);
        }
    }

    /// <summary>"Choose another app…": a file of an archive opens from its extracted copy (§7).</summary>
    private async Task ChooseAppForAsync(FileViewItem item)
    {
        if (await PathToOpenAsync(item) is { } path)
        {
            await ChooseAppAsync(path, candidateAppIds: null);
        }
    }

    /// <summary>The path an app opens for <paramref name="item"/>: the item itself, or for an item of an archive its copy in <c>/tmp</c>.</summary>
    private async Task<VirtualPath?> PathToOpenAsync(FileViewItem item) =>
        Archives is { } archives && await archives.LocateAsync(item.FullPath, HostContext.CancellationToken) is { IsRoot: false } location
            ? await ExtractToTemporaryAsync(location, item.FileName)
            : item.FullPath;

    /// <summary>Shows the inside of an archive, or says it cannot be read.</summary>
    private async Task EnterArchiveAsync(VirtualPath archive)
    {
        try
        {
            await _view!.NavigateAsync(archive);
        }
        catch (InvalidOperationException)
        {
            await ReportErrorAsync(L["Error.ArchiveUnreadable", archive.Value[(archive.Value.LastIndexOf('/') + 1)..]]);
        }
    }

    /// <summary>
    /// Extracts a file (or folder) of an archive into a new folder of <c>/tmp</c> (§7). A change made to
    /// the copy never goes back into the archive.
    /// </summary>
    /// <returns>The copy, or <see langword="null"/> after reporting why it failed.</returns>
    private async Task<VirtualPath?> ExtractToTemporaryAsync(ZipArchiveLocation location, string name)
    {
        ZipArchiveBrowser archives = Archives!;
        string archiveName = location.Archive.Value[(location.Archive.Value.LastIndexOf('/') + 1)..];
        VirtualPath folder = VirtualPath.Parse($"{TemporaryFolder}/{archiveName}-{Guid.NewGuid().ToString("N")[..8]}");
        FileSystemMutationResult created = await archives.FileSystem.CreateAsync(
            new FileSystemCreateRequest(folder, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0b111_000_000)),
            HostContext.CancellationToken);
        if (!created.Succeeded)
        {
            await ReportErrorAsync(L["Error.ArchiveExtract", name, created.Transaction.Error?.Code.ToString() ?? string.Empty]);
            return null;
        }

        VirtualPath copy = VirtualPath.Parse($"{folder.Value}/{name}");
        if (await archives.ExtractAsync(location, copy, HostContext.CancellationToken) is { } error)
        {
            await ReportErrorAsync(L["Error.ArchiveExtract", name, error.Code.ToString()]);
            return null;
        }

        return copy;
    }

    /// <summary>Whether this control browses archives and <paramref name="path"/> lies strictly inside one (not the <c>.zip</c> file itself).</summary>
    internal bool IsArchiveEntry(VirtualPath path) => Archives?.IsArchiveEntry(path) == true;

    /// <summary>Refuses a change of a folder shown as an archive or inside one, with the read-only message (§9).</summary>
    /// <returns><see langword="true"/> when the change was refused.</returns>
    private Task<bool> RefuseInArchiveAsync(VirtualPath folder) => RefuseAsync(IsInsideArchive(folder));

    /// <summary>Refuses a change of items of an archive, with the read-only message (§9).</summary>
    /// <returns><see langword="true"/> when one of <paramref name="items"/> is inside an archive and the change was refused.</returns>
    private Task<bool> RefuseArchiveEntriesAsync(IEnumerable<FileViewItem> items) => RefuseAsync(items.Any(item => IsArchiveEntry(item.FullPath)));

    private async Task<bool> RefuseAsync(bool refused)
    {
        if (refused)
        {
            await ReportErrorAsync(L["Error.ArchiveReadOnly"]);
        }

        return refused;
    }

    /// <summary>Hides the entries that would change the archive from a menu opened inside one (§9).</summary>
    private static void HideArchiveWriteCommands(HackMenuDefinition menu)
    {
        foreach (string command in ArchiveWriteCommands)
        {
            if (menu.Find(command) is { } entry)
            {
                entry.IsVisible = false;
            }
        }
    }

    /// <summary>
    /// A drag or a paste out of an archive (§9): the items of an archive are extracted into
    /// <paramref name="destination"/>, with the usual name-conflict dialog, and never removed from the
    /// archive, even for a move. The other sources are left to the ordinary transfer.
    /// </summary>
    /// <param name="sources">The dragged or pasted paths.</param>
    /// <param name="destination">The folder they go to, outside any archive.</param>
    /// <returns>The sources that are not in an archive, and the result of the extractions.</returns>
    private async Task<(IReadOnlyList<string> Others, HackFileTransferResult Extracted)> ExtractFromArchivesAsync(
        IReadOnlyList<string> sources, VirtualPath destination)
    {
        List<string> others = [];
        List<HackFileTransferItemResult> completed = [];
        List<string> skipped = [];
        List<HackFileTransferFailure> failures = [];
        if (Archives is not { } archives)
        {
            return (sources, new HackFileTransferResult(completed, skipped, failures));
        }

        HackFileConflictResolution? always = null;
        foreach (string source in sources)
        {
            VirtualPath path = VirtualPath.Parse(source);
            if (await archives.LocateAsync(path, HostContext.CancellationToken) is not { IsRoot: false } location)
            {
                others.Add(source);
                continue;
            }

            string name = HackFileOperations.GetName(source);
            VirtualPath target = Child(destination, name);
            FileSystemResult<FileSystemEntrySnapshot> existing = await archives.FileSystem.StatAsync(new FileSystemStatRequest(target), HostContext.CancellationToken);
            if (existing.Succeeded)
            {
                FileSystemResult<FileSystemEntrySnapshot> entry = await FileSystem.StatAsync(new FileSystemStatRequest(path), HostContext.CancellationToken);
                bool sourceIsDirectory = entry.Value?.Metadata.Kind == FileSystemEntryKind.Directory;
                bool targetIsDirectory = existing.Value!.Metadata.Kind == FileSystemEntryKind.Directory;
                HackFileConflictResolution resolution = always ?? await AskConflictAsync(
                    new HackFileTransferConflict(source, target.Value, sourceIsDirectory, targetIsDirectory));
                if (resolution.ApplyToAll)
                {
                    always = resolution;
                }

                if (resolution.Choice == HackFileConflictChoice.Skip)
                {
                    skipped.Add(source);
                    continue;
                }

                if (resolution.Choice == HackFileConflictChoice.KeepBoth)
                {
                    HashSet<string> taken = await NamesInAsync(destination);
                    target = Child(destination, HackFileOperations.GetCopyName(name, sourceIsDirectory, taken.Contains));
                }
                else
                {
                    try
                    {
                        if (targetIsDirectory)
                        {
                            await HackDirectory.DeleteAsync(target.Value, recursive: true, HostContext.CancellationToken);
                        }
                        else
                        {
                            await HackFile.DeleteAsync(target.Value, HostContext.CancellationToken);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        failures.Add(new HackFileTransferFailure(source, exception.Message));
                        continue;
                    }
                }
            }

            if (await archives.ExtractAsync(location, target, HostContext.CancellationToken) is { } error)
            {
                failures.Add(new HackFileTransferFailure(source, error.Code.ToString()));
            }
            else
            {
                completed.Add(new HackFileTransferItemResult(source, target.Value));
            }
        }

        return (others, new HackFileTransferResult(completed, skipped, failures));
    }

    private async Task<HashSet<string>> NamesInAsync(VirtualPath folder)
    {
        FileSystemResult<FileSystemDirectorySnapshot> listing = await FileSystem.EnumerateAsync(new FileSystemEnumerateRequest(folder), HostContext.CancellationToken);
        return listing.Succeeded
            ? new HashSet<string>(listing.Value!.Entries.Select(static entry => entry.Name.Value), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private static VirtualPath Child(VirtualPath folder, string name) =>
        VirtualPath.Parse(folder.Value == "/" ? $"/{name}" : $"{folder.Value}/{name}");

    /// <summary>Joins two transfer results into the one summary of a drop or a paste.</summary>
    private static HackFileTransferResult Merge(HackFileTransferResult first, HackFileTransferResult second) =>
        new([.. first.Completed, .. second.Completed], [.. first.Skipped, .. second.Skipped], [.. first.Failures, .. second.Failures], first.Cancelled || second.Cancelled);
}
