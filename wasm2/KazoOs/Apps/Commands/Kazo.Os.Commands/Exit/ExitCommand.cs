using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Exit;

/// <summary>
/// Implements <c>exit [n]</c> (ADR 0058, section 4). Like the bash built-in, it ends the shell
/// that ran it, through <see cref="TerminalExecutionContext.Shell"/> (capability
/// <c>shell.control</c>): the rest of the line is not run, the shell's process ends with
/// <c>n</c> (or <c>$?</c>), and the terminal closes the tab.
/// </summary>
public sealed class ExitCommand : TerminalAppBase
{
    /// <summary>Exit status of a non-numeric argument, as bash: the shell still exits.</summary>
    private const int NumericArgumentRequired = 2;

    /// <summary>Initializes the command with its manifest.</summary>
    public ExitCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["NoShell"]);
            return 1;
        }

        if (context.Arguments.Count == 0)
        {
            return Exit(shell, null);
        }

        string argument = context.Arguments[0];
        if (!int.TryParse(argument, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int status))
        {
            // bash: "numeric argument required", then it exits anyway with 2.
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["NumericRequired", argument]);
            return Exit(shell, NumericArgumentRequired);
        }

        if (context.Arguments.Count > 1)
        {
            // bash refuses and keeps the shell running.
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["TooManyArguments"]);
            return 1;
        }

        return Exit(shell, status);
    }

    /// <summary>Asks the shell to exit; its returned status (0 to 255) becomes this command's.</summary>
    private static int Exit(IShellSession shell, int? status)
    {
        try
        {
            return shell.Exit(status);
        }
        catch (NotSupportedException)
        {
            return 1;
        }
    }
}
