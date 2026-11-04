using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Sudo;

/// <summary>
/// Implements <c>sudo command [args...]</c> (ADR 0051, section 4; ADR 0042). The command itself
/// holds no authority: it asks its shell (<see cref="IShellSession.RunElevatedAsync"/>), which owns
/// the credential prompt and the single-use <c>SudoRoot</c> ticket, to run the target as root with
/// sudo's own output. Only one command is elevated; a redirection was opened by the unelevated
/// shell, so <c>sudo echo x &gt; /etc/f</c> fails as on Linux.
/// </summary>
public sealed class SudoCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public SudoCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        if (context.Arguments.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 1;
        }

        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(text["NoShell"]);
            return 1;
        }

        return await shell.RunElevatedAsync(context.Arguments, context.StandardOutput, context.StandardError, cancellationToken);
    }
}
