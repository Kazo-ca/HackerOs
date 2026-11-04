using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Which;

/// <summary>
/// <c>which [-a] NAME...</c> (ADR 0064): prints where each installed command lives, as
/// <c>/usr/bin/NAME</c> (the shell's <c>sh</c> as <c>/bin/sh</c>); exit 1 when one is missing.
/// The list comes from the calling shell (<see cref="IShellSession.GetCommands()"/>), like <c>PATH</c>.
/// </summary>
public sealed class WhichCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["NoShell"]);
            return 1;
        }

        IReadOnlyList<ShellCommandInfo> commands = shell.GetCommands();
        int status = 0;
        foreach (string name in context.Arguments.Where(argument => !argument.StartsWith('-')))
        {
            ShellCommandInfo? command = commands.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.Ordinal) || c.Aliases.Contains(name, StringComparer.Ordinal));
            if (command is null || command.Description.EndsWith("(shell builtin)", StringComparison.Ordinal))
            {
                status = 1;
                continue;
            }

            await context.StandardOutput.WriteLineAsync(name is "sh" or "bash" or "hsh" ? $"/bin/{name}" : $"/usr/bin/{name}");
        }

        return status;
    }
}
