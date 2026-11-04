using Kazo.Os.App.Abstractions;
using System.Text;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Intents;

/// <summary>
/// Reads and writes a small UTF-8 text file of a user's home as that user (ADR 0096 section 5,
/// ADR 0121): POSIX permissions of the principal apply, as for a desktop tool editing
/// <c>~/.config</c> on their behalf. Shared by <see cref="IntentChoiceStore"/> and
/// <see cref="UserFileAssociationStore"/>.
/// </summary>
internal static class PrincipalTextFile
{
    /// <summary>Reads the whole file, or returns <see langword="null"/> when it is absent or unreadable.</summary>
    /// <param name="fileSystem">OS filesystem.</param>
    /// <param name="principal">Acting user.</param>
    /// <param name="path">File to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<string?> ReadAsync(
        IFileSystemService fileSystem, AuthenticatedPrincipal principal, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> result = await fileSystem.ReadAsync(
            new FileSystemReadRequest(path),
            PrincipalFileAccess.ForUser(principal, AppIntentDispatcher.OsAppId),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return null;
        }

        await using FileSystemContentReadHandle handle = result.Value;
        using StreamReader reader = new(handle.Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the file's content, creating <c>~/.config/hackeros</c> (0755) and the file (with
    /// <paramref name="mode"/>) first when they are missing.
    /// </summary>
    /// <param name="fileSystem">OS filesystem.</param>
    /// <param name="principal">Acting user; the file lives under their <c>~/.config/hackeros</c>.</param>
    /// <param name="fileName">File name inside <c>~/.config/hackeros</c>.</param>
    /// <param name="content">Complete new content.</param>
    /// <param name="mode">Permission bits of a newly created file (an existing file keeps its own).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="false"/> when the user's permissions refuse the write.</returns>
    public static async ValueTask<bool> WriteConfigAsync(
        IFileSystemService fileSystem,
        AuthenticatedPrincipal principal,
        string fileName,
        string content,
        ushort mode,
        CancellationToken cancellationToken)
    {
        FileSystemAuthorizationContext context = PrincipalFileAccess.ForUser(principal, AppIntentDispatcher.OsAppId);
        string login = principal.LoginName.Value;
        FileSystemMutationResult directory = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse($"/home/{login}/.config/hackeros"), FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0b111_101_101)),
            context,
            cancellationToken).ConfigureAwait(false);
        if (!directory.Succeeded && directory.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            return false;
        }

        VirtualPath path = VirtualPath.Parse($"/home/{login}/.config/hackeros/{fileName}");
        FileSystemMutationResult file = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(mode)),
            context,
            cancellationToken).ConfigureAwait(false);
        if (!file.Succeeded && file.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            return false;
        }

        FileSystemMutationResult write = await fileSystem.WriteAsync(
            new FileSystemWriteRequest(path),
            new TextContentSource(content),
            context,
            cancellationToken).ConfigureAwait(false);
        return write.Succeeded;
    }

    private sealed class TextContentSource(string content) : IFileSystemContentSource
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
