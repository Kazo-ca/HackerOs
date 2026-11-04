using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Shell;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Starts the interactive shells terminal emulators host (ADR 0056, section 7): each one is an
/// <c>hsh</c> process, child of the app that asked for it, running lines through the
/// <see cref="ShellInterpreter"/>. Reached by apps only through <see cref="IAppShellHostGateway"/>
/// (capability <c>shell.host</c>).
/// </summary>
public sealed class ShellHostService
{
    /// <summary>App id of a hosted shell's process: <c>ps</c> shows it as <c>hsh</c>.</summary>
    public const string ShellAppId = "org.hackeros.hsh";

    private readonly ShellInterpreter _interpreter;
    private readonly IProcessManager _processes;
    private readonly IElevationService _elevation;

    /// <summary>Creates the service over the interpreter, the process table and the elevation service.</summary>
    /// <param name="interpreter">Runs the lines.</param>
    /// <param name="processes">Creates and ends the shells' processes.</param>
    /// <param name="elevation">Verifies credentials typed for <c>sudo</c> and approvals (ADR 0042).</param>
    public ShellHostService(ShellInterpreter interpreter, IProcessManager processes, IElevationService elevation)
    {
        _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
    }

    /// <summary>
    /// Gets the manifest of the <c>hsh</c> process. It is not a catalog app: it only gives the
    /// shell's process an identity for its own context (file gateway for redirections, logging).
    /// </summary>
    public static AppManifest ShellManifest { get; } = new()
    {
        Id = ShellAppId,
        Name = "hsh",
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = "Kazo OS shell",
        Kind = AppKind.Terminal,
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("system", AppLaunchVisibility.Hidden, []),
        Resources = new AppResourceProfileManifest(0, 0.01, 0.01, 0.01, 0, 0, 0, 0),
        Terminal = new TerminalCommandManifest("hsh", [], "hsh")
    };

    /// <summary>
    /// Starts a shell for <paramref name="principal"/> as a child process of
    /// <paramref name="caller"/>. The capability check is the gateway's.
    /// </summary>
    /// <param name="caller">The terminal app; its process becomes the shell's parent.</param>
    /// <param name="principal">The user the shell and its commands run as.</param>
    /// <param name="createShellContext">
    /// Builds the trusted context of the new shell process (the factory, with the caller's
    /// capabilities: a redirection is opened with the rights the terminal has, as the fork of a
    /// process keeps its credentials).
    /// </param>
    /// <param name="options">Starting directory and credential prompt.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running shell.</returns>
    public async Task<IHackShell> StartAsync(
        IAppExecutionContext caller,
        AuthenticatedPrincipal principal,
        Func<ProcessRecord, IAppExecutionContext> createShellContext,
        HackShellOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(createShellContext);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // fork + exec: the child copies the terminal's directory and environment (ADR 0050).
        ProcessRecord process = _processes.Start(new ProcessStartRequest(
            caller.ProcessId,
            ShellAppId,
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Terminal,
            principal.UserId,
            principal.SessionId,
            ResourceProfile.None,
            ["hsh"]));

        try
        {
            _processes.MarkRunning(process.Pid);
            IAppExecutionContext shellContext = createShellContext(process);
            ShellSession session = new(shellContext, principal);
            if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            {
                await TryChangeDirectoryAsync(shellContext, session, options.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            }

            ShellHostPrompts? prompts = options.CredentialPrompt is { } prompt ? new ShellHostPrompts(prompt, _elevation, () => ShellText.Language(session)) : null;
            return new HostedShell(_interpreter, _processes, shellContext, session, prompts);
        }
        catch
        {
            if (_processes.TryGetActive(process.Pid, out _))
            {
                _processes.Kill(process.Pid);
            }

            throw;
        }
    }

    /// <summary>
    /// Moves the new shell to the requested directory, checked with the shell's own rights. A
    /// missing or unreadable directory is ignored: the shell keeps the terminal's directory.
    /// </summary>
    private static async Task TryChangeDirectoryAsync(
        IAppExecutionContext shellContext, ShellSession session, string path, CancellationToken cancellationToken)
    {
        try
        {
            using (HackExecutionContext.Enter(shellContext))
            {
                await session.ChangeDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException)
        {
            // Like a terminal whose saved directory is gone: start where the parent is.
        }
    }

    /// <summary>One running <c>hsh</c> process.</summary>
    private sealed class HostedShell : IHackShell
    {
        private readonly ShellInterpreter _interpreter;
        private readonly IProcessManager _processes;
        private readonly IAppExecutionContext _context;
        private readonly ShellSession _session;
        private readonly ShellHostPrompts? _prompts;
        private readonly SemaphoreSlim _line = new(1, 1);
        private int _disposed;

        public HostedShell(
            ShellInterpreter interpreter,
            IProcessManager processes,
            IAppExecutionContext context,
            ShellSession session,
            ShellHostPrompts? prompts)
        {
            _interpreter = interpreter;
            _processes = processes;
            _context = context;
            _session = session;
            _prompts = prompts;
        }

        public int ProcessId => checked((int)_context.ProcessId.Value);

        public string WorkingDirectory => _session.CurrentDirectory;

        public int ExitStatus => _session.LastExitCode;

        public string Prompt => _session.GetPrompt();

        public bool HasExited => Volatile.Read(ref _disposed) != 0 || !_processes.TryGetActive(_context.ProcessId, out _);

        public IReadOnlyList<string> History => _session.History;

        public void AddHistory(string commandLine) => _session.AddHistory(commandLine);

        public string? NavigateHistory(int delta) => _session.NavigateHistory(delta);

        public IReadOnlyList<ShellCommandInfo> GetInstalledCommands() =>
            _interpreter.GetInstalledCommands(Kazo.Os.AppSdk.Globalization.HackLocale.ResolveLanguage(_session.GetVariable));

        public bool NeedsMoreInput(string text) => ShellParser.Parse(text ?? string.Empty).IsIncomplete;

        public async Task<int> ExecuteAsync(string commandLine, HackShellExecution execution, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(commandLine);
            ArgumentNullException.ThrowIfNull(execution);
            ObjectDisposedException.ThrowIf(HasExited, this);

            // A shell reads one line at a time from its terminal.
            await _line.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Killing the shell (its tab closed, its terminal gone) stops the line too.
                using CancellationTokenSource line = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _context.CancellationToken);
                using (HackExecutionContext.Enter(_context))
                {
                    ShellExecutionResult result = await _interpreter.ExecuteAsync(
                        _session,
                        commandLine,
                        new ShellExecutionOptions
                        {
                            StandardInput = execution.StandardInput,
                            StandardOutput = execution.StandardOutput,
                            StandardError = execution.StandardError,
                            FullScreen = execution.FullScreen,
                            ApprovalPrompt = _prompts,
                            CredentialPrompt = _prompts is null ? null : _prompts.RequestSudoAsync,
                            PermissionApproval = _prompts is null ? null : _prompts.RequestPermissionApprovalAsync
                        },
                        line.Token).ConfigureAwait(false);
                    if (_session.ExitRequestedStatus is { } exitStatus)
                    {
                        // `exit [n]`: the shell's process ends with n and its terminal closes the tab.
                        await DisposeAsync().ConfigureAwait(false);
                        return exitStatus;
                    }

                    return result.ExitCode;
                }
            }
            finally
            {
                _line.Release();
            }
        }

        public Task<HackShellCompletion> CompleteAsync(string commandLine, int cursor, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(commandLine);
            ArgumentOutOfRangeException.ThrowIfNegative(cursor);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(cursor, commandLine.Length);
            if (HasExited)
            {
                return Task.FromResult(HackShellCompletion.None(cursor));
            }

            return CompleteCoreAsync(commandLine, cursor, cancellationToken);
        }

        /// <summary>Looks paths up with the shell's own rights and directory, as bash's readline does.</summary>
        private async Task<HackShellCompletion> CompleteCoreAsync(string commandLine, int cursor, CancellationToken cancellationToken)
        {
            using (HackExecutionContext.Enter(_context))
            {
                return await ShellCompletion.CompleteAsync(
                    commandLine, cursor, [.. _interpreter.GetInstalledCommands(), .. _interpreter.GetLaunchableApplications()], _session.HomeDirectory, cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            // The terminal hangs up (SIGHUP): commands still running die first, then the shell
            // exits with $?.
            foreach (ProcessRecord child in _processes.GetActiveProcesses().Where(p => p.ParentPid == _context.ProcessId))
            {
                TryEnd(() => _processes.Kill(child.Pid, ProcessExitReason.DependencyStop));
            }

            int exitCode = _session.ExitRequestedStatus ?? _session.LastExitCode;
            TryEnd(() => _processes.Complete(_context.ProcessId, exitCode));
            return ValueTask.CompletedTask;
        }

        /// <summary>Ends a process that may already have ended on its own (or with its host).</summary>
        private static void TryEnd(Action end)
        {
            try
            {
                end();
            }
            catch (KeyNotFoundException)
            {
                // Already gone.
            }
        }
    }
}
