using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace Kazo.Os.Commands.True;

/// <summary><c>true</c>: does nothing and exits with 0, for <c>while true</c> and <c>cmd || true</c> (ADR 0064).</summary>
public sealed class TrueCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(0);
}
