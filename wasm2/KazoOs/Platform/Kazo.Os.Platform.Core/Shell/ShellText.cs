using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// The shell's own messages in the language of the shell (ADR 0104): bash prints its diagnostics
/// through <c>gettext</c> in the locale of its process, so <c>hsh</c> reads <c>LC_ALL</c>,
/// <c>LC_MESSAGES</c> and <c>LANG</c> from its own variables (a <c>LANG=fr_FR.UTF-8</c> typed at
/// the prompt takes effect at once, as <c>setlocale</c> does in bash) and looks its texts up in
/// <c>Resources/Strings.resx</c> / <c>Strings.fr.resx</c>.
/// </summary>
/// <remarks>
/// Only human-readable diagnostics go through here. Program names (<c>hsh: </c>, <c>sudo: </c>),
/// command names, paths, error codes and machine-readable output (<c>set -o</c>, <c>trap -p</c>)
/// stay as they are.
/// </remarks>
internal static class ShellText
{
    /// <summary>Gets the language of <paramref name="session"/>'s messages: <c>en</c> or <c>fr</c>.</summary>
    /// <param name="session">The shell whose variables decide.</param>
    public static CultureInfo Language(ShellSession session) => KazoLocale.ResolveLanguage(session.GetVariable);

    /// <summary>Gets the shell's texts in <paramref name="session"/>'s language.</summary>
    /// <param name="session">The shell whose variables decide.</param>
    public static IStringLocalizer For(ShellSession session) => Strings.For(Language(session));

    /// <summary>Gets the English texts, the ones kept in exception messages and logs.</summary>
    public static IStringLocalizer English => Strings.For(KazoLocale.DefaultLanguage);

    /// <summary>
    /// Translates the reason of a <c>Kazo*</c> file API failure (the <c>strerror</c> text a shell
    /// prints after <c>hsh: &lt;path&gt;: </c>), as glibc translates <c>strerror</c> in the
    /// process's locale. Mirrors <see cref="Kazo.Os.AppSdk.ShellErrorText.Describe(System.Exception)"/>, whose
    /// English texts are the neutral resources.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="texts">The shell's texts.</param>
    public static string DescribeError(Exception exception, IStringLocalizer texts) => exception switch
    {
        KazoUnauthorizedAccessException { ErrorCode: FileSystemErrorCode.NotFile } => texts["Errno.IsDirectory"],
        UnauthorizedAccessException => texts["Errno.PermissionDenied"],
        KazoFileNotFoundException or KazoDirectoryNotFoundException => texts["Errno.NoSuchFile"],
        KazoIOException { ErrorCode: FileSystemErrorCode.NotDirectory } => texts["Errno.NotDirectory"],
        KazoIOException { ErrorCode: FileSystemErrorCode.NoSpace } => texts["Errno.NoSpace"],
        KazoIOException { ErrorCode: FileSystemErrorCode.FileTooLarge } => texts["Errno.FileTooLarge"],
        KazoIOException { ErrorCode: FileSystemErrorCode.TooManyOpenFiles } => texts["Errno.TooManyOpenFiles"],
        ArgumentException => texts["Errno.InvalidPath"],
        _ => exception.Message
    };
}

/// <summary>
/// A shell error whose text is a resource key and its arguments, so the shell prints it in its own
/// language while <see cref="Exception.Message"/> stays English for logs and developers.
/// </summary>
public interface IShellLocalizableError
{
    /// <summary>Gets the key of the text in <c>Resources/Strings.resx</c>.</summary>
    string TextKey { get; }

    /// <summary>Gets the arguments of the text (<c>{0}</c>, <c>{1}</c>…).</summary>
    IReadOnlyList<object> TextArguments { get; }
}

/// <summary>A text of the shell as a resource key and its arguments, formatted later in the shell's language.</summary>
/// <param name="TextKey">Key of the text in <c>Resources/Strings.resx</c>.</param>
/// <param name="TextArguments">Arguments of the text.</param>
public sealed record ShellLocalizedText(string TextKey, IReadOnlyList<object> TextArguments) : IShellLocalizableError
{
    /// <summary>Creates the text <paramref name="textKey"/> with <paramref name="arguments"/>.</summary>
    /// <param name="textKey">Key of the text in <c>Resources/Strings.resx</c>.</param>
    /// <param name="arguments">Arguments of the text.</param>
    public ShellLocalizedText(string textKey, params object[] arguments)
        : this(textKey, (IReadOnlyList<object>)arguments)
    {
    }

    /// <summary>Gets the English text.</summary>
    public override string ToString() => this.Localize(ShellText.English);
}

/// <summary>Formats an <see cref="IShellLocalizableError"/> in a language.</summary>
public static class ShellLocalizableErrorExtensions
{
    /// <summary>Gets the error's text in the language of <paramref name="texts"/>.</summary>
    /// <param name="error">The error.</param>
    /// <param name="texts">The shell's texts (<c>Strings.For(culture)</c>).</param>
    public static string Localize(this IShellLocalizableError error, IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(texts);
        object[] arguments = [.. error.TextArguments.Select(argument => argument is IShellLocalizableError nested ? nested.Localize(texts) : argument)];
        return texts[error.TextKey, arguments];
    }
}
