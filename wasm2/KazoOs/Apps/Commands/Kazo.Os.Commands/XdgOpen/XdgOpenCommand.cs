using System.Text.RegularExpressions;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.XdgOpen;

/// <summary>
/// Implements <c>xdg-open &lt;file|folder|URI&gt;</c> (ADR 0096, section 3), modelled on freedesktop's
/// <c>xdg-open</c>: a path opens with its file association (a folder in the default file manager,
/// <c>inode/directory</c>), a URI with the handler of its scheme (<c>x-scheme-handler/&lt;scheme&gt;</c>,
/// the web browser for <c>http</c>/<c>https</c>). It returns as soon as the request is sent.
/// </summary>
/// <remarks>
/// Exit codes follow <c>xdg-open</c>: 0 sent, 1 bad command line, 3 no application handles it,
/// 4 the file does not exist or the action failed (several apps and no default included: a terminal
/// has no chooser and the OS never guesses).
/// </remarks>
public sealed partial class XdgOpenCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    /// <param name="manifest">This command's manifest.</param>
    public XdgOpenCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        if (context.Arguments.Count != 1 || string.IsNullOrWhiteSpace(context.Arguments[0]))
        {
            context.StandardError.WriteLine(text["Error", text[context.Arguments.Count > 1 ? "UnexpectedArgument" : "NoTarget"]]);
            context.StandardError.WriteLine(text["Usage"]);
            return 1;
        }

        string target = context.Arguments[0];
        if (target is "--help" or "-h")
        {
            context.StandardOutput.WriteLine(text["Usage"]);
            return 0;
        }

        try
        {
            return SchemePattern().IsMatch(target) && Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
                ? await OpenUriAsync(context, uri, cancellationToken)
                : await OpenPathAsync(context, target, cancellationToken);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            context.StandardError.WriteLine(text["Error", exception.Message]);
            return 4;
        }
    }

    private static async Task<int> OpenPathAsync(TerminalExecutionContext context, string target, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        VirtualPath path;
        try
        {
            path = VirtualPath.Parse(KazoPath.GetFullPath(target));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            context.StandardError.WriteLine(text["Error", text["InvalidPath", target]]);
            return 1;
        }

        bool isDirectory = await KazoDirectory.ExistsAsync(path.Value, cancellationToken);
        if (!isDirectory && !await KazoFile.ExistsAsync(path.Value, cancellationToken))
        {
            context.StandardError.WriteLine(text["Error", text["FileMissing", target]]);
            return 4;
        }

        // Like Linux, a folder is a file of type inode/directory: its handler is the file manager.
        AppIntentOpenFileResult result = await context.App.Intents.OpenFileAsync(
            path, isDirectory ? "inode/directory" : null, cancellationToken);
        return result.Outcome switch
        {
            AppIntentOpenFileOutcome.Opened => 0,
            AppIntentOpenFileOutcome.NoHandler => Fail(context, 3, text["NoHandler", target]),
            AppIntentOpenFileOutcome.ChooserRequired => Fail(
                context, 4,
                text["Ambiguous", target, string.Join(", ", result.CandidateAppIds ?? []), path.Value]),
            _ => Fail(context, 4, text["OpenFailed", target, result.ErrorCode ?? string.Empty])
        };
    }

    private static async Task<int> OpenUriAsync(TerminalExecutionContext context, Uri uri, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        AppIntentResult result = await context.App.Intents.OpenUriAsync(uri, cancellationToken: cancellationToken);
        return result.Outcome switch
        {
            AppIntentOutcome.Dispatched => 0,
            AppIntentOutcome.NoHandler => Fail(context, 3, text["NoSchemeHandler", uri.Scheme]),
            AppIntentOutcome.NotFound => Fail(context, 4, text["FileMissing", uri.LocalPath]),
            AppIntentOutcome.ChooserRequired => Fail(
                context, 4,
                text["SchemeAmbiguous", uri.Scheme, string.Join(", ", result.CandidateAppIds ?? [])]),
            AppIntentOutcome.Invalid => Fail(context, 1, text["InvalidUri", uri.OriginalString]),
            _ => Fail(context, 4, text["OpenFailed", uri.OriginalString, result.ErrorCode ?? string.Empty])
        };
    }

    private static int Fail(TerminalExecutionContext context, int exitCode, string message)
    {
        context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["Error", message]);
        return exitCode;
    }

    /// <summary>A URI scheme of two characters or more, so a path never reads as one.</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]+:", RegexOptions.CultureInvariant)]
    private static partial Regex SchemePattern();
}
