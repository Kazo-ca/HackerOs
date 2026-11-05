using System.Text;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.Settings;

/// <summary>Outcome of reading one settings file.</summary>
/// <param name="Content">File text, or <see langword="null"/> when the file does not exist.</param>
/// <param name="Revision">File revision, or <see langword="null"/> when the file does not exist.</param>
/// <param name="Error">
/// Error other than "not found" (for example a permission denial), or <see langword="null"/>.
/// A missing file is not an error: it is an empty layer (ADR 0044 section 2).
/// </param>
public sealed record SettingsFileReadResult(string? Content, long? Revision, FileSystemError? Error)
{
    /// <summary>Gets whether the file exists and was read.</summary>
    public bool Exists => Content is not null;

    /// <summary>
    /// Gets whether the read was refused by a permission check (mode bits, capability, authority):
    /// for a private settings file, the reader sees its keys masked (ADR 0100 section 1).
    /// </summary>
    public bool IsDenied => Error?.Code is FileSystemErrorCode.PermissionDenied
        or FileSystemErrorCode.CapabilityDenied
        or FileSystemErrorCode.AuthorityDenied
        or FileSystemErrorCode.ProtectedEntry;
}

/// <summary>
/// Reads and edits settings files through an authorized <see cref="IAppFileSystemGateway"/>, so
/// capabilities, authority, mode bits and elevation tickets apply exactly as for any other file
/// (ADR 0044 section 1). Shared by the SDK (<c>KazoSettings</c>) and the platform
/// (<c>OsSettingsService</c>, the migration).
/// </summary>
/// <remarks>
/// Writes follow the same rule as the <c>Kazo*</c> file API of ADR 0043: read the current revision,
/// apply the edit to the current content, write with that revision as precondition, and retry
/// exactly once on a revision conflict. Because the edit is re-applied to fresh content, a
/// concurrent <c>nano</c> change to another key is never lost.
/// </remarks>
public static class SettingsTextFile
{
    /// <summary>Default mode of a created settings file (<c>0644</c>, umask 022).</summary>
    public const ushort DefaultFileMode = 0b110_100_100;

    /// <summary>Default mode of a created settings directory (<c>0755</c>, umask 022).</summary>
    public const ushort DefaultDirectoryMode = 0b111_101_101;

    /// <summary>Reads a UTF-8 settings file.</summary>
    /// <param name="fileSystem">Authorized gateway of the acting app/user.</param>
    /// <param name="path">Absolute file path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<SettingsFileReadResult> ReadAsync(
        IAppFileSystemGateway fileSystem,
        VirtualPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        FileSystemResult<FileSystemContentReadHandle> read = await fileSystem
            .ReadAsync(new FileSystemReadRequest(path), cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded || read.Value is null)
        {
            return read.Error?.Code == FileSystemErrorCode.NotFound
                ? new SettingsFileReadResult(null, null, null)
                : new SettingsFileReadResult(null, null, read.Error);
        }

        await using FileSystemContentReadHandle handle = read.Value;
        using StreamReader reader = new(handle.Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return new SettingsFileReadResult(content, handle.Entry.Metadata.Revision, null);
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to the file's current content and writes the result,
    /// creating missing parent directories (<c>0755</c>) and the file (<c>0644</c>) when needed.
    /// </summary>
    /// <param name="fileSystem">Authorized gateway of the acting app/user.</param>
    /// <param name="path">Absolute file path.</param>
    /// <param name="edit">
    /// Receives the current content (<see langword="null"/> when the file does not exist) and
    /// returns the new content. Called once per attempt, so at most twice.
    /// </param>
    /// <param name="createIfMissing">
    /// When <see langword="false"/>, a missing file is not created and the call fails with
    /// <see cref="FileSystemErrorCode.NotFound"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="null"/> on success, otherwise the error that stopped the write.</returns>
    public static ValueTask<FileSystemError?> EditAsync(
        IAppFileSystemGateway fileSystem,
        VirtualPath path,
        Func<string?, string> edit,
        bool createIfMissing = true,
        CancellationToken cancellationToken = default) =>
        EditAsync(fileSystem, path, edit, createIfMissing, DefaultFileMode, cancellationToken);

    /// <summary>
    /// Same as <see cref="EditAsync(IAppFileSystemGateway, VirtualPath, Func{string?, string}, bool, CancellationToken)"/>,
    /// creating a missing file with <paramref name="fileMode"/>: <see cref="SettingsPrivateFiles.PrivateFileMode"/>
    /// (<c>0600</c>) for a private settings file (ADR 0100 section 1).
    /// </summary>
    /// <param name="fileSystem">Authorized gateway of the acting app/user.</param>
    /// <param name="path">Absolute file path.</param>
    /// <param name="edit">Receives the current content and returns the new content.</param>
    /// <param name="createIfMissing">When <see langword="false"/>, a missing file is not created.</param>
    /// <param name="fileMode">Unix mode of a created file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="null"/> on success, otherwise the error that stopped the write.</returns>
    public static async ValueTask<FileSystemError?> EditAsync(
        IAppFileSystemGateway fileSystem,
        VirtualPath path,
        Func<string?, string> edit,
        bool createIfMissing,
        ushort fileMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(edit);

        for (int attempt = 0; ; attempt++)
        {
            SettingsFileReadResult current = await ReadAsync(fileSystem, path, cancellationToken).ConfigureAwait(false);
            if (current.Error is not null)
            {
                return current.Error;
            }

            if (!current.Exists)
            {
                if (!createIfMissing)
                {
                    return new FileSystemError(FileSystemOperation.Write, FileSystemErrorCode.NotFound, path);
                }

                FileSystemError? created = await CreateFileAsync(fileSystem, path, fileMode, cancellationToken).ConfigureAwait(false);
                if (created is not null)
                {
                    return created;
                }
            }

            string next = edit(current.Content);
            FileSystemMutationResult write = await fileSystem.WriteAsync(
                    new FileSystemWriteRequest(path, current.Revision),
                    new Utf8TextSource(next),
                    cancellationToken)
                .ConfigureAwait(false);
            if (write.Succeeded)
            {
                return null;
            }

            FileSystemError error = write.Transaction.Error!;
            if (error.Code != FileSystemErrorCode.RevisionConflict || attempt >= 1)
            {
                return error;
            }
        }
    }

    /// <summary>Replaces the whole file content (used for the strict JSON documents of ADR 0044 section 8).</summary>
    /// <param name="fileSystem">Authorized gateway of the acting app/user.</param>
    /// <param name="path">Absolute file path.</param>
    /// <param name="content">New content.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="null"/> on success, otherwise the error that stopped the write.</returns>
    public static ValueTask<FileSystemError?> WriteAllAsync(
        IAppFileSystemGateway fileSystem,
        VirtualPath path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return EditAsync(fileSystem, path, _ => content, createIfMissing: true, cancellationToken);
    }

    /// <summary>Creates <paramref name="path"/> and any missing ancestors as <c>0755</c> directories.</summary>
    /// <param name="fileSystem">Authorized gateway of the acting app/user.</param>
    /// <param name="path">Absolute directory path.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="null"/> on success, otherwise the first error.</returns>
    public static async ValueTask<FileSystemError?> EnsureDirectoryAsync(
        IAppFileSystemGateway fileSystem,
        VirtualPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return await EnsureDirectoryCoreAsync(fileSystem, path, isAncestor: false, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<FileSystemError?> EnsureDirectoryCoreAsync(
        IAppFileSystemGateway fileSystem,
        VirtualPath path,
        bool isAncestor,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await fileSystem
            .StatAsync(new FileSystemStatRequest(path), cancellationToken)
            .ConfigureAwait(false);
        if (stat.Succeeded)
        {
            return stat.Value!.Metadata.Kind == FileSystemEntryKind.Directory
                ? null
                : new FileSystemError(FileSystemOperation.Create, FileSystemErrorCode.NotDirectory, path);
        }

        if (stat.Error?.Code != FileSystemErrorCode.NotFound)
        {
            // An app may write ~/.config/<appId>/ (its private capability) without being allowed
            // to even look at ~/.config itself: an ancestor it cannot stat is assumed to exist,
            // and the create of the child below reports the real outcome.
            return isAncestor ? null : stat.Error;
        }

        VirtualPath parent = GetParent(path);
        if (parent.Value != path.Value)
        {
            FileSystemError? parentError = await EnsureDirectoryCoreAsync(fileSystem, parent, isAncestor: true, cancellationToken).ConfigureAwait(false);
            if (parentError is not null)
            {
                return parentError;
            }
        }

        FileSystemMutationResult created = await fileSystem.CreateAsync(
                new FileSystemCreateRequest(path, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(DefaultDirectoryMode)),
                cancellationToken)
            .ConfigureAwait(false);
        return created.Succeeded || created.Transaction.Error?.Code == FileSystemErrorCode.AlreadyExists
            ? null
            : created.Transaction.Error;
    }

    /// <summary>Gets the parent directory of an absolute path (the root is its own parent).</summary>
    /// <param name="path">Absolute path.</param>
    public static VirtualPath GetParent(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
    }

    private static async ValueTask<FileSystemError?> CreateFileAsync(
        IAppFileSystemGateway fileSystem,
        VirtualPath path,
        ushort fileMode,
        CancellationToken cancellationToken)
    {
        FileSystemError? directory = await EnsureDirectoryAsync(fileSystem, GetParent(path), cancellationToken).ConfigureAwait(false);
        if (directory is not null)
        {
            return directory;
        }

        FileSystemMutationResult created = await fileSystem.CreateAsync(
                new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(fileMode)),
                cancellationToken)
            .ConfigureAwait(false);
        return created.Succeeded || created.Transaction.Error?.Code == FileSystemErrorCode.AlreadyExists
            ? null
            : created.Transaction.Error;
    }

    /// <summary>In-memory UTF-8 text content published by one write.</summary>
    private sealed class Utf8TextSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);

        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

        public long? Length => _bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
    }
}
