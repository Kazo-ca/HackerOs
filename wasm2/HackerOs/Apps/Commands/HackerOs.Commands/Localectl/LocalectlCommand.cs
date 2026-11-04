using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Localectl;

/// <summary>
/// Implements <c>localectl</c> (ADR 0104, section 2), the systemd tool that reads and writes the
/// system locale in <c>/etc/locale.conf</c>:
/// <list type="bullet">
/// <item><c>localectl</c> / <c>localectl status</c>: prints the variables of <c>/etc/locale.conf</c>.</item>
/// <item><c>localectl list-locales</c>: prints the locales KazoOS knows, one per line.</item>
/// <item><c>localectl set-locale LANG=fr_CA.UTF-8 [LC_TIME=…]</c>: replaces the file. Like on Linux,
/// only root may do it (<c>sudo localectl set-locale …</c>); the file's owner and mode decide.</item>
/// </list>
/// The new system locale applies to the sign-in screen at the next boot or sign-out, and to every
/// user without <c>~/.config/locale.conf</c> at their next sign-in.
/// </summary>
public sealed class LocalectlCommand : TerminalAppBase
{
    private static readonly VirtualPath s_localeFile = VirtualPath.Parse("/etc/locale.conf");

    /// <summary>Initializes the command with its manifest.</summary>
    public LocalectlCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        string verb = context.Arguments.Count == 0 ? "status" : context.Arguments[0];
        switch (verb)
        {
            case "status":
                return await StatusAsync(context, text, cancellationToken);
            case "list-locales":
                foreach (string locale in HackLocale.AvailableLocales)
                {
                    await context.StandardOutput.WriteLineAsync(locale);
                }

                return 0;
            case "set-locale":
                return await SetLocaleAsync(context, text, cancellationToken);
            default:
                await context.StandardError.WriteLineAsync(text["UnknownVerb", verb]);
                return 1;
        }
    }

    /// <summary>Prints the system locale like systemd's <c>localectl status</c>.</summary>
    private static async Task<int> StatusAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, CancellationToken cancellationToken)
    {
        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(context.App.FileSystem, s_localeFile, cancellationToken);
        if (read.Error is { Code: not FileSystemErrorCode.NotFound } error)
        {
            await context.StandardError.WriteLineAsync(text["ReadFailed", error.Code]);
            return 1;
        }

        IReadOnlyDictionary<string, string> variables = HackLocale.ParseLocaleConf(read.Content);
        string label = text["SystemLocale"];
        if (variables.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync($"{label}: {text["NotSet"]}");
            return 0;
        }

        string indent = new(' ', label.Length + 2);
        bool first = true;
        foreach (KeyValuePair<string, string> variable in variables.OrderBy(v => v.Key == HackLocale.LangVariable ? 0 : 1).ThenBy(v => v.Key, StringComparer.Ordinal))
        {
            await context.StandardOutput.WriteLineAsync($"{(first ? label + ": " : indent)}{variable.Key}={variable.Value}");
            first = false;
        }

        return 0;
    }

    /// <summary>Validates <c>VARIABLE=LOCALE</c> assignments and replaces <c>/etc/locale.conf</c>.</summary>
    private static async Task<int> SetLocaleAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, CancellationToken cancellationToken)
    {
        if (context.Arguments.Count < 2)
        {
            await context.StandardError.WriteLineAsync(text["MissingLocale"]);
            return 1;
        }

        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        foreach (string argument in context.Arguments.Skip(1))
        {
            // A bare locale means LANG, as in "localectl set-locale fr_FR.UTF-8".
            int equals = argument.IndexOf('=', StringComparison.Ordinal);
            string name = equals < 0 ? HackLocale.LangVariable : argument[..equals];
            string value = equals < 0 ? argument : argument[(equals + 1)..];
            if (!HackLocale.LocaleConfVariables.Contains(name))
            {
                await context.StandardError.WriteLineAsync(text["InvalidVariable", name]);
                return 1;
            }

            if (name != "LANGUAGE" && !HackLocale.IsAvailable(value))
            {
                await context.StandardError.WriteLineAsync(text["InvalidLocale", value]);
                return 1;
            }

            variables[name] = value;
        }

        string content = HackLocale.FormatLocaleConf(variables, "System locale, written by localectl (ADR 0104).");
        FileSystemError? failure = await SettingsTextFile.WriteAllAsync(context.App.FileSystem, s_localeFile, content, cancellationToken);
        if (failure is not null)
        {
            await context.StandardError.WriteLineAsync(failure.Code is FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.AuthorityDenied
                ? text["AccessDenied"]
                : text["WriteFailed", failure.Code]);
            return 1;
        }

        return 0;
    }
}
