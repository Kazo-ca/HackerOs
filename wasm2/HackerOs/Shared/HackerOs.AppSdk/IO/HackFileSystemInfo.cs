using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.IO;

/// <summary>
/// Snapshot of one virtual filesystem entry, shaped like <see cref="FileSystemInfo"/> (ADR 0043).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="FileSystemInfo"/>, nothing is fetched lazily: the values come from the snapshot
/// taken by <c>GetAsync</c>, an enumeration or the last <see cref="RefreshAsync"/>. An instance built
/// with its public constructor has no snapshot (<see cref="Exists"/> is <see langword="false"/>) until
/// <see cref="RefreshAsync"/> runs.
/// </para>
/// <para>
/// For a symbolic link, <see cref="IsSymbolicLink"/> and <see cref="LinkTarget"/> describe the link
/// itself while every other property describes what it points to, like <c>stat</c>.
/// </para>
/// </remarks>
public abstract class HackFileSystemInfo
{
    /// <summary>Value .NET reports for the time of a missing entry (1601-01-01 UTC).</summary>
    private static readonly DateTime MissingTime = DateTime.FromFileTimeUtc(0);

    private FileSystemEntryMetadata? _ownMetadata;
    private FileSystemEntryMetadata? _targetMetadata;

    /// <summary>Initializes an instance for a canonical path with an optional initial snapshot.</summary>
    /// <param name="fullPath">Canonical absolute path.</param>
    /// <param name="ownMetadata">The entry's own metadata (a link's own metadata for a symbolic link).</param>
    /// <param name="linkTarget">For a symbolic link, the metadata of what it points to.</param>
    private protected HackFileSystemInfo(string fullPath, FileSystemEntryMetadata? ownMetadata, FileSystemEntryMetadata? linkTarget)
    {
        FullName = fullPath;
        _ownMetadata = ownMetadata;
        _targetMetadata = linkTarget;
    }

    /// <summary>Gets the canonical absolute path.</summary>
    public string FullName { get; private protected set; }

    /// <summary>Gets the last path segment (<c>/</c> for the root).</summary>
    public virtual string Name => FullName == "/" ? "/" : HackPath.GetFileName(FullName);

    /// <summary>Gets the extension, including its leading dot, or an empty string.</summary>
    public string Extension => HackPath.GetExtension(FullName);

    /// <summary>Gets whether the snapshot found an entry of the expected kind (file for <see cref="HackFileInfo"/>, directory for <see cref="HackDirectoryInfo"/>).</summary>
    public abstract bool Exists { get; }

    /// <summary>Gets the creation time in UTC, or 1601-01-01 UTC when the entry does not exist.</summary>
    public DateTime CreationTimeUtc => Effective?.Timestamps.CreatedAtUtc.UtcDateTime ?? MissingTime;

    /// <summary>Gets the last content modification time in UTC, or 1601-01-01 UTC when the entry does not exist.</summary>
    public DateTime LastWriteTimeUtc => Effective?.Timestamps.ContentModifiedAtUtc.UtcDateTime ?? MissingTime;

    /// <summary>Gets the permission bits, or <c>(UnixFileMode)(-1)</c> when the entry does not exist (as .NET reports).</summary>
    public UnixFileMode UnixFileMode => Effective is { } metadata
        ? HackUnixFileMode.FromPermissions(metadata.Permissions)
        : (UnixFileMode)(-1);

    /// <summary>Gets the owning user identity, or <see langword="null"/> when the entry does not exist.</summary>
    public string? Owner => Effective?.OwnerId;

    /// <summary>Gets the owning group identity, or <see langword="null"/> when the entry does not exist.</summary>
    public string? Group => Effective?.GroupId;

    /// <summary>
    /// Gets the engine's optimistic revision of the entry (of the link target for a symbolic link), or
    /// <see langword="null"/> when it does not exist. Pass it as <see cref="HackWriteOptions.ExpectedRevision"/>
    /// for a strict write.
    /// </summary>
    public long? Revision => Effective?.Revision;

    /// <summary>Gets whether the entry itself is a symbolic link.</summary>
    public bool IsSymbolicLink => _ownMetadata?.Kind == FileSystemEntryKind.SymbolicLink;

    /// <summary>Gets the link text when the entry is a symbolic link, otherwise <see langword="null"/>.</summary>
    public string? LinkTarget => (_ownMetadata as SymbolicLinkMetadata)?.Target;

    /// <summary>Gets the metadata properties describe: the link target for a link, otherwise the entry itself.</summary>
    private protected FileSystemEntryMetadata? Effective =>
        _ownMetadata?.Kind == FileSystemEntryKind.SymbolicLink ? _targetMetadata : _ownMetadata;

    /// <summary>Re-reads the snapshot from the filesystem as the ambient app.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        (FileSystemEntryMetadata? own, FileSystemEntryMetadata? target) =
            await LoadAsync(HackFileSystemCore.Gateway, VirtualPath.Parse(FullName), cancellationToken).ConfigureAwait(false);
        _ownMetadata = own;
        _targetMetadata = target;
    }

    /// <summary>Returns the full path.</summary>
    public override string ToString() => FullName;

    /// <summary>Reads an entry's own metadata and, for a link, its target's metadata (<see langword="null"/>s when missing).</summary>
    private protected static async ValueTask<(FileSystemEntryMetadata? Own, FileSystemEntryMetadata? Target)> LoadAsync(
        IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemEntrySnapshot? own = await HackFileSystemCore.TryStatAsync(
            gateway, path, FileSystemLinkBehavior.NoFollow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
        if (own?.Metadata.Kind != FileSystemEntryKind.SymbolicLink)
        {
            return (own?.Metadata, null);
        }

        FileSystemResult<FileSystemEntrySnapshot> followed =
            await gateway.StatAsync(new FileSystemStatRequest(path, FileSystemLinkBehavior.Follow), cancellationToken).ConfigureAwait(false);
        return (own.Metadata, followed.Succeeded ? followed.Value!.Metadata : null);
    }
}

/// <summary>Snapshot of one regular file, shaped like <see cref="FileInfo"/> (ADR 0043).</summary>
public sealed class HackFileInfo : HackFileSystemInfo
{
    /// <summary>
    /// Creates an instance for <paramref name="fileName"/> (resolved against the ambient context)
    /// <b>without</b> reading it: call <see cref="HackFileSystemInfo.RefreshAsync"/>, or prefer <see cref="GetAsync"/>.
    /// </summary>
    public HackFileInfo(string fileName)
        : base(HackPath.Resolve(fileName, nameof(fileName)).Value, ownMetadata: null, linkTarget: null)
    {
    }

    internal HackFileInfo(string fullPath, FileSystemEntryMetadata? ownMetadata, FileSystemEntryMetadata? linkTarget)
        : base(fullPath, ownMetadata, linkTarget)
    {
    }

    /// <summary>Resolves <paramref name="fileName"/> and returns it with an initial snapshot.</summary>
    public static async ValueTask<HackFileInfo> GetAsync(string fileName, CancellationToken cancellationToken = default)
    {
        VirtualPath path = HackPath.Resolve(fileName, nameof(fileName));
        (FileSystemEntryMetadata? own, FileSystemEntryMetadata? target) =
            await LoadAsync(HackFileSystemCore.Gateway, path, cancellationToken).ConfigureAwait(false);
        return new HackFileInfo(path.Value, own, target);
    }

    /// <inheritdoc />
    public override bool Exists => Effective is { Kind: not FileSystemEntryKind.Directory };

    /// <summary>Gets the size in bytes.</summary>
    /// <exception cref="HackFileNotFoundException">The file did not exist when the snapshot was taken.</exception>
    public long Length => Effective switch
    {
        FileMetadata file => file.Length,
        { Kind: not FileSystemEntryKind.Directory } => 0,
        _ => throw new HackFileNotFoundException(FullName)
    };

    /// <summary>Gets the parent directory path (<see langword="null"/> only for the root).</summary>
    public string? DirectoryName => HackPath.GetDirectoryName(FullName);

    /// <summary>Gets the parent directory, without reading it.</summary>
    public HackDirectoryInfo? Directory => DirectoryName is { } parent ? new HackDirectoryInfo(parent, null, null) : null;

    /// <summary>Opens the file for reading.</summary>
    public Task<HackFileStream> OpenReadAsync(CancellationToken cancellationToken = default) =>
        HackFile.OpenReadAsync(FullName, cancellationToken);

    /// <summary>Opens the file for replacement writing (see <see cref="HackFile.OpenWriteAsync(string, CancellationToken)"/>).</summary>
    public Task<HackFileStream> OpenWriteAsync(HackWriteOptions? options = null, CancellationToken cancellationToken = default) =>
        HackFile.OpenWriteAsync(FullName, options, cancellationToken);

    /// <summary>Copies the file and returns the destination with a fresh snapshot.</summary>
    public async Task<HackFileInfo> CopyToAsync(string destFileName, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        string destination = HackPath.GetFullPath(destFileName);
        await HackFile.CopyAsync(FullName, destination, overwrite, cancellationToken).ConfigureAwait(false);
        return await GetAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves the file; this instance then points at the destination with a fresh snapshot.</summary>
    public async Task MoveToAsync(string destFileName, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        string destination = HackPath.GetFullPath(destFileName);
        await HackFile.MoveAsync(FullName, destination, overwrite, cancellationToken).ConfigureAwait(false);
        FullName = destination;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Permanently deletes the file and refreshes the snapshot.</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await HackFile.DeleteAsync(FullName, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Snapshot of one directory, shaped like <see cref="DirectoryInfo"/> (ADR 0043).</summary>
public sealed class HackDirectoryInfo : HackFileSystemInfo
{
    /// <summary>
    /// Creates an instance for <paramref name="path"/> (resolved against the ambient context)
    /// <b>without</b> reading it: call <see cref="HackFileSystemInfo.RefreshAsync"/>, or prefer <see cref="GetAsync"/>.
    /// </summary>
    public HackDirectoryInfo(string path)
        : base(HackPath.Resolve(path, nameof(path)).Value, ownMetadata: null, linkTarget: null)
    {
    }

    internal HackDirectoryInfo(string fullPath, FileSystemEntryMetadata? ownMetadata, FileSystemEntryMetadata? linkTarget)
        : base(fullPath, ownMetadata, linkTarget)
    {
    }

    /// <summary>Resolves <paramref name="path"/> and returns it with an initial snapshot.</summary>
    public static async ValueTask<HackDirectoryInfo> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        (FileSystemEntryMetadata? own, FileSystemEntryMetadata? target) =
            await LoadAsync(HackFileSystemCore.Gateway, resolved, cancellationToken).ConfigureAwait(false);
        return new HackDirectoryInfo(resolved.Value, own, target);
    }

    /// <inheritdoc />
    public override bool Exists => Effective is { Kind: FileSystemEntryKind.Directory };

    /// <summary>Gets the parent directory, without reading it (<see langword="null"/> for the root).</summary>
    public HackDirectoryInfo? Parent => HackPath.GetDirectoryName(FullName) is { } parent ? new HackDirectoryInfo(parent, null, null) : null;

    /// <summary>Gets the root directory.</summary>
    public HackDirectoryInfo Root => new("/", null, null);

    /// <summary>Gets the total size of this folder's whole content (ADR 0185 §5); see <see cref="HackDirectory.GetTotalSizeAsync"/>.</summary>
    public Task<HackDirectorySize> GetTotalSizeAsync(CancellationToken cancellationToken = default) =>
        HackDirectory.GetTotalSizeAsync(FullName, cancellationToken);

    /// <summary>Creates the directory and any missing parent, then refreshes the snapshot.</summary>
    public async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        await HackDirectory.CreateDirectoryAsync(FullName, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a subdirectory (and missing parents) and returns it with a fresh snapshot.</summary>
    public Task<HackDirectoryInfo> CreateSubdirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        if (HackPath.IsPathRooted(path))
        {
            throw new ArgumentException("A subdirectory path must be relative.", nameof(path));
        }

        return HackDirectory.CreateDirectoryAsync(HackPath.GetFullPath(path, FullName), cancellationToken);
    }

    /// <summary>Lists matching files (snapshots come from the enumeration, no extra read per entry).</summary>
    public async Task<HackFileInfo[]> GetFilesAsync(
        string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly, CancellationToken cancellationToken = default)
    {
        List<HackFileInfo> files = [];
        await foreach (HackEntry entry in Enumerate(searchPattern, searchOption, includeFiles: true, includeDirectories: false, cancellationToken).ConfigureAwait(false))
        {
            files.Add(new HackFileInfo(entry.FullPath, entry.Metadata, entry.LinkTarget));
        }

        return [.. files];
    }

    /// <summary>Lists matching subdirectories.</summary>
    public async Task<HackDirectoryInfo[]> GetDirectoriesAsync(
        string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly, CancellationToken cancellationToken = default)
    {
        List<HackDirectoryInfo> directories = [];
        await foreach (HackEntry entry in Enumerate(searchPattern, searchOption, includeFiles: false, includeDirectories: true, cancellationToken).ConfigureAwait(false))
        {
            directories.Add(new HackDirectoryInfo(entry.FullPath, entry.Metadata, entry.LinkTarget));
        }

        return [.. directories];
    }

    /// <summary>Lists matching files and subdirectories.</summary>
    public async Task<HackFileSystemInfo[]> GetFileSystemInfosAsync(
        string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly, CancellationToken cancellationToken = default)
    {
        List<HackFileSystemInfo> infos = [];
        await foreach (HackEntry entry in Enumerate(searchPattern, searchOption, includeFiles: true, includeDirectories: true, cancellationToken).ConfigureAwait(false))
        {
            infos.Add(entry.IsDirectory
                ? new HackDirectoryInfo(entry.FullPath, entry.Metadata, entry.LinkTarget)
                : new HackFileInfo(entry.FullPath, entry.Metadata, entry.LinkTarget));
        }

        return [.. infos];
    }

    /// <summary>Moves the directory; this instance then points at the destination with a fresh snapshot.</summary>
    public async Task MoveToAsync(string destDirName, CancellationToken cancellationToken = default)
    {
        string destination = HackPath.GetFullPath(destDirName);
        await HackDirectory.MoveAsync(FullName, destination, cancellationToken).ConfigureAwait(false);
        FullName = destination;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the directory (its whole subtree when <paramref name="recursive"/>) and refreshes the snapshot.</summary>
    public async Task DeleteAsync(bool recursive = false, CancellationToken cancellationToken = default)
    {
        await HackDirectory.DeleteAsync(FullName, recursive, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private IAsyncEnumerable<HackEntry> Enumerate(
        string searchPattern, SearchOption searchOption, bool includeFiles, bool includeDirectories, CancellationToken cancellationToken)
    {
        HackPathPattern.Validate(searchPattern, nameof(searchPattern));
        return HackFileSystemCore.EnumerateAsync(
            HackFileSystemCore.Gateway, VirtualPath.Parse(FullName), searchPattern, searchOption, includeFiles, includeDirectories, cancellationToken);
    }
}
