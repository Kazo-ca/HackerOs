using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.AppSdk.IO;

/// <summary>Whether the failed facade call was about a file or a directory (it decides which "not found" exception applies).</summary>
internal enum HackEntryTarget
{
    /// <summary>The caller asked for a regular file (<c>HackFile</c>, <c>HackFileInfo</c>, streams).</summary>
    File,

    /// <summary>The caller asked for a directory (<c>HackDirectory</c>, <c>HackDirectoryInfo</c>).</summary>
    Directory
}

/// <summary>
/// Translates the engine's stable <see cref="FileSystemErrorCode"/> into the <c>Hack*</c> /
/// BCL exception the equivalent <c>System.IO</c> call would throw (ADR 0043, section 5 table).
/// </summary>
internal static class HackFileSystemErrorTranslator
{
    /// <summary>Builds the exception for one failed gateway call.</summary>
    /// <param name="error">Engine error returned by the gateway.</param>
    /// <param name="target">Whether the facade call was about a file or a directory.</param>
    /// <param name="fallbackPath">Path the caller asked for, used when the error carries none.</param>
    public static Exception Translate(FileSystemError error, HackEntryTarget target, string? fallbackPath = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        string? path = error.Path?.Value ?? fallbackPath;

        return error.Code switch
        {
            // A missing parent during a create, or any miss on a directory call, is a missing directory.
            FileSystemErrorCode.NotFound when target == HackEntryTarget.Directory || error.Operation == FileSystemOperation.Create =>
                new HackDirectoryNotFoundException(path),
            FileSystemErrorCode.NotFound => new HackFileNotFoundException(path),

            FileSystemErrorCode.NotFile => new HackUnauthorizedAccessException(
                $"Access to the path '{path}' is denied: it is a directory.", error.Code, path, error.RequiredCapability),

            FileSystemErrorCode.PermissionDenied or
            FileSystemErrorCode.CapabilityDenied or
            FileSystemErrorCode.AuthorityDenied or
            FileSystemErrorCode.ProtectedEntry => new HackUnauthorizedAccessException(
                $"Access to the path '{path}' is denied ({error.Code}).", error.Code, path, error.RequiredCapability),

            FileSystemErrorCode.RevisionConflict => new HackFileConflictException(path, currentRevision: null),

            FileSystemErrorCode.InvalidPath or
            FileSystemErrorCode.InvalidName or
            FileSystemErrorCode.RootContainmentViolation => new ArgumentException(
                $"The path '{path}' is not valid ({error.Code}).", nameof(path)),

            FileSystemErrorCode.UnsupportedOperation => new NotSupportedException(
                $"The filesystem holding '{path}' does not support this operation."),

            FileSystemErrorCode.Cancelled => new OperationCanceledException(
                $"The operation on '{path}' was cancelled before it committed."),

            FileSystemErrorCode.AlreadyExists => new HackIOException(
                $"The path '{path}' already exists.", error.Code, path),
            FileSystemErrorCode.DirectoryNotEmpty => new HackIOException(
                $"The directory '{path}' is not empty.", error.Code, path),
            FileSystemErrorCode.NotDirectory => new HackIOException(
                $"A component of the path '{path}' is not a directory.", error.Code, path),
            FileSystemErrorCode.NoSpace => new HackIOException(
                $"No space left on device while writing '{path}'.", error.Code, path),
            FileSystemErrorCode.FileTooLarge => new HackIOException(
                $"The file '{path}' is too large (16 MiB at most).", error.Code, path),
            // ADR 0125: EMFILE, the process holds as many descriptors as its nofile limit allows.
            FileSystemErrorCode.TooManyOpenFiles => new HackIOException(
                $"{path}: Too many open files", error.Code, path),

            // SymbolicLinkLoop, SymbolicLinkLimitExceeded, InvalidContent, ProviderUnavailable,
            // ProviderFailure, CrossMountOperation (normally absorbed by the facade) and any code added later.
            _ => new HackIOException($"The operation on '{path}' failed ({error.Code}).", error.Code, path)
        };
    }
}
