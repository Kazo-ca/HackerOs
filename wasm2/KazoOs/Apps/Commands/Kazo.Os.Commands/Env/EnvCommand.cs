using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Env;

/// <summary>
/// Implements <c>env</c> (ADR 0051, section 4): prints its own environment, inherited from the
/// shell when it started, one <c>NAME=value</c> line per variable, like <c>/usr/bin/env</c>. It
/// needs no access to the shell. <c>env NAME=value command</c> is out of scope.
/// </summary>
public sealed class EnvCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public EnvCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Arguments.Count > 0)
        {
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["CommandNotSupported"]);
            return 125;
        }

        foreach ((string name, string value) in context.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            await context.StandardOutput.WriteLineAsync($"{name}={value}");
        }

        return 0;
    }
}
