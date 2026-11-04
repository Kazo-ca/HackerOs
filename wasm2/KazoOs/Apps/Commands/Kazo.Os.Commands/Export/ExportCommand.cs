using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Export;

/// <summary>
/// Implements <c>export [NAME=value...]</c> (ADR 0051, section 4): sets variables of the calling
/// shell through <see cref="IShellSession.SetVariable"/>, so every command started afterwards
/// inherits them (ADR 0050, section 5). Without arguments it lists the environment as bash's
/// <c>declare -x</c> lines. <c>export NAME</c> alone keeps an existing variable, or exports it empty.
/// </summary>
public sealed class ExportCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public ExportCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["NoShell"]);
            return 1;
        }

        if (context.Arguments.Count == 0 || context.Arguments is ["-p"])
        {
            foreach ((string name, string value) in shell.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                await context.StandardOutput.WriteLineAsync($"declare -x {name}=\"{Quote(value)}\"");
            }

            return 0;
        }

        int status = 0;
        foreach (string argument in context.Arguments)
        {
            int equals = argument.IndexOf('=');
            string name = equals < 0 ? argument : argument[..equals];
            string value = equals < 0
                ? shell.GetVariable(name) ?? string.Empty
                : argument[(equals + 1)..];
            try
            {
                shell.SetVariable(name, value);
            }
            catch (ArgumentException)
            {
                await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["InvalidIdentifier", argument]);
                status = 1;
            }
        }

        return status;
    }

    /// <summary>Escapes a value for a double-quoted <c>declare -x</c> line, as bash prints it.</summary>
    private static string Quote(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal);
}
