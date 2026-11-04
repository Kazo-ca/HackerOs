using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Theming.Abstractions;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.ThemeEditor;

/// <summary>
/// Reads and writes <c>.theme</c> files through a filesystem gateway: the app's own (home folder)
/// or one carrying the handle a file dialog issued for a file elsewhere (open or export). Its messages
/// are in the language of the process running the call (ADR 0104), English outside any app.
/// </summary>
public static class ThemeFileIo
{
    /// <summary>Reads a theme file as UTF-8 text.</summary>
    /// <param name="fileSystem">Authorized gateway.</param>
    /// <param name="path">File path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The content, or an error message.</returns>
    public static async Task<(string? Content, string? Error)> ReadAsync(
        IAppFileSystemGateway fileSystem, VirtualPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        FileSystemResult<FileSystemContentReadHandle> result = await fileSystem
            .ReadAsync(new FileSystemReadRequest(path), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return (null, Describe(result.Error?.Code));
        }

        await using FileSystemContentReadHandle handle = result.Value;
        if (handle.Entry.Metadata is FileMetadata metadata && metadata.Length > ThemeFile.MaxFileBytes)
        {
            return (null, Text["Error.FileTooLarge", ThemeFile.MaxFileBytes / 1024]);
        }

        using StreamReader reader = new(handle.Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), null);
    }

    /// <summary>Writes a theme file, creating it (<c>0644</c>) when needed.</summary>
    /// <param name="fileSystem">Authorized gateway.</param>
    /// <param name="path">Target path; its folder must exist.</param>
    /// <param name="content">Theme text.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="null"/> on success, otherwise an error message.</returns>
    public static async Task<string?> WriteAsync(
        IAppFileSystemGateway fileSystem, VirtualPath path, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(content);
        FileSystemMutationResult creation = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)),
            cancellationToken).ConfigureAwait(false);
        if (!creation.Succeeded && creation.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            return Describe(creation.Transaction.Error?.Code);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(content);
        FileSystemMutationResult written = await fileSystem.WriteAsync(
            new FileSystemWriteRequest(path, null),
            new TextContentSource(bytes),
            cancellationToken).ConfigureAwait(false);
        return written.Succeeded ? null : Describe(written.Transaction.Error?.Code);
    }

    /// <summary>The texts in the language of the running process, read at each call.</summary>
    private static IStringLocalizer<Strings> Text => HackLocalization.GetLocalizer<Strings>();

    private static string Describe(FileSystemErrorCode? code) => code switch
    {
        FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.CapabilityDenied or FileSystemErrorCode.AuthorityDenied => Text["Error.PermissionDenied"],
        FileSystemErrorCode.NotFound => Text["Error.NotFound"],
        FileSystemErrorCode.NotFile => Text["Error.NotFile"],
        null => Text["Error.Unknown"],
        _ => code.Value.ToString()
    };

    private sealed class TextContentSource(byte[] content) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text(ThemeFile.MediaType, "utf-8");

        public long? Length => content.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }
}
