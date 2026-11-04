using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Base class for a command executable hosted by a Kazo OS terminal session.
/// </summary>
public abstract class TerminalAppBase : AppBase
{
    /// <summary>Initializes a terminal application with its validated manifest.</summary>
    /// <param name="manifest">Terminal application manifest.</param>
    protected TerminalAppBase(AppManifest manifest)
        : base(manifest, AppKind.Terminal)
    {
    }

    /// <summary>
    /// Executes the command using renderer-independent streams.
    /// </summary>
    /// <param name="context">Command streams, arguments, and shell state.</param>
    /// <param name="cancellationToken">Signals command interruption or OS shutdown.</param>
    /// <returns>The command exit status, where zero indicates success.</returns>
    public abstract ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken);
}