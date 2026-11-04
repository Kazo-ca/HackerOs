using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.IO;

/// <summary>
/// Static file operations on the KazoOS virtual filesystem, shaped like <see cref="File"/>
/// (ADR 0043). Paths may be absolute, relative to <see cref="HackExecutionContext.WorkingDirectory"/>,
/// or start with <c>~</c>. Every call runs as the ambient app (<see cref="HackExecutionContext.Current"/>)
/// through its capability-checked gateway.
/// </summary>
/// <remarks>
/// <para>Everything is asynchronous: the backing store (IndexedDB) is asynchronous and the browser has one thread.</para>
/// <para>
/// Writes create a missing file with the phase 1 umask (<c>0644</c>), commit with one atomic engine
/// write and retry once on a concurrent change. Overloads taking <see cref="HackWriteOptions"/>
/// give strict conflict control and return the committed <see cref="HackFileInfo"/>.
/// </para>
/// <para>Text is UTF-8 without a byte-order mark unless another encoding is passed; lines end with <c>\n</c>.</para>
/// </remarks>
public static class HackFile
{
    private const string NewLine = "\n";
    private static readonly UTF8Encoding DefaultEncoding = new(encoderShouldEmitUTF8Identifier: false);

    // ----- Read -----

    /// <summary>Reads a whole text file, detecting a byte-order mark (UTF-8 otherwise).</summary>
    public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAllTextAsync(path, encoding: null, cancellationToken);

    /// <summary>Reads a whole text file with <paramref name="encoding"/> (a byte-order mark still wins, like <see cref="File.ReadAllTextAsync(string, Encoding, CancellationToken)"/>).</summary>
    public static async Task<string> ReadAllTextAsync(string path, Encoding? encoding, CancellationToken cancellationToken = default)
    {
        using StreamReader reader = await OpenTextReaderAsync(path, encoding, cancellationToken).ConfigureAwait(false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads every line of a text file.</summary>
    public static Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAllLinesAsync(path, encoding: null, cancellationToken);

    /// <summary>Reads every line of a text file with <paramref name="encoding"/>.</summary>
    public static async Task<string[]> ReadAllLinesAsync(string path, Encoding? encoding, CancellationToken cancellationToken = default)
    {
        using StreamReader reader = await OpenTextReaderAsync(path, encoding, cancellationToken).ConfigureAwait(false);
        List<string> lines = [];
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    /// <summary>Reads a whole file as bytes.</summary>
    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        HackFileContent content = await HackFileSystemCore.ReadAllAsync(HackFileSystemCore.Gateway, resolved, cancellationToken).ConfigureAwait(false);
        return content.Bytes;
    }

    // ----- Write (replace) -----

    /// <summary>Creates or replaces a text file (UTF-8 without byte-order mark).</summary>
    public static Task WriteAllTextAsync(string path, string? contents, CancellationToken cancellationToken = default) =>
        WriteAllTextAsync(path, contents, encoding: null, options: null, cancellationToken);

    /// <summary>Creates or replaces a text file with <paramref name="encoding"/> (its preamble is written, like <c>File.WriteAllTextAsync</c>).</summary>
    public static Task WriteAllTextAsync(string path, string? contents, Encoding? encoding, CancellationToken cancellationToken = default) =>
        WriteAllTextAsync(path, contents, encoding, options: null, cancellationToken);

    /// <summary>Creates or replaces a text file with advanced options and returns the committed file.</summary>
    public static Task<HackFileInfo> WriteAllTextAsync(string path, string? contents, HackWriteOptions options, CancellationToken cancellationToken = default) =>
        WriteAllTextAsync(path, contents, encoding: null, options, cancellationToken);

    /// <summary>Creates or replaces a text file with <paramref name="encoding"/> and advanced options; returns the committed file.</summary>
    public static Task<HackFileInfo> WriteAllTextAsync(
        string path, string? contents, Encoding? encoding, HackWriteOptions? options, CancellationToken cancellationToken = default)
    {
        Encoding effective = encoding ?? DefaultEncoding;
        byte[] bytes = EncodeWithPreamble(contents ?? string.Empty, effective);
        return PublishAsync(path, _ => bytes, needsExistingContent: false, TextDescriptor(effective), options, cancellationToken);
    }

    /// <summary>Creates or replaces a text file with one line per item, each ending with <c>\n</c>.</summary>
    public static Task WriteAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default) =>
        WriteAllLinesAsync(path, contents, encoding: null, options: null, cancellationToken);

    /// <summary>Creates or replaces a text file with one line per item, using <paramref name="encoding"/>.</summary>
    public static Task WriteAllLinesAsync(string path, IEnumerable<string> contents, Encoding? encoding, CancellationToken cancellationToken = default) =>
        WriteAllLinesAsync(path, contents, encoding, options: null, cancellationToken);

    /// <summary>Creates or replaces a text file with one line per item and advanced options; returns the committed file.</summary>
    public static Task<HackFileInfo> WriteAllLinesAsync(string path, IEnumerable<string> contents, HackWriteOptions options, CancellationToken cancellationToken = default) =>
        WriteAllLinesAsync(path, contents, encoding: null, options, cancellationToken);

    /// <summary>Creates or replaces a text file with one line per item, <paramref name="encoding"/> and advanced options; returns the committed file.</summary>
    public static Task<HackFileInfo> WriteAllLinesAsync(
        string path, IEnumerable<string> contents, Encoding? encoding, HackWriteOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return WriteAllTextAsync(path, JoinLines(contents), encoding, options, cancellationToken);
    }

    /// <summary>Creates or replaces a binary file.</summary>
    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) =>
        WriteAllBytesAsync(path, bytes, options: null, cancellationToken);

    /// <summary>Creates or replaces a binary file with advanced options; returns the committed file.</summary>
    public static Task<HackFileInfo> WriteAllBytesAsync(string path, byte[] bytes, HackWriteOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        byte[] copy = (byte[])bytes.Clone();
        return PublishAsync(path, _ => copy, needsExistingContent: false, FileSystemContentDescriptor.Binary(), options, cancellationToken);
    }

    // ----- Append -----

    /// <summary>Appends text to a file, creating it if needed.</summary>
    public static Task AppendAllTextAsync(string path, string? contents, CancellationToken cancellationToken = default) =>
        AppendAllTextAsync(path, contents, encoding: null, options: null, cancellationToken);

    /// <summary>Appends text encoded with <paramref name="encoding"/> (the preamble is written only to an empty file).</summary>
    public static Task AppendAllTextAsync(string path, string? contents, Encoding? encoding, CancellationToken cancellationToken = default) =>
        AppendAllTextAsync(path, contents, encoding, options: null, cancellationToken);

    /// <summary>Appends text with advanced options; returns the committed file.</summary>
    public static Task<HackFileInfo> AppendAllTextAsync(string path, string? contents, HackWriteOptions options, CancellationToken cancellationToken = default) =>
        AppendAllTextAsync(path, contents, encoding: null, options, cancellationToken);

    /// <summary>
    /// Appends text with <paramref name="encoding"/> and advanced options; returns the committed file.
    /// The engine has no native append: the facade reads, concatenates and writes at the revision it
    /// read, retrying once (re-reading) on a concurrent change.
    /// </summary>
    public static Task<HackFileInfo> AppendAllTextAsync(
        string path, string? contents, Encoding? encoding, HackWriteOptions? options, CancellationToken cancellationToken = default)
    {
        Encoding effective = encoding ?? DefaultEncoding;
        string text = contents ?? string.Empty;
        return PublishAsync(
            path,
            existing => existing is { Length: > 0 }
                ? [.. existing, .. effective.GetBytes(text)]
                : EncodeWithPreamble(text, effective),
            needsExistingContent: true,
            TextDescriptor(effective),
            options,
            cancellationToken);
    }

    /// <summary>Appends lines (each ending with <c>\n</c>) to a file, creating it if needed.</summary>
    public static Task AppendAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default) =>
        AppendAllLinesAsync(path, contents, encoding: null, options: null, cancellationToken);

    /// <summary>Appends lines encoded with <paramref name="encoding"/>.</summary>
    public static Task AppendAllLinesAsync(string path, IEnumerable<string> contents, Encoding? encoding, CancellationToken cancellationToken = default) =>
        AppendAllLinesAsync(path, contents, encoding, options: null, cancellationToken);

    /// <summary>Appends lines with advanced options; returns the committed file.</summary>
    public static Task<HackFileInfo> AppendAllLinesAsync(string path, IEnumerable<string> contents, HackWriteOptions options, CancellationToken cancellationToken = default) =>
        AppendAllLinesAsync(path, contents, encoding: null, options, cancellationToken);

    /// <summary>Appends lines with <paramref name="encoding"/> and advanced options; returns the committed file.</summary>
    public static Task<HackFileInfo> AppendAllLinesAsync(
        string path, IEnumerable<string> contents, Encoding? encoding, HackWriteOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return AppendAllTextAsync(path, JoinLines(contents), encoding, options, cancellationToken);
    }

    // ----- Queries and structure -----

    /// <summary>
    /// Returns whether <paramref name="path"/> is an existing regular file (following links). Like
    /// <see cref="File.Exists(string)"/>, an invalid path or a denied lookup answers <see langword="false"/>
    /// instead of throwing; only a missing app context still throws.
    /// </summary>
    public static async Task<bool> ExistsAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;
        try
        {
            FileSystemEntrySnapshot? snapshot = await HackFileSystemCore.TryStatAsync(
                gateway, HackPath.Resolve(path, nameof(path)), FileSystemLinkBehavior.Follow, HackEntryTarget.File, cancellationToken)
                .ConfigureAwait(false);
            return snapshot is not null && snapshot.Metadata.Kind != FileSystemEntryKind.Directory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies a file. The engine copies atomically when it can; across two mounts, or when replacing
    /// an existing destination, the facade streams the bytes (keeping the source's permission bits for
    /// a new file).
    /// </summary>
    /// <exception cref="HackIOException"><paramref name="destFileName"/> exists and <paramref name="overwrite"/> is false.</exception>
    public static async Task CopyAsync(string sourceFileName, string destFileName, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        VirtualPath source = HackPath.Resolve(sourceFileName, nameof(sourceFileName));
        VirtualPath destination = HackPath.Resolve(destFileName, nameof(destFileName));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;

        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot sourceEntry = await RequireFileAsync(gateway, source, cancellationToken).ConfigureAwait(false);
            if (source == destination)
            {
                throw new HackIOException($"The file '{source.Value}' cannot be copied onto itself.", FileSystemErrorCode.AlreadyExists, source.Value);
            }

            FileSystemEntrySnapshot? destinationEntry = await HackFileSystemCore.TryStatAsync(
                gateway, destination, FileSystemLinkBehavior.Follow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
            if (destinationEntry is not null)
            {
                if (destinationEntry.Metadata.Kind == FileSystemEntryKind.Directory)
                {
                    throw HackFileSystemCore.IsDirectory(destination);
                }

                if (!overwrite)
                {
                    throw AlreadyExists(destination);
                }

                await HackFileSystemCore.CopyFileContentAsync(gateway, source, destination, cancellationToken).ConfigureAwait(false);
                return;
            }

            FileSystemEntrySnapshot destinationParent = await HackFileSystemCore.RequireParentAsync(gateway, destination, cancellationToken).ConfigureAwait(false);
            FileSystemMutationResult copied = await gateway.CopyAsync(
                new FileSystemCopyRequest(source, destination, sourceEntry.Metadata.Revision, destinationParent.Metadata.Revision),
                cancellationToken).ConfigureAwait(false);
            if (copied.Succeeded)
            {
                return;
            }

            FileSystemError error = copied.Transaction.Error!;
            if (error.Code == FileSystemErrorCode.CrossMountOperation)
            {
                await HackFileSystemCore.CopyFileContentAsync(gateway, source, destination, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (error.Code is FileSystemErrorCode.RevisionConflict or FileSystemErrorCode.AlreadyExists && attempt < 2)
            {
                continue;
            }

            throw HackFileSystemErrorTranslator.Translate(error, HackEntryTarget.File, source.Value);
        }
    }

    /// <summary>
    /// Moves (renames) a file. Across two mounts the facade copies then deletes the source, like
    /// <see cref="File.Move(string, string)"/> between volumes. Moving a file onto itself does nothing.
    /// </summary>
    /// <exception cref="HackIOException"><paramref name="destFileName"/> exists and <paramref name="overwrite"/> is false.</exception>
    public static async Task MoveAsync(string sourceFileName, string destFileName, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        VirtualPath source = HackPath.Resolve(sourceFileName, nameof(sourceFileName));
        VirtualPath destination = HackPath.Resolve(destFileName, nameof(destFileName));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;

        FileSystemEntrySnapshot sourceEntry = await HackFileSystemCore.StatAsync(
            gateway, source, FileSystemLinkBehavior.NoFollow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
        if (sourceEntry.Metadata.Kind == FileSystemEntryKind.Directory)
        {
            // File.Move only moves files: a directory source reads as "file not found".
            throw new HackFileNotFoundException(source.Value);
        }

        if (source == destination)
        {
            return;
        }

        FileSystemEntrySnapshot? destinationEntry = await HackFileSystemCore.TryStatAsync(
            gateway, destination, FileSystemLinkBehavior.NoFollow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
        if (destinationEntry is not null)
        {
            if (!overwrite || destinationEntry.Metadata.Kind == FileSystemEntryKind.Directory)
            {
                throw AlreadyExists(destination);
            }

            await HackFileSystemCore.DeleteEntryAsync(gateway, destination, recursive: false, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
        }

        await HackFileSystemCore.MoveEntryAsync(gateway, source, destination, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Permanently deletes a file. Like <see cref="File.Delete(string)"/>, a missing file is not an
    /// error, but a missing parent directory is. The trash arrives in phase 3 (<c>HackTrash</c>) and
    /// will not change this method.
    /// </summary>
    public static async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;

        FileSystemEntrySnapshot? entry = await HackFileSystemCore.TryStatAsync(
            gateway, resolved, FileSystemLinkBehavior.NoFollow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            await HackFileSystemCore.RequireParentAsync(gateway, resolved, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (entry.Metadata.Kind == FileSystemEntryKind.Directory)
        {
            throw HackFileSystemCore.IsDirectory(resolved);
        }

        await HackFileSystemCore.DeleteEntryAsync(gateway, resolved, recursive: false, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a symbolic link at <paramref name="path"/> that points to
    /// <paramref name="pathToTarget"/>, like <see cref="File.CreateSymbolicLink(string, string)"/>
    /// and <c>ln -s</c> (ADR 0064). The target text is stored as given (absolute or relative to
    /// the link's directory) and need not exist: a dangling link is valid.
    /// </summary>
    /// <param name="path">Where to create the link; it must not exist.</param>
    /// <param name="pathToTarget">The link's text.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A snapshot of the new link.</returns>
    /// <exception cref="HackIOException">An entry already exists at <paramref name="path"/>.</exception>
    public static async Task<HackFileInfo> CreateSymbolicLinkAsync(string path, string pathToTarget, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathToTarget);
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;
        FileSystemMutationResult result = await gateway.CreateAsync(
            new FileSystemCreateRequest(
                resolved,
                FileSystemEntryKind.SymbolicLink,
                HackUnixFileMode.ToPermissions((UnixFileMode)0x1FF, nameof(path)),
                symbolicLinkTarget: pathToTarget),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw HackFileSystemErrorTranslator.Translate(result.Transaction.Error!, HackEntryTarget.File, resolved.Value);
        }

        return await HackFileInfo.GetAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
    }

    // ----- Streams -----

    /// <summary>Opens a file for reading.</summary>
    public static async Task<HackFileStream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        return await HackFileStream.OpenReadAsync(HackFileSystemCore.Gateway, resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a file for writing, <b>replacing</b> its content (unlike <see cref="File.OpenWrite(string)"/>,
    /// per ADR 0043). Bytes are buffered and published by one atomic write on
    /// <see cref="Stream.FlushAsync()"/> or <see cref="HackFileStream.DisposeAsync"/>: always use <c>await using</c>.
    /// </summary>
    public static Task<HackFileStream> OpenWriteAsync(string path, CancellationToken cancellationToken = default) =>
        OpenWriteAsync(path, options: null, cancellationToken);

    /// <summary>Opens a file for replacement writing with advanced options (see <see cref="OpenWriteAsync(string, CancellationToken)"/>).</summary>
    public static async Task<HackFileStream> OpenWriteAsync(string path, HackWriteOptions? options, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        return await HackFileStream.OpenWriteAsync(HackFileSystemCore.Gateway, resolved, append: false, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a file for appending, creating it if needed; appended bytes are published on flush or disposal.</summary>
    public static Task<HackFileStream> OpenAppendAsync(string path, CancellationToken cancellationToken = default) =>
        OpenAppendAsync(path, options: null, cancellationToken);

    /// <summary>Opens a file for appending with advanced options.</summary>
    public static async Task<HackFileStream> OpenAppendAsync(string path, HackWriteOptions? options, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        return await HackFileStream.OpenWriteAsync(HackFileSystemCore.Gateway, resolved, append: true, options, cancellationToken).ConfigureAwait(false);
    }

    // ----- Permissions -----

    /// <summary>Gets a file's (or directory's) permission bits, following links like <c>stat</c>.</summary>
    public static async Task<UnixFileMode> GetUnixFileModeAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        FileSystemEntrySnapshot entry = await HackFileSystemCore.StatAsync(
            HackFileSystemCore.Gateway, resolved, FileSystemLinkBehavior.Follow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
        return HackUnixFileMode.FromPermissions(entry.Metadata.Permissions);
    }

    /// <summary>Sets a file's (or directory's) permission bits, following links like <c>chmod</c>. Only the <c>0777</c> bits are supported.</summary>
    public static async Task SetUnixFileModeAsync(string path, UnixFileMode mode, CancellationToken cancellationToken = default)
    {
        FileSystemPermissions permissions = HackUnixFileMode.ToPermissions(mode, nameof(mode));
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;

        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot entry = await HackFileSystemCore.StatAsync(
                gateway, resolved, FileSystemLinkBehavior.Follow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
            FileSystemMutationResult result = await gateway.SetPermissionsAsync(
                new FileSystemSetPermissionsRequest(resolved, permissions, entry.Metadata.Revision, FileSystemLinkBehavior.Follow),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return;
            }

            if (result.Transaction.Error!.Code == FileSystemErrorCode.RevisionConflict && attempt < 2)
            {
                continue;
            }

            throw HackFileSystemErrorTranslator.Translate(result.Transaction.Error!, HackEntryTarget.File, resolved.Value);
        }
    }

    // ----- Helpers -----

    private static async Task<HackFileInfo> PublishAsync(
        string path,
        Func<byte[]?, byte[]> compose,
        bool needsExistingContent,
        FileSystemContentDescriptor descriptor,
        HackWriteOptions? options,
        CancellationToken cancellationToken)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        FileSystemEntrySnapshot committed = await HackFileSystemCore.PublishAsync(
            HackFileSystemCore.Gateway, resolved, compose, needsExistingContent, descriptor, options, cancellationToken).ConfigureAwait(false);
        return new HackFileInfo(resolved.Value, committed.Metadata, linkTarget: null);
    }

    private static async Task<StreamReader> OpenTextReaderAsync(string path, Encoding? encoding, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new StreamReader(new MemoryStream(bytes, writable: false), encoding ?? DefaultEncoding, detectEncodingFromByteOrderMarks: true);
    }

    private static async ValueTask<FileSystemEntrySnapshot> RequireFileAsync(
        IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemEntrySnapshot entry = await HackFileSystemCore.StatAsync(
            gateway, path, FileSystemLinkBehavior.Follow, HackEntryTarget.File, cancellationToken).ConfigureAwait(false);
        return entry.Metadata.Kind == FileSystemEntryKind.Directory ? throw HackFileSystemCore.IsDirectory(path) : entry;
    }

    private static HackIOException AlreadyExists(VirtualPath path) =>
        new($"The file '{path.Value}' already exists.", FileSystemErrorCode.AlreadyExists, path.Value);

    private static byte[] EncodeWithPreamble(string text, Encoding encoding) =>
        [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    private static FileSystemContentDescriptor TextDescriptor(Encoding encoding) =>
        FileSystemContentDescriptor.Text("text/plain", encoding.WebName);

    private static string JoinLines(IEnumerable<string> lines)
    {
        StringBuilder builder = new();
        foreach (string line in lines)
        {
            builder.Append(line).Append(NewLine);
        }

        return builder.ToString();
    }
}
