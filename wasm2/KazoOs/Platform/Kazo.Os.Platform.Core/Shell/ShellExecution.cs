using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>A single-use elevation ticket (ADR 0042) handed to the next command the shell launches.</summary>
/// <param name="Token">Ticket token minted by <c>IElevationService</c>.</param>
/// <param name="Scope">What the ticket authorizes.</param>
public sealed record ShellElevation(Guid Token, ElevationScope Scope);

/// <summary>How one line is run by <see cref="ShellInterpreter.ExecuteAsync"/>.</summary>
public sealed class ShellExecutionOptions
{
    /// <summary>
    /// Gets where the last stage of each pipeline writes when it is not redirected: the terminal,
    /// or a buffer for a non-interactive shell. Never closed by the shell.
    /// </summary>
    public required TextWriter StandardOutput { get; init; }

    /// <summary>Gets where every stage writes its errors, and the shell its own messages. Never closed by the shell.</summary>
    public required TextWriter StandardError { get; init; }

    /// <summary>
    /// Gets what the first stage of each pipeline reads when it is not redirected (a hosted
    /// shell's terminal input, ADR 0056 section 7); <see langword="null"/> for an empty input.
    /// Never closed by the shell.
    /// </summary>
    public TextReader? StandardInput { get; init; }

    /// <summary>Gets the optional alternate screen offered to commands such as <c>nano</c>.</summary>
    public IFullScreenTerminalSession? FullScreen { get; init; }

    /// <summary>Gets the prompt for pending dangerous capabilities (ADR 0049 section 5), e.g. the terminal's text mode.</summary>
    public IDangerousCapabilityApprovalPrompt? ApprovalPrompt { get; init; }

    /// <summary>
    /// Gets the administrator credential prompt <c>sudo</c> goes through
    /// (<see cref="IShellSession.RunElevatedAsync"/>, ADR 0051 section 4): it receives a description
    /// of the command and returns a <c>SudoRoot</c> ticket (ADR 0042), or <see langword="null"/>
    /// when the prompt was cancelled or the credentials refused. Without it, <c>sudo</c> fails.
    /// </summary>
    public Func<string, CancellationToken, Task<ShellElevation?>>? CredentialPrompt { get; init; }

    /// <summary>
    /// Gets the callback offered a one-shot approval (ADR 0042) when a single-command pipeline
    /// failed on a permission denial; returning a ticket reruns the command with it.
    /// </summary>
    public Func<ShellPermissionDenial, CancellationToken, Task<ShellElevation?>>? PermissionApproval { get; init; }

    /// <summary>Gets a ticket consumed by the first command this line launches (e.g. an <c>ExecuteCommandIntent</c> retry).</summary>
    public ShellElevation? Elevation { get; init; }
}

/// <summary>A permission denial a command hit, offered to <see cref="ShellExecutionOptions.PermissionApproval"/>.</summary>
/// <param name="CommandName">The command as typed (after alias expansion).</param>
/// <param name="Error">The first filesystem denial the command raised.</param>
public sealed record ShellPermissionDenial(string CommandName, FileSystemError Error);

/// <summary>Why the last stage that ran did not complete normally, when it did not.</summary>
public enum ShellFailure
{
    /// <summary>The stage ran (whatever its exit code).</summary>
    None,

    /// <summary>The line did not parse; nothing ran (exit 2).</summary>
    Syntax,

    /// <summary>No installed command has this name (exit 127).</summary>
    CommandNotFound,

    /// <summary>The command exists but is uninstalled; the shell says "command not found" (exit 127).</summary>
    Disabled,

    /// <summary>The elevation ticket was refused (exit 1).</summary>
    ElevationDenied,

    /// <summary>The command's entry point threw (exit 1).</summary>
    Fault,

    /// <summary>A redirection target could not be opened; the stage did not start (exit 1).</summary>
    Redirection
}

/// <summary>Outcome of one line.</summary>
/// <param name="ExitCode">Exit code of the last pipeline that ran (also stored in <see cref="ShellSession.LastExitCode"/>).</param>
/// <param name="Failure">Why the last stage that ran failed to run, if it did.</param>
/// <param name="SyntaxError">The syntax error, when the line did not parse.</param>
/// <param name="PermissionDenials">Filesystem permission denials raised by the commands of the line.</param>
public sealed record ShellExecutionResult(
    int ExitCode,
    ShellFailure Failure,
    ShellSyntaxError? SyntaxError,
    IReadOnlyList<FileSystemError> PermissionDenials);
