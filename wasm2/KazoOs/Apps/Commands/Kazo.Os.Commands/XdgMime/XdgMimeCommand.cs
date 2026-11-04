using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.XdgMime;

/// <summary>
/// Implements <c>xdg-mime</c> (ADR 0121, section 5), modelled on freedesktop's <c>xdg-mime</c>:
/// <c>query default &lt;type&gt;</c> prints the app that opens a type for this user (their own
/// choice, else the machine's), <c>default &lt;app&gt; &lt;type&gt;...</c> writes the user's choice to
/// <c>~/.config/hackeros/file-associations.json</c>, and <c>query filetype &lt;file&gt;</c> prints the
/// type the OS matches a file by.
/// </summary>
/// <remarks>
/// A type is a media type (<c>text/markdown</c>, <c>x-scheme-handler/https</c>) or, in Kazo OS, an
/// extension with its dot (<c>.md</c>). An app is named by its ID; a trailing <c>.desktop</c>, as
/// freedesktop writes it, is ignored. Exit codes follow <c>xdg-mime</c>: 0 done, 1 bad command
/// line, 2 the file does not exist, 4 the action failed.
/// </remarks>
public sealed class XdgMimeCommand : TerminalAppBase
{
    private const string DirectoryMediaType = "inode/directory";
    private const string UnknownMediaType = "application/octet-stream";

    /// <summary>Initializes the command with its manifest.</summary>
    /// <param name="manifest">This command's manifest.</param>
    public XdgMimeCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<string> arguments = context.Arguments;

        if (arguments.Count == 1 && arguments[0] is "--help" or "-h")
        {
            context.StandardOutput.WriteLine(text["Usage"]);
            return 0;
        }

        try
        {
            return arguments switch
            {
                ["query", "default", string type] => await QueryDefaultAsync(context, type, cancellationToken),
                ["query", "filetype", string file] => await QueryFileTypeAsync(context, file, cancellationToken),
                ["default", string app, _, ..] => await SetDefaultAsync(context, app, arguments.Skip(2).ToArray(), cancellationToken),
                _ => Usage(context)
            };
        }
        catch (KazoUnauthorizedAccessException exception)
        {
            return Fail(context, 4, exception.Message);
        }
    }

    /// <summary>Prints the app the OS uses to open <paramref name="type"/> for this user, or nothing.</summary>
    private static async Task<int> QueryDefaultAsync(TerminalExecutionContext context, string type, CancellationToken cancellationToken)
    {
        string target = type.Trim().ToLowerInvariant();
        IReadOnlyList<KazoFileAssociation> defaults = await KazoApps.GetDefaultHandlersAsync(cancellationToken);

        // The user's rows come first: the first match is the one the OS uses.
        KazoFileAssociation? match = defaults.FirstOrDefault(row =>
            string.Equals(row.Target, target, StringComparison.Ordinal) && string.Equals(row.Action, "open", StringComparison.Ordinal));
        if (match is not null)
        {
            context.StandardOutput.WriteLine(match.AppId);
        }

        return 0;
    }

    /// <summary>
    /// Prints the type a file is matched by: <c>inode/directory</c> for a folder, else the media type
    /// an installed app declares for its extension, else the extension itself, else
    /// <c>application/octet-stream</c>. Kazo OS has no shared-mime-info database (ADR 0121, section 5).
    /// </summary>
    private static async Task<int> QueryFileTypeAsync(TerminalExecutionContext context, string file, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string path;
        try
        {
            path = KazoPath.GetFullPath(file);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Fail(context, 1, text["InvalidPath", file]);
        }

        if (await KazoDirectory.ExistsAsync(path, cancellationToken))
        {
            context.StandardOutput.WriteLine(DirectoryMediaType);
            return 0;
        }

        if (!await KazoFile.ExistsAsync(path, cancellationToken))
        {
            return Fail(context, 2, text["FileMissing", file]);
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension.Length == 0)
        {
            context.StandardOutput.WriteLine(UnknownMediaType);
            return 0;
        }

        IReadOnlyList<KazoAppInfo> apps = await KazoApps.GetInstalledAsync(cancellationToken);
        string? mediaType = apps
            .SelectMany(static app => app.FileHandlers)
            .Where(handler => handler.MediaType is not null
                && handler.Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
            .Select(static handler => handler.MediaType!.ToLowerInvariant())
            .OrderBy(static type => type, StringComparer.Ordinal)
            .FirstOrDefault();
        context.StandardOutput.WriteLine(mediaType ?? extension);
        return 0;
    }

    /// <summary>Makes <paramref name="app"/> the user's default for each type; stops at the first type that fails.</summary>
    private static async Task<int> SetDefaultAsync(
        TerminalExecutionContext context, string app, IReadOnlyList<string> types, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string appId = app.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? app[..^".desktop".Length] : app;
        foreach (string type in types)
        {
            KazoDefaultHandlerStatus status = await KazoApps.SetDefaultHandlerAsync(type, appId, "open", cancellationToken);
            int exitCode = status switch
            {
                KazoDefaultHandlerStatus.Changed => 0,
                KazoDefaultHandlerStatus.InvalidTarget => Fail(context, 1, text["InvalidType", type]),
                KazoDefaultHandlerStatus.AppNotFound => Fail(context, 4, text["AppNotFound", appId]),
                KazoDefaultHandlerStatus.AppCannotHandle => Fail(context, 4, text["AppCannotHandle", appId, type]),
                KazoDefaultHandlerStatus.FileInvalid => Fail(context, 4, text["FileInvalid"]),
                _ => Fail(context, 4, text["WriteFailed"])
            };
            if (exitCode != 0)
            {
                return exitCode;
            }
        }

        return 0;
    }

    private static int Usage(TerminalExecutionContext context)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        context.StandardError.WriteLine(text["Error", text["BadCommandLine"]]);
        context.StandardError.WriteLine(text["Usage"]);
        return 1;
    }

    private static int Fail(TerminalExecutionContext context, int exitCode, string message)
    {
        context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["Error", message]);
        return exitCode;
    }
}
