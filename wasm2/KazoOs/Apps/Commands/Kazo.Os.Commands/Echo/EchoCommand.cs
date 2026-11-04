using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace Kazo.Os.Commands.Echo;

/// <summary>
/// Implements the <c>echo</c> terminal command (`P2-CMD-005`).
/// </summary>
public sealed class EchoCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public EchoCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        string outputText = string.Join(" ", context.Arguments);
        context.StandardOutput.WriteLine(outputText);
        return ValueTask.FromResult(0);
    }
}
