using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Lifecycle;

/// <summary>Requests that one catalog app be started for one authenticated user, per `P1-APP-004`.</summary>
/// <param name="AppId">Exact target app ID.</param>
/// <param name="Principal">Authenticated principal the new process will run as.</param>
/// <param name="Arguments">
/// Opaque launch arguments. For <see cref="Kazo.Os.App.Abstractions.AppKind.Terminal"/> apps
/// these become <see cref="TerminalExecutionContext.Arguments"/>.
/// </param>
/// <param name="ParentPid">Optional parent process, when launched as a child of another process.</param>
/// <param name="WorkingDirectory">
/// Initial working directory of the new process (e.g. the invoking shell's current directory,
/// ADR 0050 section 5); when omitted, the parent's directory is inherited, or the principal's home
/// for a process with no parent.
/// </param>
/// <param name="ElevationToken">
/// Optional single-use elevation ticket token (`ADR 0042`), minted by <see cref="IElevationService"/>.
/// Consumed exactly once against <see cref="ElevationScope"/> before this launch proceeds.
/// </param>
/// <param name="ElevationScope">The scope the ticket in <see cref="ElevationToken"/> authorizes.</param>
/// <param name="ApprovalPrompt">
/// Prompt for pending dangerous capabilities (ADR 0049 section 5); the terminal passes its text
/// mode. <see langword="null"/> uses the host's default (the secure-desktop overlay).
/// </param>
/// <param name="Interactive">
/// <see langword="false"/> for a launch nobody is watching (a service auto-started with the
/// session): pending dangerous capabilities are then deferred instead of prompted.
/// </param>
/// <param name="Environment">
/// Environment variables applied over the inherited ones (ADR 0050, section 5): the parent's when
/// <see cref="ParentPid"/> is set, otherwise the login environment.
/// </param>
/// <param name="StandardInput">
/// Standard input of a <see cref="Kazo.Os.App.Abstractions.AppKind.Terminal"/> app (ADR 0050,
/// section 4): a pipe from the previous pipeline stage. <see langword="null"/> gives an empty
/// input (end of stream at once, like <c>/dev/null</c>).
/// </param>
/// <param name="StandardOutput">
/// Standard output of a Terminal app: the terminal, a pipe or a redirected file. When supplied, the
/// command writes there as it runs and <see cref="AppLaunchResult.StandardOutput"/> stays empty;
/// when <see langword="null"/>, the output is buffered and returned in the result.
/// </param>
/// <param name="StandardError">Standard error of a Terminal app, with the same rule as <see cref="StandardOutput"/>.</param>
/// <param name="Shell">
/// The interactive shell starting a Terminal app (ADR 0051, section 3). It reaches the command as
/// <see cref="TerminalExecutionContext.Shell"/> only when the command's granted capabilities
/// include <see cref="AppCapabilities.ShellControl"/>.
/// </param>
/// <param name="CommandName">
/// The name the command was invoked as (a manifest alias such as <c>unalias</c>), which becomes
/// <c>argv[0]</c> like on Linux; the manifest's command name when omitted.
/// </param>
/// <param name="ProcessStarted">
/// Called with the new process as soon as it exists, before the app runs (ADR 0064): the shell
/// records the process ids of a background job (<c>$!</c>, <c>jobs -l</c>, <c>kill %n</c>).
/// </param>
/// <param name="SystemScope">
/// Whether the process belongs to the system rather than to the login session (ADR 0097, section 4):
/// a service started at boot under the service account, which a logout does not stop.
/// </param>
/// <param name="Intent">
/// The intent this launch carries (ADR 0096, section 5): the new process reads it as
/// <see cref="IAppExecutionContext.Intent"/>; a running single instance receives it through
/// <see cref="IAppExecutionContext.IntentReceived"/> instead of a second process.
/// </param>
public sealed record AppLaunchRequest(
    string AppId,
    AuthenticatedPrincipal Principal,
    IReadOnlyList<string> Arguments,
    ProcessId? ParentPid = null,
    VirtualPath? WorkingDirectory = null,
    Guid? ElevationToken = null,
    ElevationScope? ElevationScope = null,
    IDangerousCapabilityApprovalPrompt? ApprovalPrompt = null,
    bool Interactive = true,
    IReadOnlyDictionary<string, string>? Environment = null,
    TextReader? StandardInput = null,
    TextWriter? StandardOutput = null,
    TextWriter? StandardError = null,
    IShellSession? Shell = null,
    string? CommandName = null,
    Action<ProcessRecord>? ProcessStarted = null,
    bool SystemScope = false,
    AppIntentDelivery? Intent = null)
{
    /// <summary>
    /// Gets the niceness the new process starts with (ADR 0127): <c>nice -n 5 make</c> sets it;
    /// <see langword="null"/> inherits the parent's (<see cref="ProcessStartRequest.Nice"/>).
    /// </summary>
    public int? Nice { get; init; }
}

/// <summary>Identifies the stable outcome of one launch attempt.</summary>
public enum AppLaunchStatus
{
    /// <summary>A new process and execution context were created and started.</summary>
    Launched,

    /// <summary>An existing singleton instance was found; no new process was created (`P1-APP-006`).</summary>
    FocusedExisting,

    /// <summary>No catalog app has this ID.</summary>
    NotFound,

    /// <summary>The app is currently disabled.</summary>
    Disabled,

    /// <summary>The entry point threw while starting or executing.</summary>
    EntryPointFault,

    /// <summary>The supplied elevation token was missing, expired, wrong-scope, or already consumed (`ADR 0042`).</summary>
    ElevationDenied
}

/// <summary>Contains the result of one launch attempt, including any fault, per `P1-APP-004`.</summary>
/// <param name="Status">Stable launch outcome.</param>
/// <param name="Process">Process record, when one exists or was created.</param>
/// <param name="Context">Scoped execution context, when one was created.</param>
/// <param name="ExitCode">
/// Exit code returned by a <see cref="Kazo.Os.App.Abstractions.AppKind.Terminal"/> app that ran
/// to completion as part of this launch.
/// </param>
/// <param name="StandardOutput">Captured standard output for a completed Terminal launch.</param>
/// <param name="StandardError">Captured standard error for a completed Terminal launch.</param>
/// <param name="ErrorCode">Stable machine-readable error code when the launch did not succeed.</param>
/// <param name="ErrorMessage">Human-readable diagnostic, populated for <see cref="AppLaunchStatus.EntryPointFault"/>.</param>
/// <param name="PermissionDenials">
/// Filesystem permission-class errors raised while a completed Terminal launch ran (`ADR 0042`), so
/// the caller can offer an admin-approval retry without the command itself needing to report them.
/// </param>
public sealed record AppLaunchResult(
    AppLaunchStatus Status,
    ProcessRecord? Process = null,
    IAppExecutionContext? Context = null,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<FileSystemError>? PermissionDenials = null)
{
    /// <summary>Gets whether the app is now running (or already was, for a focused singleton).</summary>
    public bool IsSuccess => Status is AppLaunchStatus.Launched or AppLaunchStatus.FocusedExisting;
}
