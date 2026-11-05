using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.AppSdk.IO;

/// <summary>
/// Base I/O failure of the <c>Kazo*</c> file API. Derives from <see cref="IOException"/> so an
/// ordinary <c>catch (IOException)</c> works exactly as with <c>System.IO</c> (ADR 0043, section 5).
/// </summary>
public class KazoIOException : IOException
{
    /// <summary>Initializes an I/O failure.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="errorCode">Engine error code that caused the failure, when there is one.</param>
    /// <param name="path">Canonical path involved, when known.</param>
    /// <param name="innerException">Underlying exception, when there is one.</param>
    public KazoIOException(
        string message,
        FileSystemErrorCode? errorCode = null,
        string? path = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Path = path;
    }

    /// <summary>Gets the engine error code that caused the failure, when there is one.</summary>
    public FileSystemErrorCode? ErrorCode { get; }

    /// <summary>Gets the canonical path involved, when known.</summary>
    public string? Path { get; }
}

/// <summary>A file operation targeted a file that does not exist (like <see cref="FileNotFoundException"/>).</summary>
public sealed class KazoFileNotFoundException : KazoIOException
{
    /// <summary>Initializes the exception for <paramref name="path"/>.</summary>
    public KazoFileNotFoundException(string? path, FileSystemErrorCode? errorCode = FileSystemErrorCode.NotFound)
        : base($"Could not find file '{path}'.", errorCode, path)
    {
    }
}

/// <summary>
/// A directory (or a missing parent directory of a file) could not be found (like
/// <see cref="DirectoryNotFoundException"/>).
/// </summary>
public sealed class KazoDirectoryNotFoundException : KazoIOException
{
    /// <summary>Initializes the exception for <paramref name="path"/>.</summary>
    public KazoDirectoryNotFoundException(string? path, FileSystemErrorCode? errorCode = FileSystemErrorCode.NotFound)
        : base($"Could not find a part of the path '{path}'.", errorCode, path)
    {
    }
}

/// <summary>
/// The file changed concurrently: its optimistic revision no longer matches the one the write was
/// based on (after the facade's single automatic retry, or immediately when the caller supplied
/// <see cref="KazoWriteOptions.ExpectedRevision"/>).
/// </summary>
public sealed class KazoFileConflictException : KazoIOException
{
    /// <summary>Initializes the exception for <paramref name="path"/>.</summary>
    /// <param name="path">Canonical path that changed.</param>
    /// <param name="currentRevision">Revision observed after the conflict, when it could be read.</param>
    public KazoFileConflictException(string? path, long? currentRevision)
        : base($"The file '{path}' was modified by someone else (current revision: {currentRevision?.ToString() ?? "unknown"}).",
            FileSystemErrorCode.RevisionConflict, path)
    {
        CurrentRevision = currentRevision;
    }

    /// <summary>Gets the revision observed after the conflict, or <see langword="null"/> when it could not be read.</summary>
    public long? CurrentRevision { get; }
}

/// <summary>
/// Access was denied by mode bits, capability policy, authority, a protected entry, or because the
/// operation targeted a directory where a file was required (like <see cref="UnauthorizedAccessException"/>).
/// </summary>
public sealed class KazoUnauthorizedAccessException : UnauthorizedAccessException
{
    /// <summary>Initializes an access denial.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="errorCode">Engine error code that caused the denial, when there is one.</param>
    /// <param name="path">Canonical path involved, when known.</param>
    /// <param name="requiredCapability">Capability policy required, when known (used by the ADR 0042 approval prompt).</param>
    public KazoUnauthorizedAccessException(
        string message,
        FileSystemErrorCode? errorCode = null,
        string? path = null,
        string? requiredCapability = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Path = path;
        RequiredCapability = requiredCapability;
    }

    /// <summary>Gets the engine error code that caused the denial, when there is one.</summary>
    public FileSystemErrorCode? ErrorCode { get; }

    /// <summary>Gets the canonical path involved, when known.</summary>
    public string? Path { get; }

    /// <summary>Gets the capability policy required, when known.</summary>
    public string? RequiredCapability { get; }
}
