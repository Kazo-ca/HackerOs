using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;

using Kazo.Os.Platform.Core.Execution;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// The headless shell <c>hsh</c>, a POSIX <c>sh</c> subset (ADR 0064, extending ADR 0050): parses
/// a line or a script, expands its words, opens redirections, runs pipeline stages at the same
/// time connected by <see cref="ShellPipe"/>s, chains and-or lists, runs compound commands,
/// functions, scripts and background jobs. Commands are started by
/// <see cref="AppLifecycleOrchestrator"/> as children of the shell's process; a name that is no
/// command but the last segment of an app id (<c>text-editor</c>) starts that app. The interpreter
/// has no UI: the terminal and <c>ExecuteCommandIntent</c> both drive it.
/// </summary>
/// <remarks>
/// Exit codes: <c>127</c> command not found, <c>126</c> found but not executable, <c>2</c>
/// syntax error, <c>130</c> interrupted, <c>1</c> unhandled fault or failed redirection,
/// <c>141</c> broken pipe; otherwise the command's own code. A pipeline's code is its last
/// stage's (no <c>pipefail</c>), inverted by <c>!</c>.
/// </remarks>
public sealed partial class ShellInterpreter
{
    /// <summary>Per-user alias file read for the first word of each command (unchanged location).</summary>
    public const string AliasFileRelativePath = ".kazoos/aliases";

    /// <summary>Exit code of a command that could not be found.</summary>
    public const int CommandNotFoundExitCode = 127;

    /// <summary>Exit code of a command that was found but cannot be executed (a script without <c>+x</c>).</summary>
    public const int NotExecutableExitCode = 126;

    /// <summary>Exit code of a line that did not parse.</summary>
    public const int SyntaxErrorExitCode = 2;

    /// <summary>Exit code of a command ended by <c>SIGKILL</c> (128 + 9).</summary>
    internal const int KilledExitCode = 137;

    /// <summary>Exit code of a command ended by <c>SIGXCPU</c> (128 + 24, ADR 0125).</summary>
    internal const int CpuLimitExitCode = 152;

    /// <summary>Exit code of an interrupted pipeline (128 + SIGINT).</summary>
    public const int InterruptedExitCode = 130;

    /// <summary>Exit code of a command that hit a closed pipe (128 + SIGPIPE).</summary>
    public const int BrokenPipeExitCode = 141;

    /// <summary>Nested scripts, functions and <c>eval</c>s allowed before the shell refuses, as bash's <c>FUNCNEST</c>.</summary>
    private const int MaximumCallDepth = 200;

    private readonly AppLifecycleOrchestrator _orchestrator;
    private readonly IAppEnablementRegistry _enablement;
    private readonly AppCatalog _catalog;
    private readonly IKernelMemoryLedger? _kernelMemory;

    /// <summary>Creates an interpreter over the launch mechanism and the installed commands.</summary>
    /// <param name="orchestrator">Starts every command as a child process of the shell.</param>
    /// <param name="catalog">Commands are the catalog's terminal apps, found by name or static alias.</param>
    /// <param name="enablement">An uninstalled command is "not found", like a removed binary (ADR 0048).</param>
    /// <param name="kernelMemory">
    /// Optional ledger charged with the bytes waiting in each pipe, against the shell that created it
    /// (ADR 0068); <see langword="null"/> or not tracking when memory is not measured.
    /// </param>
    public ShellInterpreter(
        AppLifecycleOrchestrator orchestrator,
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        IKernelMemoryLedger? kernelMemory = null)
    {
        _kernelMemory = kernelMemory;
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Resolver = new ShellCommandResolver(catalog);
    }

    /// <summary>Gets the command lookup (installed or not), shared with <c>help</c> and completion.</summary>
    public ShellCommandResolver Resolver { get; }

    /// <summary>
    /// Gets the installed commands, ordered by name: the catalog's terminal apps, the shell itself
    /// (<c>sh</c>) and its special built-ins. What <c>help</c> lists through
    /// <see cref="IShellSession.GetCommands()"/> and what the terminal completes (ADR 0051, section 2).
    /// </summary>
    /// <param name="culture">
    /// Language of the descriptions and usages (ADR 0104 section 4): the catalog's translations of
    /// each command's manifest and the built-ins' own texts; English when <see langword="null"/>.
    /// </param>
    public IReadOnlyList<ShellCommandInfo> GetInstalledCommands(System.Globalization.CultureInfo? culture = null)
    {
        culture ??= Kazo.Os.AppSdk.Globalization.KazoLocale.DefaultLanguage;
        return [.. Resolver.GetTerminalCommands()
            .Where(manifest => _enablement.IsEnabled(manifest.Id))
            .Select(manifest => _catalog.Localize(manifest, culture))
            .Select(manifest => new ShellCommandInfo(
                manifest.Terminal!.Name, manifest.Terminal.Aliases, manifest.Terminal.Usage, manifest.Description))
            .Concat(LocalizeBuiltins(culture))
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Gets the window apps the shell starts by short name (ADR 0064), as completion entries:
    /// <c>text-editor</c>, <c>browser</c>…, unless a command already has that name.
    /// </summary>
    /// <param name="culture">Language of the descriptions; English when <see langword="null"/>.</param>
    public IReadOnlyList<ShellCommandInfo> GetLaunchableApplications(System.Globalization.CultureInfo? culture = null)
    {
        culture ??= Kazo.Os.AppSdk.Globalization.KazoLocale.DefaultLanguage;
        HashSet<string> commands = new(GetInstalledCommands(culture).SelectMany(command => command.Aliases.Prepend(command.Name)), StringComparer.OrdinalIgnoreCase);
        return [.. Resolver.GetApplications()
            .Where(manifest => _enablement.IsEnabled(manifest.Id))
            .Select(manifest => (Manifest: _catalog.Localize(manifest, culture), Name: ShellCommandResolver.ShortName(manifest.Id)))
            .Where(entry => !commands.Contains(entry.Name))
            .Select(entry => new ShellCommandInfo(
                entry.Name, [], $"{entry.Name} [args...]", Strings.For(culture)["Shell.StartApp", entry.Manifest.Name]))];
    }

    /// <summary>Runs one line (or several lines) typed in <paramref name="session"/>.</summary>
    /// <param name="session">The shell: process, variables, <c>$?</c>, jobs.</param>
    /// <param name="commandLine">The input as typed.</param>
    /// <param name="options">Output streams and prompts.</param>
    /// <param name="cancellationToken">Ctrl+C: cancels every stage of the running pipeline and stops the line.</param>
    /// <returns>The exit code of the last pipeline that ran, and why it failed if it did not run.</returns>
    public async Task<ShellExecutionResult> ExecuteAsync(
        ShellSession session,
        string commandLine,
        ShellExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(options);

        if (session.IsInteractive)
        {
            await ReportFinishedJobsAsync(session, options.StandardError).ConfigureAwait(false);
        }

        ShellParseResult parsed = ShellParser.Parse(commandLine);
        if (parsed.Error is { } syntaxError)
        {
            await options.StandardError.WriteLineAsync($"hsh: {syntaxError.Localize(ShellText.For(session))}").ConfigureAwait(false);
            session.LastExitCode = SyntaxErrorExitCode;
            return new ShellExecutionResult(SyntaxErrorExitCode, ShellFailure.Syntax, syntaxError, []);
        }

        if (parsed.Commands is null)
        {
            // A blank line runs nothing and, as in bash, leaves $? alone.
            return new ShellExecutionResult(session.LastExitCode, ShellFailure.None, null, []);
        }

        LineState line = new(options.Elevation);
        Exec exec = new()
        {
            Session = session,
            Options = options,
            Line = line,
            Source = commandLine,
            In = options.StandardInput ?? TextReader.Null,
            Out = options.StandardOutput,
            Err = options.StandardError,
            Token = cancellationToken,
            Control = new ControlState()
        };

        int exitCode = await RunListAsync(exec, parsed.Commands).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            exitCode = InterruptedExitCode;
        }

        if (session.ExitRequestedStatus is { } exitStatus)
        {
            exitCode = exitStatus;
        }

        // A `break` or `return` typed at the prompt has nothing left to leave.
        exec.Control.Reset();
        session.LastExitCode = exitCode;
        if (session.IsInteractive)
        {
            await ReportFinishedJobsAsync(session, options.StandardError).ConfigureAwait(false);
        }

        return new ShellExecutionResult(exitCode, line.LastFailure, null, line.PermissionDenials);
    }

    /// <summary>
    /// Runs a command line in a subshell copy of <paramref name="session"/> with the given streams
    /// (<see cref="IShellSession.RunAsync"/>: <c>watch</c>). Nothing it changes reaches the shell.
    /// </summary>
    internal async Task<int> RunInSubshellAsync(
        ShellSession session,
        string commandLine,
        ShellExecutionOptions options,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ShellParseResult parsed = ShellParser.Parse(commandLine);
        if (parsed.Error is { } syntaxError)
        {
            await error.WriteLineAsync($"hsh: {syntaxError.Localize(ShellText.For(session))}").ConfigureAwait(false);
            return SyntaxErrorExitCode;
        }

        if (parsed.Commands is null)
        {
            return 0;
        }

        ShellSession copy = session.Fork();
        Exec exec = new()
        {
            Session = copy,
            Options = options,
            Line = new LineState(null),
            Source = commandLine,
            In = input,
            Out = output,
            Err = error,
            Token = cancellationToken,
            Control = new ControlState()
        };
        int status = await RunListAsync(exec, parsed.Commands).ConfigureAwait(false);
        return cancellationToken.IsCancellationRequested ? InterruptedExitCode : copy.ExitRequestedStatus ?? status;
    }

    // ---- Lists, and-or lists, pipelines --------------------------------------------------

    /// <summary>Runs a list: each and-or list in turn, a background one as a job.</summary>
    private async Task<int> RunListAsync(Exec exec, ShellCommandList list)
    {
        int status = 0;
        foreach (ShellListItem item in list.Items)
        {
            if (ShouldStop(exec))
            {
                break;
            }

            if (item.Background)
            {
                StartBackgroundJob(exec, item);
                status = 0;
                exec.Session.LastExitCode = 0;
                continue;
            }

            status = await RunAndOrAsync(exec, item.AndOr).ConfigureAwait(false);
        }

        return status;
    }

    /// <summary><c>a &amp;&amp; b || c</c>: left to right, each pipeline run or skipped by the previous status.</summary>
    private async Task<int> RunAndOrAsync(Exec exec, ShellAndOrList andOr)
    {
        int status = exec.Session.LastExitCode;
        for (int index = 0; index < andOr.Pipelines.Count; index++)
        {
            if (index > 0)
            {
                ShellAndOrOperator op = andOr.Operators[index - 1];
                if ((op == ShellAndOrOperator.And && status != 0) || (op == ShellAndOrOperator.Or && status == 0))
                {
                    continue;
                }
            }

            if (ShouldStop(exec))
            {
                break;
            }

            // Every pipeline but the last is tested by && or ||: set -e ignores its failure.
            bool tested = index < andOr.Pipelines.Count - 1;
            status = await RunPipelineAsync(exec, andOr.Pipelines[index], tested).ConfigureAwait(false);
            // $? follows every pipeline, so `false && …` then `echo $?` and `a && echo $?` behave as in bash.
            exec.Session.LastExitCode = status;
        }

        return status;
    }

    /// <summary>Runs every stage of a pipeline at once, connected by pipes.</summary>
    private async Task<int> RunPipelineAsync(Exec exec, ShellPipeline pipeline, bool tested)
    {
        int status;
        if (pipeline.Commands.Count == 1)
        {
            status = await RunCommandSafelyAsync(exec with { SingleStage = true }, pipeline.Commands[0]).ConfigureAwait(false);
        }
        else
        {
            using CancellationTokenSource pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(exec.Token);
            ShellPipe[] pipes = [.. Enumerable.Range(0, pipeline.Commands.Count - 1).Select(_ => new ShellPipe(charge: ChargePipe(exec.Session.ProcessId)))];
            Task<int>[] running = new Task<int>[pipeline.Commands.Count];
            // ADR 0070, section 2: every stage of one pipeline goes to the same worker. Each stage
            // captures the group when it starts, so the scope can close before the stages end.
            using (ExecutionPlacement.BeginGroup())
            {
                for (int index = 0; index < pipeline.Commands.Count; index++)
                {
                    ShellPipe? input = index == 0 ? null : pipes[index - 1];
                    ShellPipe? output = index == pipeline.Commands.Count - 1 ? null : pipes[index];
                    // Each stage runs in a subshell copy (ADR 0051 section 3): a `cd` or a variable
                    // set inside a pipe is lost when the stage ends.
                    Exec stage = exec with
                    {
                        Session = exec.Session.Fork(),
                        In = input?.Reader ?? exec.In,
                        Out = output?.Writer ?? exec.Out,
                        Token = pipelineCancellation.Token,
                        Control = new ControlState(),
                        SingleStage = false,
                        LoopDepth = 0,
                        CanReturn = false
                    };
                    running[index] = RunStageAsync(stage, pipeline.Commands[index], input, output);
                }
            }

            int[] statuses = await Task.WhenAll(running).ConfigureAwait(false);
            status = statuses[^1];
        }

        if (exec.Token.IsCancellationRequested)
        {
            status = InterruptedExitCode;
        }

        if (pipeline.Negated)
        {
            status = status == 0 ? 1 : 0;
        }
        else if (status != 0 && !tested && !exec.InCondition && exec.Session.Options.Contains('e') && exec.Session.ExitRequestedStatus is null)
        {
            // set -e: a failing command that nothing tests ends the shell.
            exec.Session.RequestExit(status);
        }

        return status;
    }

    /// <summary>Runs one stage of a multi-stage pipeline, then closes its pipe ends like a process exiting.</summary>
    private async Task<int> RunStageAsync(Exec stage, ShellCommand command, ShellPipe? input, ShellPipe? output)
    {
        try
        {
            int status = await RunCommandSafelyAsync(stage, command).ConfigureAwait(false);
            return stage.Session.ExitRequestedStatus ?? status;
        }
        finally
        {
            // The reader then sees end of stream, and the writer feeding it gets "Broken pipe".
            output?.CloseWriter();
            input?.CloseReader();
        }
    }

    /// <summary>Runs a command, turning an interruption, a broken pipe or an expansion error into an exit status.</summary>
    private async Task<int> RunCommandSafelyAsync(Exec exec, ShellCommand command)
    {
        try
        {
            return await RunCommandAsync(exec, command).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (exec.Token.IsCancellationRequested)
        {
            return InterruptedExitCode;
        }
        catch (ShellBrokenPipeException)
        {
            return BrokenPipeExitCode;
        }
        catch (ShellExpansionException exception)
        {
            await exec.Err.WriteLineAsync($"{ErrorPrefix(exec)}{exception.Localize(exec.Text)}").ConfigureAwait(false);
            if (exec.Session.IsScript)
            {
                // A non-interactive shell exits on an expansion error (POSIX 2.8.1).
                exec.Session.RequestExit(1);
            }

            return 1;
        }
    }

    /// <summary>Runs one command of any kind.</summary>
    private async Task<int> RunCommandAsync(Exec exec, ShellCommand command)
    {
        if (command is ShellSimpleCommand simple)
        {
            return await RunSimpleCommandAsync(exec, simple).ConfigureAwait(false);
        }

        if (command is ShellFunctionDefinition definition)
        {
            exec.Session.Functions[definition.Name] = definition;
            return 0;
        }

        RedirectionScope? scope = null;
        if (command.Redirections.Count > 0)
        {
            scope = await OpenRedirectionsAsync(exec, command.Redirections).ConfigureAwait(false);
            if (scope is null)
            {
                exec.Line.LastFailure = ShellFailure.Redirection;
                return 1;
            }

            exec = scope.Exec;
        }

        int status;
        try
        {
            status = command switch
            {
                ShellGroupCommand { Subshell: true } group => await RunSubshellAsync(exec, group.Body).ConfigureAwait(false),
                ShellGroupCommand group => await RunListAsync(exec, group.Body).ConfigureAwait(false),
                ShellIfCommand @if => await RunIfAsync(exec, @if).ConfigureAwait(false),
                ShellLoopCommand loop => await RunLoopAsync(exec, loop).ConfigureAwait(false),
                ShellForCommand @for => await RunForAsync(exec, @for).ConfigureAwait(false),
                ShellCaseCommand @case => await RunCaseAsync(exec, @case).ConfigureAwait(false),
                _ => 0
            };
        }
        finally
        {
            if (scope is not null && !await scope.CloseAsync().ConfigureAwait(false))
            {
                status = 1;
            }
        }

        return status;
    }

    /// <summary><c>( list )</c>: a copy of the shell whose changes (directory, variables, <c>exit</c>) stay inside.</summary>
    private async Task<int> RunSubshellAsync(Exec exec, ShellCommandList body)
    {
        ShellSession copy = exec.Session.Fork();
        int status = await RunListAsync(exec with { Session = copy, Control = new ControlState(), LoopDepth = 0, CanReturn = false }, body).ConfigureAwait(false);
        return copy.ExitRequestedStatus ?? status;
    }

    private async Task<int> RunIfAsync(Exec exec, ShellIfCommand command)
    {
        foreach (ShellIfBranch branch in command.Branches)
        {
            int condition = await RunListAsync(exec with { InCondition = true }, branch.Condition).ConfigureAwait(false);
            if (ShouldStop(exec))
            {
                return condition;
            }

            if (condition == 0)
            {
                return await RunListAsync(exec, branch.Body).ConfigureAwait(false);
            }
        }

        return command.Else is null ? 0 : await RunListAsync(exec, command.Else).ConfigureAwait(false);
    }

    private async Task<int> RunLoopAsync(Exec exec, ShellLoopCommand loop)
    {
        Exec body = exec with { LoopDepth = exec.LoopDepth + 1 };
        int status = 0;
        for (long iteration = 0; ; iteration++)
        {
            await YieldIfBusyAsync(iteration, exec.Token).ConfigureAwait(false);
            int condition = await RunListAsync(body with { InCondition = true }, loop.Condition).ConfigureAwait(false);
            if (LeaveLoop(body, out bool stop) || stop)
            {
                break;
            }

            if ((condition == 0) == loop.Until)
            {
                break;
            }

            status = await RunListAsync(body, loop.Body).ConfigureAwait(false);
            if (LeaveLoop(body, out stop) || stop)
            {
                break;
            }
        }

        return status;
    }

    private async Task<int> RunForAsync(Exec exec, ShellForCommand loop)
    {
        IReadOnlyList<string> items = loop.Items is null
            ? exec.Session.PositionalParameters
            : await CreateExpander(exec).ExpandFieldsAsync(loop.Items).ConfigureAwait(false);
        Exec body = exec with { LoopDepth = exec.LoopDepth + 1 };
        int status = 0;
        for (int index = 0; index < items.Count; index++)
        {
            await YieldIfBusyAsync(index, exec.Token).ConfigureAwait(false);
            exec.Session.AssignVariable(loop.Variable, items[index]);
            status = await RunListAsync(body, loop.Body).ConfigureAwait(false);
            if (LeaveLoop(body, out bool stop) || stop)
            {
                break;
            }
        }

        return status;
    }

    private async Task<int> RunCaseAsync(Exec exec, ShellCaseCommand command)
    {
        ShellExpander expander = CreateExpander(exec);
        string subject = await expander.ExpandStringAsync(command.Subject).ConfigureAwait(false);
        foreach (ShellCaseItem item in command.Items)
        {
            foreach (ShellWord pattern in item.Patterns)
            {
                if (ShellPattern.IsMatch(subject, await expander.ExpandPatternAsync(pattern).ConfigureAwait(false)))
                {
                    return item.Body is null ? 0 : await RunListAsync(exec, item.Body).ConfigureAwait(false);
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Applies a pending <c>break</c> or <c>continue</c> at the end of a loop iteration. Returns
    /// <see langword="true"/> to leave the loop; <paramref name="stop"/> reports a <c>return</c>,
    /// an <c>exit</c> or an interruption.
    /// </summary>
    private static bool LeaveLoop(Exec exec, out bool stop)
    {
        ControlState control = exec.Control;
        stop = false;
        switch (control.Kind)
        {
            case ControlKind.Break:
                if (control.Count > 1)
                {
                    control.Count--;
                }
                else
                {
                    control.Reset();
                }

                return true;
            case ControlKind.Continue:
                if (control.Count > 1)
                {
                    control.Count--;
                    return true;
                }

                control.Reset();
                return false;
            default:
                stop = ShouldStop(exec);
                return false;
        }
    }

    /// <summary>
    /// Lets the browser breathe during a long loop: <c>while true; do :; done</c> made only of
    /// built-ins would otherwise never return to the event loop, and Ctrl+C could never arrive.
    /// </summary>
    private static async Task YieldIfBusyAsync(long iteration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (iteration > 0 && iteration % 64 == 0)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the current list must stop: <c>break</c>, <c>continue</c>, <c>return</c>, <c>exit</c> or Ctrl+C.</summary>
    private static bool ShouldStop(Exec exec) =>
        exec.Control.Kind != ControlKind.None
        || exec.Session.ExitRequestedStatus is not null
        || exec.Token.IsCancellationRequested;

    /// <summary>Prefix of the shell's own error messages: <c>hsh: </c>, or <c>script: </c> inside a script, as bash prints its <c>$0</c>.</summary>
    private static string ErrorPrefix(Exec exec) => exec.Session.IsScript ? $"{exec.Session.ScriptName}: " : "hsh: ";

    /// <summary>Creates the expander of one command, whose command substitutions run in subshell copies.</summary>
    private ShellExpander CreateExpander(Exec exec) =>
        new(exec.Session, body => RunCommandSubstitutionAsync(exec, body), exec.Token);

    /// <summary><c>$(list)</c>: runs in a subshell copy, its output captured; <c>$?</c> becomes its status.</summary>
    private async Task<string> RunCommandSubstitutionAsync(Exec exec, ShellCommandList body)
    {
        if (exec.Session.CallDepth >= MaximumCallDepth)
        {
            throw new ShellExpansionException(new ShellLocalizedText("Shell.MaximumNestingLevel"));
        }

        StringWriter captured = new() { NewLine = "\n" };
        ShellSession copy = exec.Session.Fork();
        copy.CallDepth++;
        int status = await RunListAsync(
            exec with
            {
                Session = copy,
                Out = captured,
                Control = new ControlState(),
                LoopDepth = 0,
                CanReturn = false,
                InCondition = false,
                SingleStage = false
            },
            body).ConfigureAwait(false);
        exec.Session.LastExitCode = copy.ExitRequestedStatus ?? status;
        return captured.ToString();
    }

    // ---- Background jobs -----------------------------------------------------------------

    /// <summary>
    /// <c>list &amp;</c>: runs the and-or list in a subshell copy without waiting, with an empty
    /// input and its own cancellation (Ctrl+C at the prompt does not reach it), and prints
    /// <c>[n] pid</c> in an interactive shell.
    /// </summary>
    private void StartBackgroundJob(Exec exec, ShellListItem item)
    {
        ShellSession session = exec.Session;
        string command = SourceText(exec.Source, item.Offset, item.Length);
        CancellationTokenSource cancellation = session.Host is { } host
            ? CancellationTokenSource.CreateLinkedTokenSource(host.CancellationToken)
            : new CancellationTokenSource();
        ShellJob job = session.Jobs.Add(command, cancellation);
        Exec jobExec = exec with
        {
            Session = session.Fork(),
            In = TextReader.Null,
            Token = cancellation.Token,
            Control = new ControlState(),
            Line = new LineState(null),
            InCondition = false,
            SingleStage = false,
            LoopDepth = 0,
            CanReturn = false,
            ProcessStarted = process =>
            {
                job.AddProcess(process.Pid);
                session.LastBackgroundProcessId = process.Pid.Value;
                exec.ProcessStarted?.Invoke(process);
            }
        };

        job.Completion = RunJobAsync(jobExec, item.AndOr, job);
        if (session.IsInteractive)
        {
            string pid = job.Processes.Count > 0 ? $" {job.Processes[^1].Value}" : string.Empty;
            TryWriteLine(exec.Err, $"[{job.Id}]{pid}");
        }
    }

    private async Task<int> RunJobAsync(Exec exec, ShellAndOrList andOr, ShellJob job)
    {
        // Let the caller print `[n] pid` and read its next line before the job runs on.
        await Task.Yield();
        int status;
        try
        {
            status = await RunAndOrAsync(exec, andOr).ConfigureAwait(false);
            status = exec.Session.ExitRequestedStatus ?? status;
        }
        catch (OperationCanceledException)
        {
            status = 143;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Its terminal went away (tab closed): the job ends with the hang-up.
            status = 129;
        }

        bool terminated = job.Cancellation.IsCancellationRequested;
        job.Finish(terminated ? 143 : status, terminated);
        job.Cancellation.Dispose();
        return status;
    }

    /// <summary>Prints <c>[1]+  Done   cmd</c> for each job that ended since the last report, then forgets it.</summary>
    private static async Task ReportFinishedJobsAsync(ShellSession session, TextWriter error)
    {
        IReadOnlyList<ShellJob> jobs = session.Jobs.Jobs;
        ShellJob? current = session.Jobs.Current;
        foreach (ShellJob job in jobs)
        {
            if (job.State == ShellJobState.Running || job.Reported)
            {
                continue;
            }

            job.Reported = true;
            try
            {
                await error.WriteLineAsync(FormatJob(job, ReferenceEquals(job, current), ShellText.For(session))).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Nothing to report to.
            }
        }

        session.Jobs.RemoveReported();
    }

    /// <summary>Formats a job like bash's <c>jobs</c> and completion notices.</summary>
    /// <param name="job">The job.</param>
    /// <param name="current">Whether it is the current job (<c>+</c>).</param>
    /// <param name="texts">The shell's texts (bash translates the job states); English when <see langword="null"/>.</param>
    internal static string FormatJob(ShellJob job, bool current, Microsoft.Extensions.Localization.IStringLocalizer? texts = null)
    {
        texts ??= ShellText.English;
        string state = job.State switch
        {
            ShellJobState.Running => texts["Job.Running"],
            ShellJobState.Terminated => texts["Job.Terminated"],
            _ when job.ExitStatus is null or 0 => texts["Job.Done"],
            _ => texts["Job.Exit", job.ExitStatus]
        };
        string suffix = job.State == ShellJobState.Running ? " &" : string.Empty;
        return $"[{job.Id}]{(current ? '+' : '-')}  {state,-24}{job.Command}{suffix}";
    }

    /// <summary>Stops a job: its token is cancelled and its live processes are stopped (<c>kill %n</c>).</summary>
    internal bool KillJob(ShellSession session, int jobId, bool force)
    {
        ShellJob? job = session.Jobs.Find(jobId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (job is null)
        {
            return false;
        }

        try
        {
            job.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already over.
        }

        foreach (ProcessId pid in job.Processes)
        {
            _ = force ? _orchestrator.InterruptAsync(pid) : StopQuietlyAsync(pid);
        }

        return true;
    }

    private async Task StopQuietlyAsync(ProcessId pid)
    {
        try
        {
            await _orchestrator.InterruptAsync(pid).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            // Ended on its own.
        }
    }

    private static string SourceText(string source, int offset, int length) =>
        offset >= 0 && offset + length <= source.Length ? source.Substring(offset, length).Trim() : string.Empty;

    private static void TryWriteLine(TextWriter writer, string text)
    {
        try
        {
            writer.WriteLine(text);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A closed output loses the notice, nothing more.
        }
    }

    /// <summary>Opens the kernel-memory charge of a new pipe against the shell's process, when memory is measured.</summary>
    private KernelMemoryCharge? ChargePipe(ProcessId? shell) =>
        _kernelMemory is { IsTracking: true } ledger && shell is { } pid
            ? ledger.Charge(pid, KernelMemoryKind.Pipe)
            : null;

    // ---- Execution state -----------------------------------------------------------------

    /// <summary>Everything one command runs with: its shell, streams, cancellation and control-flow state.</summary>
    private sealed record Exec
    {
        public required ShellSession Session { get; init; }

        public required ShellExecutionOptions Options { get; init; }

        public required LineState Line { get; init; }

        /// <summary>The text the commands were parsed from (job names).</summary>
        public required string Source { get; init; }

        public required TextReader In { get; init; }

        public required TextWriter Out { get; init; }

        public required TextWriter Err { get; init; }

        public required CancellationToken Token { get; init; }

        public required ControlState Control { get; init; }

        /// <summary>Gets the shell's texts in its current language (<c>LANG</c> can change between commands).</summary>
        public Microsoft.Extensions.Localization.IStringLocalizer Text => ShellText.For(Session);

        /// <summary>Inside an <c>if</c>/<c>while</c> condition: <c>set -e</c> does not apply.</summary>
        public bool InCondition { get; init; }

        /// <summary>The command is a pipeline of its own (one-shot approval retry, ADR 0042).</summary>
        public bool SingleStage { get; init; }

        /// <summary>How many loops enclose the command (<c>break</c> and <c>continue</c> need one).</summary>
        public int LoopDepth { get; init; }

        /// <summary>Inside a function or a sourced script: <c>return</c> is allowed.</summary>
        public bool CanReturn { get; init; }

        /// <summary>Called with each process the command starts (background jobs).</summary>
        public Action<ProcessRecord>? ProcessStarted { get; init; }
    }

    private enum ControlKind
    {
        None,
        Break,
        Continue,
        Return
    }

    /// <summary>A pending <c>break n</c>, <c>continue n</c> or <c>return</c>, shared by the commands of one shell.</summary>
    private sealed class ControlState
    {
        public ControlKind Kind { get; set; }

        public int Count { get; set; }

        public void Reset()
        {
            Kind = ControlKind.None;
            Count = 0;
        }
    }

    /// <summary>State shared by the commands of one line.</summary>
    private sealed class LineState(ShellElevation? elevation)
    {
        private readonly object _sync = new();
        private readonly List<FileSystemError> _denials = [];
        private ShellElevation? _elevation = elevation;

        public ShellFailure LastFailure { get; set; }

        public IReadOnlyList<FileSystemError> PermissionDenials
        {
            get
            {
                lock (_sync)
                {
                    return [.. _denials];
                }
            }
        }

        /// <summary>Hands the line's ticket to the first command launched; single use.</summary>
        public ShellElevation? TakeElevation()
        {
            lock (_sync)
            {
                ShellElevation? taken = _elevation;
                _elevation = null;
                return taken;
            }
        }

        public void AddDenials(IReadOnlyList<FileSystemError> denials)
        {
            lock (_sync)
            {
                _denials.AddRange(denials);
            }
        }
    }
}
