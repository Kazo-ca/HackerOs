namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>Identifies how file content should be interpreted by consumers.</summary>
public enum FileSystemContentKind
{
    /// <summary>Opaque binary bytes with no character encoding.</summary>
    Binary = 1,

    /// <summary>Encoded text bytes with a declared character encoding.</summary>
    Text = 2
}

/// <summary>Describes streamed file content without embedding its bytes.</summary>
public sealed record FileSystemContentDescriptor
{
    private FileSystemContentDescriptor(
        FileSystemContentKind kind,
        string mediaType,
        string? encodingName)
    {
        Kind = kind;
        MediaType = mediaType;
        EncodingName = encodingName;
    }

    /// <summary>Gets whether the content is binary or encoded text.</summary>
    public FileSystemContentKind Kind { get; }

    /// <summary>Gets the normalized media type without parameters.</summary>
    public string MediaType { get; }

    /// <summary>Gets the text encoding web name, or <see langword="null"/> for binary content.</summary>
    public string? EncodingName { get; }

    /// <summary>Creates an opaque binary content descriptor.</summary>
    /// <param name="mediaType">Media type without parameters.</param>
    /// <returns>The binary descriptor.</returns>
    public static FileSystemContentDescriptor Binary(string mediaType = "application/octet-stream") =>
        new(FileSystemContentKind.Binary, ValidateMediaType(mediaType), null);

    /// <summary>Creates an encoded text content descriptor.</summary>
    /// <param name="mediaType">Text media type without parameters.</param>
    /// <param name="encodingName">Character encoding web name.</param>
    /// <returns>The text descriptor.</returns>
    public static FileSystemContentDescriptor Text(
        string mediaType = "text/plain",
        string encodingName = "utf-8")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingName);
        return new FileSystemContentDescriptor(
            FileSystemContentKind.Text,
            ValidateMediaType(mediaType),
            encodingName.Trim().ToLowerInvariant());
    }

    private static string ValidateMediaType(string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        string normalized = mediaType.Trim().ToLowerInvariant();
        if (!normalized.Contains('/') || normalized.Contains(';') || normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "Media types must contain one type separator and no parameters or whitespace.",
                nameof(mediaType));
        }

        return normalized;
    }
}

/// <summary>
/// Opens an independently owned readable stream for one write or copy operation.
/// </summary>
public interface IFileSystemContentSource
{
    /// <summary>Gets the content interpretation persisted with the file.</summary>
    FileSystemContentDescriptor Descriptor { get; }

    /// <summary>Gets the byte length when known without opening the stream.</summary>
    long? Length { get; }

    /// <summary>Opens a readable stream positioned at the beginning.</summary>
    /// <param name="cancellationToken">Signals cancellation before the stream is opened.</param>
    /// <returns>A stream owned and disposed by the filesystem operation.</returns>
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owns one streamed file read and its immutable entry snapshot.</summary>
public sealed class FileSystemContentReadHandle : IAsyncDisposable
{
    private int _disposed;

    /// <summary>Initializes an owned streamed read.</summary>
    /// <param name="entry">Entry snapshot matching the opened content revision.</param>
    /// <param name="descriptor">Content interpretation.</param>
    /// <param name="content">Readable stream positioned at the beginning.</param>
    public FileSystemContentReadHandle(
        FileSystemEntrySnapshot entry,
        FileSystemContentDescriptor descriptor,
        Stream content)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException("Filesystem content streams must be readable.", nameof(content));
        }

        Entry = entry;
        Descriptor = descriptor;
        Content = content;
    }

    /// <summary>Gets the entry snapshot matching the opened content revision.</summary>
    public FileSystemEntrySnapshot Entry { get; }

    /// <summary>Gets the content interpretation.</summary>
    public FileSystemContentDescriptor Descriptor { get; }

    /// <summary>Gets the readable stream owned by this handle.</summary>
    public Stream Content { get; }

    /// <summary>Disposes the owned content stream exactly once.</summary>
    /// <returns>A task representing asynchronous stream disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await Content.DisposeAsync().ConfigureAwait(false);
        }
    }
}