using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Turns a <c>Hack*</c> file API failure into the short reason a Linux shell prints after
/// <c>hsh: &lt;path&gt;: </c> (the <c>strerror</c> text of the matching <c>errno</c>).
/// </summary>
/// <remarks>
/// Lives in the SDK, next to <see cref="IShellSession"/>, so built-in commands such as <c>cd</c>
/// print the same text as the shell itself without referencing Platform Core (ADR 0056, section 6).
/// </remarks>
public static class ShellErrorText
{
    /// <summary>Gets the reason for <paramref name="exception"/> in English, e.g. "No such file or directory".</summary>
    public static string Describe(Exception exception) => Describe(exception, context: null);

    /// <summary>
    /// Gets the reason for <paramref name="exception"/> in the language of <paramref name="context"/>'s
    /// process (ADR 0104), as glibc's <c>strerror</c> follows <c>LC_MESSAGES</c>: "Aucun fichier ou
    /// dossier de ce type" under <c>LANG=fr_FR.UTF-8</c>. An exception with no <c>errno</c> keeps its
    /// own message.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="context">The app printing the reason; English when <see langword="null"/>.</param>
    public static string Describe(Exception exception, IAppExecutionContext? context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string? key = exception switch
        {
            HackUnauthorizedAccessException { ErrorCode: FileSystemErrorCode.NotFile } => "Errno.IsDirectory",
            UnauthorizedAccessException => "Errno.PermissionDenied",
            HackFileNotFoundException or HackDirectoryNotFoundException => "Errno.NoSuchFile",
            HackIOException { ErrorCode: FileSystemErrorCode.NotDirectory } => "Errno.NotDirectory",
            HackIOException { ErrorCode: FileSystemErrorCode.NoSpace } => "Errno.NoSpace",
            HackIOException { ErrorCode: FileSystemErrorCode.FileTooLarge } => "Errno.FileTooLarge",
            HackIOException { ErrorCode: FileSystemErrorCode.TooManyOpenFiles } => "Errno.TooManyOpenFiles",
            ArgumentException => "Errno.InvalidPath",
            _ => null
        };
        return key is null ? exception.Message : HackLocalization.GetLocalizer<Strings>(context)[key].Value;
    }
}
