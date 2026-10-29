using HackerOs.App.Abstractions;

namespace HackerOs.Simulation.Abstractions.FileSystem;

/// <summary>
/// A mount table that can change while the OS runs (ADR 0181, section 1): network drives are
/// mounted and unmounted after boot, while <c>/</c>, <c>/proc</c> and <c>/dev</c> stay fixed.
/// </summary>
public interface IDynamicFileSystemMountRouter : IFileSystemMountRouter
{
    /// <summary>Raised after a mount is added or removed.</summary>
    event Action? MountsChanged;

    /// <summary>Adds a mount at a path no other mount owns.</summary>
    /// <exception cref="InvalidOperationException">The path is <c>/</c>, already a mount, or inside a mount other than <c>/</c>.</exception>
    void Mount(FileSystemMount mount);

    /// <summary>Removes a mount added by <see cref="Mount"/>; <see langword="false"/> when none is at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidOperationException">The path is one of the fixed mounts.</exception>
    bool Unmount(VirtualPath path);
}

/// <summary>Kind of an entry of a folder of the host computer.</summary>
public enum HostFolderEntryKind
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A folder.</summary>
    Directory
}

/// <summary>One entry of a folder of the host computer, as the browser describes it.</summary>
/// <param name="Name">Name of the entry.</param>
/// <param name="Kind">File or folder.</param>
/// <param name="Length">Size of a file in bytes; 0 for a folder.</param>
/// <param name="LastModifiedUtc">Modification time of a file; <see langword="null"/> for a folder (the browser has none).</param>
public sealed record HostFolderEntry(string Name, HostFolderEntryKind Kind, long Length, DateTimeOffset? LastModifiedUtc);

/// <summary>Why an operation on a host folder failed, from the browser's <c>DOMException</c> name.</summary>
public enum HostFolderErrorKind
{
    /// <summary><c>NotFoundError</c>: nothing at the path.</summary>
    NotFound,

    /// <summary>An entry already has the name.</summary>
    AlreadyExists,

    /// <summary><c>TypeMismatchError</c>: a file where a folder was expected, or the reverse.</summary>
    TypeMismatch,

    /// <summary><c>InvalidModificationError</c>: a non-recursive delete of a folder that has entries.</summary>
    NotEmpty,

    /// <summary><c>NotAllowedError</c> or <c>SecurityError</c>: the browser's permission is missing.</summary>
    NotAllowed,

    /// <summary><c>QuotaExceededError</c>: the host disk is full.</summary>
    NoSpace,

    /// <summary>The host refuses the name (<c>TypeError</c>).</summary>
    InvalidName,

    /// <summary>Any other failure.</summary>
    Failure
}

/// <summary>A failed operation on a host folder.</summary>
public sealed class HostFolderException : IOException
{
    /// <summary>Initializes the exception.</summary>
    public HostFolderException(HostFolderErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Gets why the operation failed.</summary>
    public HostFolderErrorKind Kind { get; }
}

/// <summary>The browser's permission on a host folder (<c>PermissionState</c>).</summary>
public enum HostFolderPermission
{
    /// <summary>Read and write are allowed.</summary>
    Granted,

    /// <summary>The browser must ask the user again, after a user gesture.</summary>
    Prompt,

    /// <summary>The user refused.</summary>
    Denied
}

/// <summary>
/// A folder of the host computer the user chose (ADR 0181, section 2): a
/// <c>FileSystemDirectoryHandle</c> in the browser. Paths are segment lists relative to the folder;
/// an empty list is the folder itself.
/// </summary>
public interface IHostFolder : IAsyncDisposable
{
    /// <summary>Gets the name of the folder on the host computer.</summary>
    string Name { get; }

    /// <summary>Reads the browser's permission without asking the user.</summary>
    ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the user for read and write access; needs a recent user gesture.</summary>
    ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>Describes the entry at <paramref name="path"/>.</summary>
    /// <exception cref="HostFolderException">The entry is missing or unreadable.</exception>
    ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default);

    /// <summary>Lists the entries of the folder at <paramref name="path"/>, in any order.</summary>
    ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default);

    /// <summary>Reads the whole content of the file at <paramref name="path"/>.</summary>
    ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default);

    /// <summary>Creates an empty file or a folder; fails with <see cref="HostFolderErrorKind.AlreadyExists"/> when the name is taken.</summary>
    ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default);

    /// <summary>Replaces the content of the existing file at <paramref name="path"/>; the browser swaps it in on close.</summary>
    ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Deletes the entry at <paramref name="path"/>, with its content when <paramref name="recursive"/>.</summary>
    ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves or renames an entry in one browser call (<c>FileSystemHandle.move</c>);
    /// <see langword="false"/> when the browser cannot, and the caller copies then deletes.
    /// </summary>
    ValueTask<bool> TryMoveAsync(
        IReadOnlyList<string> source,
        IReadOnlyList<string> destination,
        CancellationToken cancellationToken = default);
}

/// <summary>A host folder the user just chose, with the key its handle is stored under.</summary>
/// <param name="Key">Key of the handle in the browser's handle store.</param>
/// <param name="Folder">The folder.</param>
public sealed record HostFolderPick(string Key, IHostFolder Folder);

/// <summary>
/// The browser's door to host folders (ADR 0181, sections 7 and 8): the folder dialog and the
/// IndexedDB store that keeps chosen folders across reloads.
/// </summary>
public interface IHostFolderAccess
{
    /// <summary>Gets whether the browser has <c>showDirectoryPicker</c>.</summary>
    ValueTask<bool> IsSupportedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the browser's folder dialog in read-write mode and stores the chosen folder;
    /// <see langword="null"/> when the user closed it.
    /// </summary>
    /// <exception cref="NotSupportedException">The browser has no folder dialog.</exception>
    /// <exception cref="InvalidOperationException">No recent user gesture.</exception>
    ValueTask<HostFolderPick?> PickAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a stored folder; <see langword="null"/> when the key is unknown.</summary>
    ValueTask<IHostFolder?> LoadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes a stored folder; nothing happens when the key is unknown.</summary>
    ValueTask ForgetAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// A host folder that can tell when its content changes on the host computer (ADR 0185 §15): the
/// browser's <c>FileSystemObserver</c>, where it exists. A folder without it is read again on a timer.
/// </summary>
public interface IObservableHostFolder
{
    /// <summary>
    /// Calls <paramref name="changed"/> whenever something changes in the folder at <paramref name="path"/>
    /// (its direct entries at least), until the returned handle is disposed.
    /// </summary>
    /// <param name="path">The folder, as segments relative to the host folder; empty for its root.</param>
    /// <param name="changed">Called after a change; it may be called from any context, more than once per change.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The handle that stops observing, or <see langword="null"/> when this browser cannot observe.</returns>
    ValueTask<IAsyncDisposable?> ObserveAsync(IReadOnlyList<string> path, Action changed, CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether the user can see HackerOS (ADR 0185 §15): a network drive is read again on a timer only
/// while the page is visible, and at once when the page comes back (shown again or focused).
/// </summary>
public interface IHostActivitySignal
{
    /// <summary>Gets whether the page is visible.</summary>
    bool IsVisible { get; }

    /// <summary>Raised when the page is shown again or gets the focus back.</summary>
    event Action? Resumed;
}
