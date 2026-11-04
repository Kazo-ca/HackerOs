using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace Kazo.Os.Commands.Clear;

/// <summary>
/// Implements <c>clear</c> (ADR 0051, section 4). Like ncurses' <c>clear</c>, it only writes the
/// terminal's "home, erase screen, erase scrollback" sequence to its output; the terminal
/// interprets it. Redirected (<c>clear &gt; f</c>), the sequence lands in the file, as on Linux.
/// </summary>
public sealed class ClearCommand : TerminalAppBase
{
    /// <summary>Cursor home, erase display, erase scrollback (<c>ESC[H ESC[2J ESC[3J</c>).</summary>
    public const string ClearSequence = "\x1b[H\x1b[2J\x1b[3J";

    /// <summary>Initializes the command with its manifest.</summary>
    public ClearCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        await context.StandardOutput.WriteAsync(ClearSequence);
        return 0;
    }
}
