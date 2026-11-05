using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.KazoPaint;

/// <summary>Outcome of reading an image file.</summary>
/// <param name="Bytes">File content when read.</param>
/// <param name="Header">Format and size from the header when recognized.</param>
/// <param name="Error">Why the file cannot be edited, or <see langword="null"/>.</param>
public sealed record PaintFileReadResult(byte[]? Bytes, ImageFileHeader? Header, string? Error);

/// <summary>
/// Reads and writes image files for Kazo Paint through the app's filesystem gateway (ADR 0103 §1):
/// the C# side owns every file access, the canvas module only decodes and encodes pixels. The
/// gateway is the app's own (home folder) or one carrying the handle a file dialog issued for a
/// file outside it, exactly as the Text Editor does. Its messages are in the language of the process
/// running the call (ADR 0104), English outside any app.
/// </summary>
public static class PaintFileStore
{
    /// <summary>
    /// Reads an image file and checks it before any decoding: size of the file, known format and
    /// pixel size from the header.
    /// </summary>
    /// <param name="fileSystem">Authorized gateway.</param>
    /// <param name="path">File path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<PaintFileReadResult> ReadAsync(
        IAppFileSystemGateway fileSystem, VirtualPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        FileSystemResult<FileSystemContentReadHandle> result = await fileSystem
            .ReadAsync(new FileSystemReadRequest(path), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return new PaintFileReadResult(null, null, Describe(result.Error?.Code));
        }

        await using FileSystemContentReadHandle handle = result.Value;
        if (handle.Entry.Metadata is FileMetadata metadata && metadata.Length > ImageFileFormat.MaxFileBytes)
        {
            return new PaintFileReadResult(null, null,
                Text["Error.FileTooLarge", metadata.Length / (1024 * 1024), ImageFileFormat.MaxFileBytes / (1024 * 1024)]);
        }

        using MemoryStream buffer = new();
        await handle.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        byte[] bytes = buffer.ToArray();
        return Inspect(bytes);
    }

    /// <summary>Checks an image file's content: size, supported format and pixel dimensions.</summary>
    /// <param name="bytes">Whole file content.</param>
    public static PaintFileReadResult Inspect(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > ImageFileFormat.MaxFileBytes)
        {
            return new PaintFileReadResult(null, null,
                Text["Error.FileTooLargeUnknownSize", ImageFileFormat.MaxFileBytes / (1024 * 1024)]);
        }

        if (!ImageFileFormat.TryReadHeader(bytes, out ImageFileHeader header))
        {
            return new PaintFileReadResult(null, null, Text["Error.NotAnImage"]);
        }

        if (header.Width > ImageFileFormat.MaxDimension || header.Height > ImageFileFormat.MaxDimension)
        {
            return new PaintFileReadResult(null, header,
                Text["Error.ImageTooLarge", header.Width, header.Height, ImageFileFormat.MaxDimension]);
        }

        return new PaintFileReadResult(bytes, header, null);
    }

    /// <summary>
    /// Writes encoded image bytes, creating the file (<c>0644</c>) when it does not exist yet.
    /// </summary>
    /// <param name="fileSystem">Authorized gateway.</param>
    /// <param name="path">Target path.</param>
    /// <param name="bytes">Encoded image.</param>
    /// <param name="mediaType">Media type stored with the file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="null"/> on success, otherwise a message for the user.</returns>
    public static async Task<string?> WriteAsync(
        IAppFileSystemGateway fileSystem, VirtualPath path, byte[] bytes, string mediaType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > ImageFileFormat.MaxFileBytes)
        {
            return Text["Error.EncodedTooLarge", ImageFileFormat.MaxFileBytes / (1024 * 1024)];
        }

        FileSystemMutationResult creation = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)),
            cancellationToken).ConfigureAwait(false);
        if (!creation.Succeeded && creation.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            return Describe(creation.Transaction.Error?.Code);
        }

        FileSystemMutationResult written = await fileSystem.WriteAsync(
            new FileSystemWriteRequest(path, null),
            new ByteContentSource(bytes, FileSystemContentDescriptor.Binary(mediaType)),
            cancellationToken).ConfigureAwait(false);
        return written.Succeeded ? null : Describe(written.Transaction.Error?.Code);
    }

    /// <summary>The texts in the language of the running process, read at each call.</summary>
    private static IStringLocalizer<Strings> Text => KazoLocalization.GetLocalizer<Strings>();

    private static string Describe(FileSystemErrorCode? code) => code switch
    {
        FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.CapabilityDenied or FileSystemErrorCode.AuthorityDenied => Text["Error.PermissionDenied"],
        FileSystemErrorCode.NotFound => Text["Error.NotFound"],
        FileSystemErrorCode.NotFile => Text["Error.NotFile"],
        null => Text["Error.Unknown"],
        _ => code.Value.ToString()
    };

    /// <summary>Publishes a fixed byte array as file content.</summary>
    private sealed class ByteContentSource(byte[] content, FileSystemContentDescriptor descriptor) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = descriptor;

        public long? Length => content.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }
}
