using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Common;

/// <summary>
/// The <c>errno</c> reasons the commands print after <c>cmd: file: </c>, the texts glibc's
/// <c>strerror</c> returns (ADR 0104): translated with the process language, like a real
/// <c>LANG=fr_FR.UTF-8 cat missing</c>, which prints « Aucun fichier ou dossier de ce nom ».
/// </summary>
internal enum Errno
{
    /// <summary><c>ENOENT</c>: "No such file or directory".</summary>
    NotFound,

    /// <summary><c>EACCES</c>: "Permission denied".</summary>
    PermissionDenied,

    /// <summary><c>EISDIR</c>: "Is a directory".</summary>
    IsDirectory,

    /// <summary><c>ENOTDIR</c>: "Not a directory".</summary>
    NotDirectory,

    /// <summary><c>ENOSPC</c>: "No space left on device".</summary>
    NoSpace,

    /// <summary><c>EFBIG</c>: "File too large".</summary>
    FileTooLarge,

    /// <summary><c>EMFILE</c>: "Too many open files" (ADR 0125).</summary>
    TooManyOpenFiles,

    /// <summary><c>EEXIST</c>: "File exists".</summary>
    FileExists,

    /// <summary><c>ENOTEMPTY</c>: "Directory not empty".</summary>
    NotEmpty,

    /// <summary>A path the filesystem cannot parse: "Invalid path".</summary>
    InvalidPath,

    /// <summary><c>EPERM</c>: "Operation not permitted".</summary>
    NotPermitted,

    /// <summary><c>EOPNOTSUPP</c>: "Operation not supported".</summary>
    NotSupported,

    /// <summary><c>EINVAL</c>: "Invalid argument".</summary>
    InvalidArgument
}

/// <summary>
/// Translated texts shared by every command (<c>Resources/Common/Strings*.resx</c>, ADR 0104):
/// the <c>errno</c> reasons and the <c>cmd: file: reason</c> line, read in the language of the
/// command's process.
/// </summary>
internal static class CommandText
{
    /// <summary>
    /// The English reasons of <see cref="ShellErrorText.Describe(Exception)"/> (the SDK's one
    /// mapping from a <c>Kazo*</c> exception to an <c>errno</c> text), so they translate without
    /// a second copy of that mapping.
    /// </summary>
    private static readonly Dictionary<string, Errno> s_englishReasons = new(StringComparer.Ordinal)
    {
        ["No such file or directory"] = Errno.NotFound,
        ["Permission denied"] = Errno.PermissionDenied,
        ["Is a directory"] = Errno.IsDirectory,
        ["Not a directory"] = Errno.NotDirectory,
        ["No space left on device"] = Errno.NoSpace,
        ["File too large"] = Errno.FileTooLarge,
        ["Too many open files"] = Errno.TooManyOpenFiles,
        ["File exists"] = Errno.FileExists,
        ["Directory not empty"] = Errno.NotEmpty,
        ["Invalid path"] = Errno.InvalidPath,
        ["Operation not permitted"] = Errno.NotPermitted,
        ["Operation not supported"] = Errno.NotSupported,
        ["Invalid argument"] = Errno.InvalidArgument
    };

    /// <summary>Gets the localizer of the shared texts for the command's process.</summary>
    public static IStringLocalizer<Strings> For(TerminalExecutionContext context) =>
        context.App.GetLocalizer<Strings>();

    /// <summary>Gets the <c>strerror</c> text of <paramref name="errno"/> in the process language.</summary>
    public static string Reason(TerminalExecutionContext context, Errno errno) =>
        For(context)["Errno." + errno];

    /// <summary>
    /// Gets the reason for <paramref name="exception"/> in the process language: the SDK's
    /// <see cref="ShellErrorText.Describe(Exception)"/>, translated when it is a known
    /// <c>errno</c> text; any other message (an exception's own) is printed unchanged.
    /// </summary>
    public static string Describe(TerminalExecutionContext context, Exception exception) =>
        Translate(context, ShellErrorText.Describe(exception));

    /// <summary>Translates a known English <c>errno</c> text; returns any other text unchanged.</summary>
    public static string Translate(TerminalExecutionContext context, string englishReason) =>
        s_englishReasons.TryGetValue(englishReason, out Errno errno) ? Reason(context, errno) : englishReason;

    /// <summary>
    /// Formats the coreutils error line <c>cmd: file: reason</c> (French typography puts a space
    /// before each colon).
    /// </summary>
    public static string FileError(TerminalExecutionContext context, string command, string file, string reason) =>
        For(context)["FileError", command, file, reason];
}
