using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Help;

/// <summary>
/// Implements <c>help [command]</c> / <c>man</c> (ADR 0051, section 4): lists the commands
/// installed right now, read from the shell in the language of the command's process
/// (<see cref="IShellSession.GetCommands(System.Globalization.CultureInfo)"/>), or prints
/// one command's usage. Nothing is hard-coded: a third-party command appears as soon as it is
/// installed and disappears when it is uninstalled.
/// </summary>
public sealed class HelpCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public HelpCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        TextWriter output = context.StandardOutput;
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(text["NoShell"]);
            return 1;
        }

        IReadOnlyList<ShellCommandInfo> commands = shell.GetCommands(context.App.Culture);
        if (context.Arguments.Count > 0)
        {
            int status = 0;
            foreach (string name in context.Arguments)
            {
                ShellCommandInfo? command = commands.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
                    || c.Aliases.Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)));
                if (command is null)
                {
                    await context.StandardError.WriteLineAsync(text["NoMatch", name]);
                    status = 1;
                    continue;
                }

                await output.WriteLineAsync($"{command.Name} - {command.Description}");
                await output.WriteLineAsync(text["Usage", command.Usage]);
                if (command.Aliases.Count > 0)
                {
                    await output.WriteLineAsync(text["Aliases", string.Join(", ", command.Aliases)]);
                }
            }

            return status;
        }

        await output.WriteLineAsync(text["Header"]);
        int width = commands.Count == 0 ? 0 : commands.Max(c => c.Name.Length);
        foreach (ShellCommandInfo command in commands)
        {
            await output.WriteLineAsync($"  {command.Name.PadRight(width)}  {command.Description}");
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync(text["Operators", Array.Empty<object>()]);
        await output.WriteLineAsync(text["Footer"]);
        return 0;
    }
}
