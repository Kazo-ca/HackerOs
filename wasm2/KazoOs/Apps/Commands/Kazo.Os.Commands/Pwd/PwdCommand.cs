using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace Kazo.Os.Commands.Pwd;

/// <summary>
/// Implements the <c>pwd</c> terminal command (`P2-CMD-001`).
/// </summary>
public sealed class PwdCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public PwdCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        context.StandardOutput.WriteLine(context.WorkingDirectory);
        return ValueTask.FromResult(0);
    }
}
