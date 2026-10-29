using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.IO;

/// <summary>
/// One element of the user's trash, as listed by <see cref="HackTrash.GetItemsAsync"/> (ADR 0052).
/// </summary>
/// <param name="Name">
/// Name of the element under <c>files/</c>. It is the stable identifier of the element: two elements
/// trashed from the same path get distinct names (<c>report.txt</c>, <c>report.2.txt</c>).
/// </param>
/// <param name="OriginalPath">Absolute path the element was deleted from; <see langword="null"/> when its <c>.trashinfo</c> is missing or unreadable.</param>
/// <param name="DeletedAt">Deletion time (simulated clock, local time); <see langword="null"/> when unknown.</param>
/// <param name="IsDirectory">Whether the element is a directory (a symbolic link counts as a file).</param>
/// <param name="Length">Size in bytes of the file, or the sum of every file below the directory.</param>
public sealed record HackTrashItem(
    string Name,
    string? OriginalPath,
    DateTimeOffset? DeletedAt,
    bool IsDirectory,
    long Length)
{
    /// <summary>
    /// Gets whether the synchronization put this element in the trash because it was deleted on
    /// another device of the user (ADR 0101 section 1).
    /// </summary>
    public bool DeletedOnAnotherDevice { get; init; }
}

/// <summary>
/// The user's trash, following the freedesktop.org Trash specification 1.0 (ADR 0052). Runs as the
/// ambient app (<see cref="HackExecutionContext.Current"/>) through its filesystem gateway only, so
/// capabilities, mode bits and ownership decide exactly as for <see cref="HackFile"/>.
/// </summary>
/// <remarks>
/// <para>
/// The trash lives in <c>$HOME/.local/share/Trash/</c> of the <b>effective</b> user: under
/// <c>sudo</c> that is <c>/root/.local/share/Trash/</c>. <c>files/&lt;name&gt;</c> holds the element
/// moved as-is (content, mode, owner and dates kept) and <c>info/&lt;name&gt;.trashinfo</c> records
/// where it came from and when:
/// </para>
/// <code>
/// [Trash Info]
/// Path=/home/alice/Documents/final%20report.txt
/// DeletionDate=2026-10-10T14:03:27
/// </code>
/// <para>
/// Like the specification asks, the <c>.trashinfo</c> is created first, exclusively, to reserve the
/// name; the element is moved afterwards and the reservation is rolled back if the move fails. The
/// trash directories are created on demand with mode <c>0700</c>.
/// </para>
/// <para>
/// <see cref="HackFile.DeleteAsync"/> and <see cref="HackDirectory.DeleteAsync"/> stay permanent
/// deletions, like <c>System.IO</c>; this class is the reversible alternative.
/// </para>
/// </remarks>
public static class HackTrash
{
    /// <summary>Extension of the metadata files under <c>info/</c>.</summary>
    public const string InfoExtension = ".trashinfo";

    private const string TrashRelativePath = ".local/share/Trash";
    private const string RootHome = "/root";
    private const int MaximumNameAttempts = 10_000;

    /// <summary>Mode of the trash directories, as the specification recommends (<c>0700</c>).</summary>
    private static readonly FileSystemPermissions TrashDirectoryMode = FileSystemPermissions.FromMode(0b111_000_000);

    /// <summary>Mode of a <c>.trashinfo</c> file (<c>0600</c>): only the owner can read where its files came from.</summary>
    private static readonly FileSystemPermissions InfoFileMode = FileSystemPermissions.FromMode(0b110_000_000);

    /// <summary>
    /// Gets the canonical path of the ambient effective user's trash (<c>~/.local/share/Trash</c>, or
    /// <c>/root/.local/share/Trash</c> under <c>sudo</c>). The directory may not exist yet.
    /// </summary>
    /// <exception cref="HackExecutionContextMissingException">No app scope is active.</exception>
    public static string TrashDirectory => GetLayout(HackExecutionContext.Current).Root;

    /// <summary>
    /// Moves a file, a symbolic link or a whole directory to the trash. An element already inside the
    /// trash is deleted permanently instead.
    /// </summary>
    /// <param name="path">Path of the element (relative paths resolve against the working directory).</param>
    /// <param name="cancellationToken">Cancels before the move commits.</param>
    /// <returns>The trashed element (for an element already in the trash, what was permanently deleted).</returns>
    /// <exception cref="HackFileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    /// <exception cref="HackIOException">
    /// The path is <c>/</c>, a home directory or contains the trash, or it lives on a virtual
    /// filesystem (<c>/proc</c>, <c>/dev</c>): "Cannot move to trash: virtual file system".
    /// </exception>
    /// <exception cref="HackUnauthorizedAccessException">The caller may not remove the element.</exception>
    public static async Task<HackTrashItem> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath source = HackPath.Resolve(path, nameof(path));
        IAppExecutionContext context = HackExecutionContext.Current;
        IAppFileSystemGateway gateway = context.FileSystem;
        TrashLayout layout = GetLayout(context);

        EnsureMayBeTrashed(source, context, layout);

        FileSystemEntrySnapshot entry = await HackFileSystemCore.TryStatAsync(
                gateway, source, FileSystemLinkBehavior.NoFollow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false)
            ?? throw new HackFileNotFoundException(source.Value);
        long length = await MeasureAsync(gateway, source, entry.Metadata, cancellationToken).ConfigureAwait(false);
        bool isDirectory = entry.Metadata.Kind == FileSystemEntryKind.Directory;
        DateTimeOffset now = LocalNow(context);

        // An element already in the trash has nowhere else to go: remove it for good.
        if (IsInside(source.Value, layout.Root))
        {
            await HackFileSystemCore.DeleteEntryAsync(gateway, source, recursive: true, HackEntryTarget.File, cancellationToken)
                .ConfigureAwait(false);
            Log(context, $"Permanently deleted '{source.Value}' from the trash.");
            return new HackTrashItem(HackPath.GetFileName(source.Value)!, source.Value, now, isDirectory, length);
        }

        await EnsureTrashDirectoriesAsync(gateway, layout, cancellationToken).ConfigureAwait(false);

        string infoContent = HackTrashInfo.Format(source.Value, now);
        (string name, VirtualPath infoPath) = await ReserveNameAsync(
            gateway, layout, HackPath.GetFileName(source.Value)!, infoContent, cancellationToken).ConfigureAwait(false);

        VirtualPath destination = VirtualPath.Parse(layout.FilesDirectory + "/" + name);
        try
        {
            await MoveAsync(gateway, source, destination, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Roll the reservation back so a failed move leaves no orphan .trashinfo behind.
            await TryDeleteAsync(gateway, infoPath, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        Log(context, $"Moved '{source.Value}' to the trash as '{name}'.");
        return new HackTrashItem(name, source.Value, now, isDirectory, length);
    }

    /// <summary>
    /// Lists the trash, oldest deletion first. A <c>.trashinfo</c> whose element is gone is removed
    /// on the way; an element without <c>.trashinfo</c> is listed with an unknown origin.
    /// </summary>
    /// <returns>Every element of the trash; empty when the trash does not exist yet.</returns>
    public static async Task<IReadOnlyList<HackTrashItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        IAppExecutionContext context = HackExecutionContext.Current;
        IAppFileSystemGateway gateway = context.FileSystem;
        TrashLayout layout = GetLayout(context);

        Dictionary<string, FileSystemEntryMetadata> files = await ListAsync(gateway, layout.FilesDirectory, cancellationToken).ConfigureAwait(false);
        Dictionary<string, FileSystemEntryMetadata> infos = await ListAsync(gateway, layout.InfoDirectory, cancellationToken).ConfigureAwait(false);

        List<HackTrashItem> items = [];
        foreach (string infoName in infos.Keys)
        {
            if (!infoName.EndsWith(InfoExtension, StringComparison.Ordinal) || files.ContainsKey(infoName[..^InfoExtension.Length]))
            {
                continue;
            }

            // Orphan metadata: its element was removed behind our back (e.g. `rm` inside files/).
            await TryDeleteAsync(gateway, VirtualPath.Parse(layout.InfoDirectory + "/" + infoName), cancellationToken).ConfigureAwait(false);
        }

        foreach ((string name, FileSystemEntryMetadata metadata) in files)
        {
            HackTrashInfo? info = infos.ContainsKey(name + InfoExtension)
                ? await ReadInfoAsync(gateway, layout, name, cancellationToken).ConfigureAwait(false)
                : null;
            VirtualPath path = VirtualPath.Parse(layout.FilesDirectory + "/" + name);
            long length = await MeasureAsync(gateway, path, metadata, cancellationToken).ConfigureAwait(false);
            items.Add(new HackTrashItem(
                name, info?.OriginalPath, info?.DeletedAt, metadata.Kind == FileSystemEntryKind.Directory, length)
            {
                DeletedOnAnotherDevice = info?.DeletedOnAnotherDevice ?? false
            });
        }

        items.Sort(static (left, right) =>
        {
            int byDate = Nullable.Compare(left.DeletedAt, right.DeletedAt);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.Name, right.Name);
        });
        return items;
    }

    /// <summary>
    /// Puts a trashed element back, at <paramref name="destinationPath"/> or else at its original
    /// location, recreating missing parent directories. Never overwrites anything.
    /// </summary>
    /// <param name="item">Element to restore (only its <see cref="HackTrashItem.Name"/> identifies it).</param>
    /// <param name="destinationPath">Where to restore it; <see langword="null"/> for its original path.</param>
    /// <param name="cancellationToken">Cancels before the move commits.</param>
    /// <returns>The canonical path the element was restored to.</returns>
    /// <exception cref="HackFileNotFoundException">The element is no longer in the trash.</exception>
    /// <exception cref="HackIOException">
    /// Something already exists at the destination ("already exists"), or the original location is
    /// unknown and no destination was given.
    /// </exception>
    public static async Task<string> RestoreAsync(
        HackTrashItem item, string? destinationPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        string name = ValidateName(item.Name);
        IAppExecutionContext context = HackExecutionContext.Current;
        IAppFileSystemGateway gateway = context.FileSystem;
        TrashLayout layout = GetLayout(context);

        VirtualPath source = VirtualPath.Parse(layout.FilesDirectory + "/" + name);
        _ = await HackFileSystemCore.TryStatAsync(
                gateway, source, FileSystemLinkBehavior.NoFollow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false)
            ?? throw new HackFileNotFoundException(source.Value);

        string? target = destinationPath;
        if (target is null)
        {
            // The .trashinfo on disk is authoritative; the record the caller holds may be stale.
            HackTrashInfo? info = await ReadInfoAsync(gateway, layout, name, cancellationToken).ConfigureAwait(false);
            target = info?.OriginalPath ?? item.OriginalPath
                ?? throw new HackIOException(
                    $"Cannot restore '{name}': its original location is unknown; give a destination.",
                    FileSystemErrorCode.NotFound,
                    source.Value);
        }

        VirtualPath destination = HackPath.Resolve(target, nameof(destinationPath));
        if (IsInside(destination.Value, layout.Root) || destination.Value == layout.Root)
        {
            throw new HackIOException(
                $"Cannot restore '{name}' into the trash itself.", FileSystemErrorCode.InvalidPath, destination.Value);
        }

        if (await HackFileSystemCore.TryStatAsync(
                gateway, destination, FileSystemLinkBehavior.NoFollow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new HackIOException(
                $"Cannot restore '{name}': '{destination.Value}' already exists.", FileSystemErrorCode.AlreadyExists, destination.Value);
        }

        if (HackPath.GetDirectoryName(destination.Value) is { } parent)
        {
            await HackDirectory.CreateDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
        }

        await MoveAsync(gateway, source, destination, cancellationToken).ConfigureAwait(false);
        await TryDeleteAsync(gateway, InfoPath(layout, name), cancellationToken).ConfigureAwait(false);

        Log(context, $"Restored '{name}' from the trash to '{destination.Value}'.");
        return destination.Value;
    }

    /// <summary>Deletes one trashed element permanently, with its <c>.trashinfo</c>.</summary>
    /// <exception cref="HackFileNotFoundException">Neither the element nor its metadata exists.</exception>
    public static async Task DeleteAsync(HackTrashItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        string name = ValidateName(item.Name);
        IAppExecutionContext context = HackExecutionContext.Current;
        IAppFileSystemGateway gateway = context.FileSystem;
        TrashLayout layout = GetLayout(context);

        VirtualPath element = VirtualPath.Parse(layout.FilesDirectory + "/" + name);
        bool deletedElement = await DeleteIfExistsAsync(gateway, element, cancellationToken).ConfigureAwait(false);
        bool deletedInfo = await DeleteIfExistsAsync(gateway, InfoPath(layout, name), cancellationToken).ConfigureAwait(false);
        if (!deletedElement && !deletedInfo)
        {
            throw new HackFileNotFoundException(element.Value);
        }

        Log(context, $"Permanently deleted '{name}' from the trash.");
    }

    /// <summary>Deletes every element of the trash permanently. An absent trash is already empty.</summary>
    public static async Task EmptyAsync(CancellationToken cancellationToken = default)
    {
        IAppExecutionContext context = HackExecutionContext.Current;
        IAppFileSystemGateway gateway = context.FileSystem;
        TrashLayout layout = GetLayout(context);

        Dictionary<string, FileSystemEntryMetadata> files = await ListAsync(gateway, layout.FilesDirectory, cancellationToken).ConfigureAwait(false);
        Dictionary<string, FileSystemEntryMetadata> infos = await ListAsync(gateway, layout.InfoDirectory, cancellationToken).ConfigureAwait(false);

        foreach (string name in files.Keys)
        {
            await DeleteIfExistsAsync(gateway, VirtualPath.Parse(layout.FilesDirectory + "/" + name), cancellationToken).ConfigureAwait(false);
        }

        foreach (string name in infos.Keys)
        {
            await DeleteIfExistsAsync(gateway, VirtualPath.Parse(layout.InfoDirectory + "/" + name), cancellationToken).ConfigureAwait(false);
        }

        Log(context, $"Emptied the trash ({files.Count} element(s) permanently deleted).");
    }

    /// <summary>Paths of one user's trash.</summary>
    private sealed record TrashLayout(string Home, string Root)
    {
        public string FilesDirectory => Root + "/files";

        public string InfoDirectory => Root + "/info";
    }

    private static TrashLayout GetLayout(IAppExecutionContext context)
    {
        string home = context.IsRootElevated ? RootHome : VirtualPath.Parse(context.HomeDirectory).Value;
        return new TrashLayout(home, (home == "/" ? string.Empty : home) + "/" + TrashRelativePath);
    }

    private static VirtualPath InfoPath(TrashLayout layout, string name) =>
        VirtualPath.Parse(layout.InfoDirectory + "/" + name + InfoExtension);

    /// <summary>Returns whether <paramref name="path"/> is strictly below <paramref name="directory"/>.</summary>
    private static bool IsInside(string path, string directory) =>
        path.StartsWith(directory == "/" ? "/" : directory + "/", StringComparison.Ordinal) && path != directory;

    /// <summary>Refuses <c>/</c>, the home directories and anything that is or contains the trash (ADR 0052, section 2).</summary>
    private static void EnsureMayBeTrashed(VirtualPath source, IAppExecutionContext context, TrashLayout layout)
    {
        string path = source.Value;
        string? reason =
            path == "/" ? "it is the root directory" :
            path == layout.Home || path == VirtualPath.Parse(context.HomeDirectory).Value ? "it is a home directory" :
            path == layout.Root || IsInside(layout.Root, path) ? "it contains the trash" :
            null;
        if (reason is not null)
        {
            throw new HackIOException($"Cannot move '{path}' to trash: {reason}.", FileSystemErrorCode.ProtectedEntry, path);
        }
    }

    private static async Task EnsureTrashDirectoriesAsync(IAppFileSystemGateway gateway, TrashLayout layout, CancellationToken cancellationToken)
    {
        // /root is private on Linux; create it 0700 if the first sudo deletion is what brings it to life.
        if (layout.Home == RootHome)
        {
            FileSystemMutationResult home = await gateway.CreateAsync(
                new FileSystemCreateRequest(VirtualPath.Parse(RootHome), FileSystemEntryKind.Directory, TrashDirectoryMode),
                cancellationToken).ConfigureAwait(false);
            if (!home.Succeeded && home.Transaction.Error!.Code != FileSystemErrorCode.AlreadyExists)
            {
                throw HackFileSystemErrorTranslator.Translate(home.Transaction.Error, HackEntryTarget.Directory, RootHome);
            }
        }

        // ~/.local/share is a normal XDG directory (0755); the trash itself is private (0700).
        await HackDirectory.CreateDirectoryAsync(HackPath.GetDirectoryName(layout.Root)!, cancellationToken).ConfigureAwait(false);
        foreach (string directory in (string[])[layout.Root, layout.FilesDirectory, layout.InfoDirectory])
        {
            FileSystemMutationResult created = await gateway.CreateAsync(
                new FileSystemCreateRequest(VirtualPath.Parse(directory), FileSystemEntryKind.Directory, TrashDirectoryMode),
                cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded && created.Transaction.Error!.Code != FileSystemErrorCode.AlreadyExists)
            {
                throw HackFileSystemErrorTranslator.Translate(created.Transaction.Error, HackEntryTarget.Directory, directory);
            }
        }
    }

    /// <summary>
    /// Reserves a free name: tries <c>name</c>, then <c>name.2.ext</c>, <c>name.3.ext</c>... and creates
    /// its <c>.trashinfo</c> exclusively, so two concurrent deletions never pick the same name.
    /// </summary>
    private static async Task<(string Name, VirtualPath InfoPath)> ReserveNameAsync(
        IAppFileSystemGateway gateway, TrashLayout layout, string originalName, string infoContent, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaximumNameAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidate = HackTrashInfo.CandidateName(originalName, attempt);

            // A leftover element without metadata also holds its name.
            if (await HackFileSystemCore.TryStatAsync(
                    gateway, VirtualPath.Parse(layout.FilesDirectory + "/" + candidate), FileSystemLinkBehavior.NoFollow,
                    HackEntryTarget.File, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            VirtualPath infoPath = InfoPath(layout, candidate);
            FileSystemMutationResult created = await gateway.CreateAsync(
                new FileSystemCreateRequest(infoPath, FileSystemEntryKind.File, InfoFileMode), cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                if (created.Transaction.Error!.Code == FileSystemErrorCode.AlreadyExists)
                {
                    continue;
                }

                throw HackFileSystemErrorTranslator.Translate(created.Transaction.Error, HackEntryTarget.File, infoPath.Value);
            }

            FileSystemMutationResult written = await gateway.WriteAsync(
                new FileSystemWriteRequest(infoPath, created.Entry!.Metadata.Revision),
                new HackMemoryContentSource(System.Text.Encoding.UTF8.GetBytes(infoContent), FileSystemContentDescriptor.Text()),
                cancellationToken).ConfigureAwait(false);
            if (!written.Succeeded)
            {
                await TryDeleteAsync(gateway, infoPath, CancellationToken.None).ConfigureAwait(false);
                throw HackFileSystemErrorTranslator.Translate(written.Transaction.Error!, HackEntryTarget.File, infoPath.Value);
            }

            return (candidate, infoPath);
        }

        throw new HackIOException(
            $"Cannot move '{originalName}' to trash: no free name left.", FileSystemErrorCode.AlreadyExists, layout.FilesDirectory);
    }

    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="destination"/> within one mount. Unlike
    /// <see cref="HackFileSystemCore.MoveEntryAsync"/>, a move across mounts is refused, never copied:
    /// a virtual file (<c>/proc</c>, <c>/dev</c>) has nothing to keep.
    /// </summary>
    private static async Task MoveAsync(
        IAppFileSystemGateway gateway, VirtualPath source, VirtualPath destination, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot entry = await HackFileSystemCore.StatAsync(
                gateway, source, FileSystemLinkBehavior.NoFollow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
            FileSystemEntrySnapshot sourceParent = await HackFileSystemCore.RequireParentAsync(gateway, source, cancellationToken).ConfigureAwait(false);
            FileSystemEntrySnapshot destinationParent = await HackFileSystemCore.RequireParentAsync(gateway, destination, cancellationToken).ConfigureAwait(false);

            FileSystemMutationResult result = await gateway.MoveAsync(
                new FileSystemMoveRequest(
                    source,
                    destination,
                    entry.Metadata.Revision,
                    sourceParent.Metadata.Revision,
                    destinationParent.Metadata.Revision),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return;
            }

            FileSystemError error = result.Transaction.Error!;
            switch (error.Code)
            {
                case FileSystemErrorCode.CrossMountOperation:
                    throw new HackIOException("Cannot move to trash: virtual file system", error.Code, source.Value);
                case FileSystemErrorCode.RevisionConflict when attempt < 2:
                    continue;
                default:
                    throw HackFileSystemErrorTranslator.Translate(error, HackEntryTarget.File, source.Value);
            }
        }
    }

    /// <summary>Lists a trash subdirectory by name; empty when it does not exist.</summary>
    private static async Task<Dictionary<string, FileSystemEntryMetadata>> ListAsync(
        IAppFileSystemGateway gateway, string directory, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> listing = await gateway.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse(directory)), cancellationToken).ConfigureAwait(false);
        if (!listing.Succeeded)
        {
            return listing.Error!.Code == FileSystemErrorCode.NotFound
                ? new Dictionary<string, FileSystemEntryMetadata>(StringComparer.Ordinal)
                : throw HackFileSystemErrorTranslator.Translate(listing.Error, HackEntryTarget.Directory, directory);
        }

        return listing.Value!.Entries.ToDictionary(item => item.Name.Value, item => item.Metadata, StringComparer.Ordinal);
    }

    private static async Task<HackTrashInfo?> ReadInfoAsync(
        IAppFileSystemGateway gateway, TrashLayout layout, string name, CancellationToken cancellationToken)
    {
        HackFileContent? content = await HackFileSystemCore.TryReadAllAsync(gateway, InfoPath(layout, name), cancellationToken)
            .ConfigureAwait(false);
        return content is null ? null : HackTrashInfo.Parse(System.Text.Encoding.UTF8.GetString(content.Bytes));
    }

    /// <summary>Size of a file, or the sum of every file below a directory (links count their own size).</summary>
    private static async Task<long> MeasureAsync(
        IAppFileSystemGateway gateway, VirtualPath path, FileSystemEntryMetadata metadata, CancellationToken cancellationToken)
    {
        switch (metadata)
        {
            case FileMetadata file:
                return file.Length;
            case SymbolicLinkMetadata link:
                return link.Length;
            case DirectoryMetadata:
                long total = 0;
                await foreach (HackEntry entry in HackFileSystemCore.EnumerateAsync(
                                   gateway, path, "*", SearchOption.AllDirectories, includeFiles: true, includeDirectories: false, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    total += entry.Metadata switch
                    {
                        FileMetadata file => file.Length,
                        SymbolicLinkMetadata link => link.Length,
                        _ => 0
                    };
                }

                return total;
            default:
                return 0;
        }
    }

    private static async Task<bool> DeleteIfExistsAsync(IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken) =>
        await HackFileSystemCore.DeleteEntryAsync(gateway, path, recursive: true, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);

    /// <summary>Best-effort cleanup that never hides the error being reported.</summary>
    private static async Task TryDeleteAsync(IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteIfExistsAsync(gateway, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Left as an orphan; GetItemsAsync cleans it up later.
        }
    }

    /// <summary>The trash name is a single path segment; anything else would escape <c>files/</c>.</summary>
    private static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/'))
        {
            throw new ArgumentException($"'{name}' is not a trash element name.", nameof(name));
        }

        return name;
    }

    /// <summary>"Current local time" of the simulated clock in the app's time zone (ADR 0193), as written in <c>DeletionDate</c>.</summary>
    private static DateTimeOffset LocalNow(IAppExecutionContext context) =>
        TimeZoneInfo.ConvertTime(context.Clock.UtcNow, context.TimeZone);

    private static void Log(IAppExecutionContext context, string message) =>
        context.Logging.Log(DiagnosticSeverity.Information, message);
}
