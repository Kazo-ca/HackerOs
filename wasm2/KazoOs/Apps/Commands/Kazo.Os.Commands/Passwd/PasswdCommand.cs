using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Passwd;

/// <summary>
/// Implements <c>passwd [-d] [-S] [user]</c> (ADR 0120, section 5). Without <c>sudo</c> it only
/// changes the caller's own password, after asking the current one, as the setuid <c>passwd</c> of
/// shadow-utils does through PAM. Under <c>sudo</c> it sets (<c>passwd NAME</c>) or removes
/// (<c>passwd -d NAME</c>) any account's password without asking the old one. Passwords are read
/// with the terminal's echo off (<see cref="TerminalExecutionContext.ReadSecretLineAsync"/>).
/// </summary>
public sealed class PasswdCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public PasswdCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        bool delete = false;
        bool status = false;
        string? target = null;
        foreach (string argument in context.Arguments)
        {
            switch (argument)
            {
                case "-d" or "--delete":
                    delete = true;
                    break;
                case "-S" or "--status":
                    status = true;
                    break;
                case "-h" or "--help":
                    await context.StandardOutput.WriteLineAsync(text["Usage"]);
                    return 0;
                default:
                    if (argument.StartsWith('-') || target is not null)
                    {
                        await context.StandardError.WriteLineAsync(text["Usage"]);
                        return 2;
                    }

                    target = argument;
                    break;
            }
        }

        if (delete && status)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        bool root = context.App.IsRootElevated;
        string self = context.App.UserLoginName;
        if (target is null && root)
        {
            // Kazo OS has no root account with a password of its own: name the account.
            await context.StandardError.WriteLineAsync(text["NoRootAccount"]);
            return 1;
        }

        target ??= self;
        bool other = !string.Equals(target, self, StringComparison.OrdinalIgnoreCase);
        if ((other || delete) && !root)
        {
            await context.StandardError.WriteLineAsync(text["NotAllowed", target]);
            return 1;
        }

        try
        {
            if (status)
            {
                return await PrintStatusAsync(context, text, target, cancellationToken);
            }

            if (delete)
            {
                await KazoUserAccounts.RemovePasswordAsync(context.App, target, cancellationToken);
                await context.StandardOutput.WriteLineAsync(text["Deleted", target]);
                return 0;
            }

            await context.StandardOutput.WriteLineAsync(text["Changing", target]);
            string? current = null;
            if (!root)
            {
                IReadOnlyList<KazoUserAccount> accounts = await KazoUserAccounts.ListAsync(context.App, cancellationToken);
                bool hasPassword = accounts.FirstOrDefault(account => account.IsCurrentUser)?.HasPassword ?? true;
                current = hasPassword ? await context.ReadSecretLineAsync(text["CurrentPrompt"], cancellationToken) ?? string.Empty : string.Empty;
            }

            string? first = await context.ReadSecretLineAsync(text["NewPrompt"], cancellationToken);
            if (KazoUserAccounts.ValidateNewPassword(first) is { } reason)
            {
                await context.StandardError.WriteLineAsync(text["BadPassword", reason]);
                await context.StandardError.WriteLineAsync(text["Unchanged"]);
                return 1;
            }

            string? second = await context.ReadSecretLineAsync(text["RetypePrompt"], cancellationToken);
            if (!string.Equals(first, second, StringComparison.Ordinal))
            {
                await context.StandardError.WriteLineAsync(text["Mismatch"]);
                await context.StandardError.WriteLineAsync(text["Unchanged"]);
                return 10;
            }

            if (root)
            {
                await KazoUserAccounts.ResetPasswordAsync(context.App, target, first!, cancellationToken);
            }
            else
            {
                await KazoUserAccounts.ChangePasswordAsync(context.App, current!, first!, cancellationToken);
            }

            await context.StandardOutput.WriteLineAsync(text["Updated"]);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["TokenError"]);
            await context.StandardError.WriteLineAsync(text["Unchanged"]);
            return 10;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // The service's messages already read like passwd's ("passwd: user 'x' does not exist").
            string message = exception is ArgumentException argument ? StripParameter(argument) : exception.Message;
            await context.StandardError.WriteLineAsync(message.StartsWith("passwd:", StringComparison.Ordinal) ? message : $"passwd: {message}");
            return 1;
        }
    }

    /// <summary>Prints one <c>passwd -S</c> line: login, P/NP/L, date of the last change, and shadow-utils' fixed ageing fields.</summary>
    private static async ValueTask<int> PrintStatusAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        string target,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<KazoUserAccount> accounts = await KazoUserAccounts.ListAsync(context.App, cancellationToken);
        KazoUserAccount? account = accounts.FirstOrDefault(
            candidate => string.Equals(candidate.LoginName, target, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            await context.StandardError.WriteLineAsync(text["UnknownUser", target]);
            return 1;
        }

        string state = !account.IsEnabled ? "L" : account.HasPassword ? "P" : "NP";
        string date = account.UpdatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await context.StandardOutput.WriteLineAsync($"{account.LoginName} {state} {date} 0 99999 7 -1");
        return 0;
    }

    private static string StripParameter(ArgumentException exception) =>
        exception.ParamName is { } name
            ? exception.Message.Replace($" (Parameter '{name}')", string.Empty, StringComparison.Ordinal)
            : exception.Message;
}
