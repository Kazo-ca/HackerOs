using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.AppSdk.Settings;

/// <summary>
/// Turns a filesystem error from a settings read or write into a BCL exception, in the spirit of
/// <c>System.IO</c> (ADR 0043 section 5).
/// </summary>
/// <remarks>
/// Phase 1 interim: <c>KazoSettings</c> and the <c>Hack*</c> file API (ADR 0043) are built in
/// parallel. Once <c>Kazo.Os.AppSdk.IO</c> lands, this translator delegates to its
/// <c>KazoUnauthorizedAccessException</c>/<c>KazoIOException</c> family, which derive from the same
/// BCL types thrown here, so a caller's <c>catch (UnauthorizedAccessException)</c> keeps working.
/// </remarks>
internal static class SettingsErrorTranslator
{
    public static Exception ToException(FileSystemError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        string path = error.Path?.Value ?? "(unknown path)";
        return error.Code switch
        {
            FileSystemErrorCode.PermissionDenied
                or FileSystemErrorCode.CapabilityDenied
                or FileSystemErrorCode.AuthorityDenied
                or FileSystemErrorCode.ProtectedEntry
                or FileSystemErrorCode.NotFile => new UnauthorizedAccessException(
                    $"Access to settings file '{path}' is denied ({error.Code}"
                    + (error.RequiredCapability is null ? ")." : $", requires {error.RequiredCapability}).")),
            FileSystemErrorCode.Cancelled => new OperationCanceledException($"Settings operation on '{path}' was cancelled."),
            FileSystemErrorCode.InvalidPath or FileSystemErrorCode.InvalidName => new ArgumentException($"Invalid settings path '{path}'."),
            _ => new IOException($"Settings operation on '{path}' failed: {error.Code}.")
        };
    }
}
