using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>Simple commands: expansion, assignments, lookup (built-ins, functions, commands, scripts, apps) and launch.</summary>
public sealed partial class ShellInterpreter
{
    /// <summary>
    /// Runs one simple command: alias substitution, expansion, assignments, redirections, then the
    /// command found for its first field (POSIX 2.9.1).
    /// </summary>
    private async Task<int> RunSimpleCommandAsync(Exec exec, ShellSimpleCommand command)
    {
        ShellSession session = exec.Session;
        IReadOnlyList<ShellWord> words = command.Words;
        IReadOnlyList<ShellRedirection> redirections = command.Redirections;
        IReadOnlyList<ShellAssignment> assignments = command.Assignments;

        // Aliases apply to typed lines, not to scripts (bash's expand_aliases is off there).
        if (!session.IsScript && words.Count > 0 && words[0].PlainText is { } aliasName && aliasName != "alias"
            && !session.Functions.ContainsKey(aliasName))
        {
            string? value = await TryReadAliasAsync(session, aliasName, exec.Token).ConfigureAwait(false);
            if (value is not null)
            {
                ShellParseResult alias = ShellParser.Parse(value);
                if (alias.Commands is { Items: [{ Background: false, AndOr.Pipelines: [{ Negated: false, Commands: [ShellSimpleCommand aliased] }] }] }
                    && aliased.Words.Count > 0)
                {
                    words = [.. aliased.Words, .. words.Skip(1)];
                    redirections = [.. aliased.Redirections, .. redirections];
                    assignments = [.. assignments, .. aliased.Assignments];
                }
                else
                {
                    await exec.Err.WriteLineAsync("hsh: " + exec.Text["Shell.AliasNotSimple", aliasName]).ConfigureAwait(false);
                    exec.Line.LastFailure = ShellFailure.Fault;
                    return 1;
                }
            }
        }

        ShellExpander expander = CreateExpander(exec);
        List<string> fields = await expander.ExpandFieldsAsync(words).ConfigureAwait(false);
        List<(string Name, string Value)> values = [];
        foreach (ShellAssignment assignment in assignments)
        {
            values.Add((assignment.Name, await expander.ExpandStringAsync(assignment.Value).ConfigureAwait(false)));
        }

        if (fields.Count == 0)
        {
            // Assignments alone set shell variables; redirections alone open and close their files.
            foreach ((string name, string value) in values)
            {
                session.AssignVariable(name, value);
            }

            if (session.Options.Contains('x') && values.Count > 0)
            {
                await exec.Err.WriteLineAsync("+ " + string.Join(' ', values.Select(v => $"{v.Name}={QuoteForTrace(v.Value)}"))).ConfigureAwait(false);
            }

            if (redirections.Count > 0)
            {
                RedirectionScope? only = await OpenRedirectionsAsync(exec, redirections).ConfigureAwait(false);
                if (only is null || !await only.CloseAsync().ConfigureAwait(false))
                {
                    exec.Line.LastFailure = ShellFailure.Redirection;
                    return 1;
                }
            }

            exec.Line.LastFailure = ShellFailure.None;
            return expander.LastSubstitutionStatus ?? 0;
        }

        if (session.Options.Contains('x'))
        {
            // set -x: the command as it will run, after expansion.
            IEnumerable<string> traced = values.Select(v => $"{v.Name}={QuoteForTrace(v.Value)}").Concat(fields.Select(QuoteForTrace));
            await exec.Err.WriteLineAsync("+ " + string.Join(' ', traced)).ConfigureAwait(false);
        }

        RedirectionScope? scope = null;
        if (redirections.Count > 0)
        {
            scope = await OpenRedirectionsAsync(exec, redirections).ConfigureAwait(false);
            if (scope is null)
            {
                exec.Line.LastFailure = ShellFailure.Redirection;
                return 1;
            }
        }

        int status;
        try
        {
            status = await DispatchAsync(scope?.Exec ?? exec, fields, values).ConfigureAwait(false);
        }
        finally
        {
            if (scope is not null && !await scope.CloseAsync().ConfigureAwait(false))
            {
                // The file could not be published (e.g. `>>` to a file the user cannot write).
                exec.Line.LastFailure = ShellFailure.Redirection;
                status = 1;
            }
        }

        return status;
    }

    /// <summary>
    /// Finds what the first field names, in the order of a POSIX shell: special built-in,
    /// function, then a path (script) or a command of the catalog, the shell itself
    /// (<c>sh</c>), and finally an app whose id ends with the name (ADR 0064).
    /// </summary>
    private async Task<int> DispatchAsync(Exec exec, IReadOnlyList<string> fields, IReadOnlyList<(string Name, string Value)> assignments)
    {
        ShellSession session = exec.Session;
        string name = fields[0];
        IReadOnlyList<string> arguments = [.. fields.Skip(1)];

        if (IsSpecialBuiltin(name))
        {
            // Assignments before a special built-in persist (POSIX 2.14).
            foreach ((string variable, string value) in assignments)
            {
                session.AssignVariable(variable, value);
            }

            exec.Line.LastFailure = ShellFailure.None;
            return await RunBuiltinAsync(exec, name, arguments).ConfigureAwait(false);
        }

        if (session.Functions.TryGetValue(name, out ShellFunctionDefinition? function))
        {
            foreach ((string variable, string value) in assignments)
            {
                session.AssignVariable(variable, value);
            }

            exec.Line.LastFailure = ShellFailure.None;
            return await CallFunctionAsync(exec, function, arguments).ConfigureAwait(false);
        }

        if (name.Contains('/'))
        {
            return await RunPathAsync(exec, name, arguments, assignments).ConfigureAwait(false);
        }

        ShellCommandResolution? resolution = Resolver.Resolve(name, arguments);
        if (resolution is null && name == "[")
        {
            // `[` is `test` called under another name (as /usr/bin/[ is on Linux); manifest command
            // names cannot hold a bracket, so the shell links it, and argv[0] = "[" asks for `]`.
            resolution = Resolver.Resolve("test", arguments) is { } test ? test with { CommandName = "[" } : null;
        }

        if (resolution is not null && _enablement.IsEnabled(resolution.AppId))
        {
            return await LaunchCommandAsync(exec, resolution, assignments).ConfigureAwait(false);
        }

        if (ShellNames.Contains(name))
        {
            exec.Line.LastFailure = ShellFailure.None;
            return await RunShAsync(exec, name, arguments, assignments).ConfigureAwait(false);
        }

        if (Resolver.ResolveApplication(name) is { } application && _enablement.IsEnabled(application.Id))
        {
            return await LaunchApplicationAsync(exec, application, name, arguments, assignments).ConfigureAwait(false);
        }

        await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.CommandNotFound", name]).ConfigureAwait(false);
        exec.Line.LastFailure = resolution is null ? ShellFailure.CommandNotFound : ShellFailure.Disabled;
        return CommandNotFoundExitCode;
    }

    /// <summary>Starts a catalog command as a child of the shell's process, with its streams.</summary>
    private async Task<int> LaunchCommandAsync(Exec exec, ShellCommandResolution resolution, IReadOnlyList<(string Name, string Value)> assignments)
    {
        ShellSession session = exec.Session;
        ShellExecutionOptions options = exec.Options;

        // ADR 0051 section 3: a command declaring shell.control acts on the shell running it (a
        // terminal or a script); inside a pipe that shell is already a stage's copy. The
        // orchestrator still hands it over only if the capability is granted.
        IShellSession? shell = session.ProvidesShellControl && DeclaresShellControl(resolution.Manifest)
            ? new ShellSessionAccess(this, session, options, exec.In)
            : null;

        ShellElevation? elevation = exec.Line.TakeElevation();
        IReadOnlyDictionary<string, string>? environment = ChildEnvironment(session, assignments);
        StageOutcome outcome = await StartAsync(
            "hsh", session, resolution, session.ProcessId, exec.In, exec.Out, exec.Err, options, elevation, shell, environment,
            exec.ProcessStarted, exec.Token).ConfigureAwait(false);
        exec.Line.AddDenials(outcome.Denials);

        // ADR 0042: a lone command that failed on a permission denial may be retried once with an
        // administrator's one-shot approval. Its input was empty, so rerunning it is safe; a
        // command that read the terminal's input is not rerun, the input is gone.
        if (exec.SingleStage
            && !session.IsScript
            && options.StandardInput is null
            && outcome is { Failure: ShellFailure.None, ExitCode: not 0, Elevated: false, Denials.Count: > 0 }
            && outcome.Denials[0].RequiredCapability is not null
            && options.PermissionApproval is { } approve
            && !exec.Token.IsCancellationRequested)
        {
            ShellElevation? ticket = await approve(
                new ShellPermissionDenial(resolution.CommandName, outcome.Denials[0]), exec.Token).ConfigureAwait(false);
            if (ticket is not null)
            {
                outcome = await StartAsync(
                    "hsh", session, resolution, session.ProcessId, exec.In, exec.Out, exec.Err, options, ticket, shell, environment,
                    exec.ProcessStarted, exec.Token).ConfigureAwait(false);
                exec.Line.AddDenials(outcome.Denials);
            }
        }

        exec.Line.LastFailure = outcome.Failure;
        return outcome.ExitCode;
    }

    /// <summary>
    /// What a child inherits beyond its parent's process environment: a subshell copy (or a
    /// detached shell) passes its whole exported environment, since it lives only in the copy;
    /// prefix assignments (<c>LANG=C sort</c>) apply on top, for this command only.
    /// </summary>
    private static Dictionary<string, string>? ChildEnvironment(ShellSession session, IReadOnlyList<(string Name, string Value)> assignments)
    {
        Dictionary<string, string>? environment = session.IsSubshell || session.ProcessId is null
            ? new Dictionary<string, string>(session.GetVariables(), StringComparer.Ordinal)
            : null;
        foreach ((string name, string value) in assignments)
        {
            environment ??= new Dictionary<string, string>(StringComparer.Ordinal);
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    /// Starts a window app named by the last segment of its id (<c>text-editor notes.txt</c>,
    /// ADR 0064) as a child of the shell, with the arguments and the shell's streams: what it
    /// writes with <see cref="KazoConsole"/> appears in the terminal. Like a GUI program started
    /// from bash, the shell waits until the window closes (use <c>&amp;</c> to keep the prompt);
    /// Ctrl+C closes it. An already running single-instance app is only focused.
    /// </summary>
    private async Task<int> LaunchApplicationAsync(
        Exec exec, AppManifest application, string name, IReadOnlyList<string> arguments, IReadOnlyList<(string Name, string Value)> assignments)
    {
        ShellSession session = exec.Session;
        AppLaunchResult launch = await _orchestrator.LaunchAsync(
            new AppLaunchRequest(
                application.Id,
                session.Principal,
                arguments,
                ParentPid: session.ProcessId,
                WorkingDirectory: VirtualPath.Parse(session.CurrentDirectory),
                ApprovalPrompt: exec.Options.ApprovalPrompt,
                Environment: ChildEnvironment(session, assignments),
                StandardInput: exec.In,
                StandardOutput: exec.Out,
                StandardError: exec.Err,
                CommandName: name,
                ProcessStarted: exec.ProcessStarted),
            fullScreen: null,
            exec.Token).ConfigureAwait(false);

        switch (launch.Status)
        {
            case AppLaunchStatus.FocusedExisting:
                exec.Line.LastFailure = ShellFailure.None;
                return 0;
            case AppLaunchStatus.Launched when launch.Process is { } process:
                exec.Line.LastFailure = ShellFailure.None;
                try
                {
                    return await _orchestrator.WaitForExitAsync(process.Pid, exec.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (exec.Token.IsCancellationRequested)
                {
                    // Ctrl+C: SIGINT ends the GUI program, as it would under a Linux terminal.
                    await _orchestrator.InterruptAsync(process.Pid).ConfigureAwait(false);
                    return InterruptedExitCode;
                }
            case AppLaunchStatus.NotFound or AppLaunchStatus.Disabled:
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.CommandNotFound", name]).ConfigureAwait(false);
                exec.Line.LastFailure = launch.Status == AppLaunchStatus.NotFound ? ShellFailure.CommandNotFound : ShellFailure.Disabled;
                return CommandNotFoundExitCode;
            case AppLaunchStatus.ElevationDenied:
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.ElevationDenied", name]).ConfigureAwait(false);
                exec.Line.LastFailure = ShellFailure.ElevationDenied;
                return 1;
            default:
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.Error", name, launch.ErrorMessage ?? launch.ErrorCode ?? string.Empty]).ConfigureAwait(false);
                exec.Line.LastFailure = ShellFailure.Fault;
                return 1;
        }
    }

    /// <summary>
    /// <c>sudo</c>'s side of the shell (ADR 0051, section 4; ADR 0042): refuses a shell built-in,
    /// asks an administrator's credentials, then runs the command as root, as a child of the caller
    /// (the <c>sudo</c> process) in the shell's directory, without access to the shell.
    /// </summary>
    internal async ValueTask<int> RunElevatedAsync(
        ShellSession session,
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        ShellExecutionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (arguments.Count == 0 || string.IsNullOrEmpty(arguments[0]))
        {
            throw new ArgumentException("A command name is required.", nameof(arguments));
        }

        string name = arguments[0];
        Microsoft.Extensions.Localization.IStringLocalizer texts = ShellText.For(session);
        if (IsSpecialBuiltin(name))
        {
            await error.WriteLineAsync("sudo: " + texts["Sudo.ShellBuiltin", name]).ConfigureAwait(false);
            return 1;
        }

        ShellCommandResolution? resolution = Resolver.Resolve(name, [.. arguments.Skip(1)]);
        if (resolution is null || !_enablement.IsEnabled(resolution.AppId))
        {
            await error.WriteLineAsync("sudo: " + texts["Shell.CommandNotFound", name]).ConfigureAwait(false);
            return 1;
        }

        // ADR 0136: `sudo nice -n -5 cmd` is the one shell-control command sudo accepts, because nice
        // only execs its command: the shell's own state is never touched.
        bool isNice = string.Equals(resolution.AppId, NiceCommandId, StringComparison.Ordinal);
        if (DeclaresShellControl(resolution.Manifest) && !isNice)
        {
            // The target never reaches the shell: an elevated `cd` would have nothing to act on.
            await error.WriteLineAsync("sudo: " + texts["Sudo.ShellBuiltin", name]).ConfigureAwait(false);
            return 1;
        }

        if (options.CredentialPrompt is not { } prompt)
        {
            await error.WriteLineAsync("sudo: " + texts["Sudo.TerminalRequired"]).ConfigureAwait(false);
            return 1;
        }

        ShellElevation? ticket = await prompt(texts["Sudo.RunAsRoot", string.Join(" ", arguments)], cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            await error.WriteLineAsync("sudo: " + texts["Sudo.AuthenticationFailed"]).ConfigureAwait(false);
            return 1;
        }

        // Only the command is elevated: a redirection was already opened by the unelevated shell,
        // so `sudo echo x > /etc/f` fails as on Linux.
        ProcessId? parent = KazoExecutionContext.CurrentOrNull?.ProcessId ?? session.ProcessId;
        StageOutcome outcome = isNice
            // ADR 0136: nice runs as the user and hands the root ticket to the command it execs, which
            // is what runs as root (and may get a lower niceness). The ticket stays single-use.
            ? await StartAsync(
                "sudo", session, resolution, parent, input, output, error, options, elevation: null,
                new ShellSessionAccess(this, session, options, input, ticket),
                ChildEnvironment(session, []), processStarted: null, cancellationToken).ConfigureAwait(false)
            : await StartAsync(
                "sudo", session, resolution, parent, input, output, error, options, ticket, shell: null,
                ChildEnvironment(session, []), processStarted: null, cancellationToken).ConfigureAwait(false);
        return outcome.ExitCode;
    }

    /// <summary>
    /// <c>nice</c>'s side of the shell (ADR 0127): runs the command as a child of the caller (the
    /// <c>nice</c> process) at the caller's niceness plus <paramref name="adjustment"/>, without
    /// access to the shell. Only root may lower the niceness; otherwise, as GNU <c>nice</c> does,
    /// the command still runs, at the caller's niceness, after a warning. Under <c>sudo nice</c>
    /// (ADR 0136), <paramref name="elevation"/> is sudo's root ticket: the command runs as root and
    /// may be given a lower niceness.
    /// </summary>
    internal async ValueTask<int> RunNicedAsync(
        ShellSession session,
        IReadOnlyList<string> arguments,
        int adjustment,
        TextReader input,
        TextWriter output,
        TextWriter error,
        ShellExecutionOptions options,
        CancellationToken cancellationToken,
        ShellElevation? elevation = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (arguments.Count == 0 || string.IsNullOrEmpty(arguments[0]))
        {
            throw new ArgumentException("A command name is required.", nameof(arguments));
        }

        string name = arguments[0];
        Microsoft.Extensions.Localization.IStringLocalizer texts = ShellText.For(session);
        // Like `nice cd`: GNU nice can only exec a program, never a shell built-in.
        ShellCommandResolution? resolution = IsSpecialBuiltin(name) ? null : Resolver.Resolve(name, [.. arguments.Skip(1)]);
        if (resolution is null || !_enablement.IsEnabled(resolution.AppId))
        {
            await error.WriteLineAsync("nice: " + texts["Shell.CommandNotFound", name]).ConfigureAwait(false);
            return CommandNotFoundExitCode;
        }

        IAppExecutionContext? caller = KazoExecutionContext.CurrentOrNull;
        int current = caller is null ? ProcessNice.Default : caller.Processes.GetNice(caller.ProcessId);
        int nice = ProcessNice.Clamp(current + adjustment);
        if (nice < current && caller?.IsRootElevated != true && elevation is null)
        {
            await error.WriteLineAsync("nice: " + texts["Nice.PermissionDenied"]).ConfigureAwait(false);
            nice = current;
        }

        // A command that drives its shell (`nice watch …`, `nice nice …`) still gets it (ADR 0051 §3).
        IShellSession? shell = session.ProvidesShellControl && DeclaresShellControl(resolution.Manifest)
            ? new ShellSessionAccess(this, session, options, input)
            : null;
        ProcessId? parent = caller?.ProcessId ?? session.ProcessId;
        StageOutcome outcome = await StartAsync(
            "nice", session, resolution, parent, input, output, error, options, elevation, shell,
            ChildEnvironment(session, []), processStarted: null, cancellationToken, nice).ConfigureAwait(false);
        return outcome.ExitCode;
    }

    /// <summary>Starts a resolved command through the orchestrator and maps its launch status to an exit code.</summary>
    private async Task<StageOutcome> StartAsync(
        string caller,
        ShellSession session,
        ShellCommandResolution resolution,
        ProcessId? parent,
        TextReader input,
        TextWriter output,
        TextWriter error,
        ShellExecutionOptions options,
        ShellElevation? elevation,
        IShellSession? shell,
        IReadOnlyDictionary<string, string>? environment,
        Action<ProcessRecord>? processStarted,
        CancellationToken cancellationToken,
        int? nice = null)
    {
        string name = resolution.CommandName;
        Microsoft.Extensions.Localization.IStringLocalizer texts = ShellText.For(session);
        // ADR 0228 §2: an old command name (hackpkg) still runs the new command, after a warning.
        if (DeprecatedCommandNames.TryGetReplacement(name, out string replacement))
        {
            await error.WriteLineAsync(texts["Shell.DeprecatedCommand", name, replacement]).ConfigureAwait(false);
        }

        AppLaunchResult launch = await _orchestrator.LaunchAsync(
            new AppLaunchRequest(
                resolution.AppId,
                session.Principal,
                resolution.Arguments,
                ParentPid: parent,
                WorkingDirectory: VirtualPath.Parse(session.CurrentDirectory),
                ElevationToken: elevation?.Token,
                ElevationScope: elevation?.Scope,
                ApprovalPrompt: options.ApprovalPrompt,
                // A shell with no process of its own (or a subshell copy) passes its environment explicitly.
                Environment: environment ?? (parent is null ? session.GetVariables() : null),
                StandardInput: input,
                StandardOutput: output,
                StandardError: error,
                Shell: shell,
                CommandName: name,
                ProcessStarted: processStarted)
            {
                Nice = nice,
            },
            // Only a command writing to the terminal itself may take the screen over (nano, top,
            // htop, watch): `top -n1 | head` or `htop > f` fall back to their text output.
            ReferenceEquals(output, options.StandardOutput) ? options.FullScreen : null,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<FileSystemError> denials = launch.PermissionDenials ?? [];
        switch (launch.Status)
        {
            case AppLaunchStatus.Launched or AppLaunchStatus.FocusedExisting:
                if (launch.ExitCode == KilledExitCode
                    && launch.Process?.ExitReason is ProcessExitReason.Killed or ProcessExitReason.Timeout
                        or ProcessExitReason.CpuLimitExceeded or ProcessExitReason.MemoryLimitExceeded
                        or ProcessExitReason.OutOfMemory)
                {
                    // Like bash for a job ended by SIGKILL (kill -9, its worker was terminated, ADR 0070,
                    // a hard CPU or memory limit, ADR 0125, or the OOM killer, ADR 0126).
                    await error.WriteLineAsync(texts["Shell.Killed"]).ConfigureAwait(false);
                }
                else if (launch.ExitCode == CpuLimitExitCode && launch.Process?.ExitReason == ProcessExitReason.CpuLimitExceeded)
                {
                    // Like bash for a job ended by SIGXCPU (ADR 0125, section 2).
                    await error.WriteLineAsync(texts["Shell.CpuLimitExceeded"]).ConfigureAwait(false);
                }

                return StageOutcome.Completed(launch.ExitCode ?? 0, denials, elevation is not null);
            case AppLaunchStatus.NotFound:
                await error.WriteLineAsync($"{caller}: " + texts["Shell.CommandNotFound", name]).ConfigureAwait(false);
                return StageOutcome.Failed(CommandNotFoundExitCode, ShellFailure.CommandNotFound);
            case AppLaunchStatus.Disabled:
                await error.WriteLineAsync($"{caller}: " + texts["Shell.CommandNotFound", name]).ConfigureAwait(false);
                return StageOutcome.Failed(CommandNotFoundExitCode, ShellFailure.Disabled);
            case AppLaunchStatus.ElevationDenied:
                await error.WriteLineAsync($"{caller}: " + texts["Shell.ElevationDenied", name]).ConfigureAwait(false);
                return StageOutcome.Failed(1, ShellFailure.ElevationDenied);
            default:
                if (cancellationToken.IsCancellationRequested)
                {
                    return StageOutcome.Failed(InterruptedExitCode, ShellFailure.None);
                }

                if (launch.ErrorCode == AppLifecycleOrchestrator.ProcessLimitErrorCode)
                {
                    // ADR 0125: the nproc limit refused the fork (EAGAIN), as bash reports it.
                    await error.WriteLineAsync($"{caller}: " + texts["Shell.ForkFailed"]).ConfigureAwait(false);
                    return StageOutcome.Failed(1, ShellFailure.Fault);
                }

                await error.WriteLineAsync($"{caller}: " + texts["Shell.Error", name, launch.ErrorMessage ?? launch.ErrorCode ?? string.Empty]).ConfigureAwait(false);
                return StageOutcome.Failed(1, ShellFailure.Fault);
        }
    }

    /// <summary>The <c>nice</c> command's app id: the one shell-control command <c>sudo</c> runs (ADR 0136).</summary>
    private const string NiceCommandId = "org.kazoos.commands.nice";

    /// <summary>Whether a command asks for its calling shell (ADR 0051, section 3).</summary>
    private static bool DeclaresShellControl(AppManifest manifest) =>
        manifest.Capabilities.Contains(AppCapabilities.ShellControl, StringComparer.Ordinal);

    /// <summary>Quotes a field for <c>set -x</c> when it would not read back as one word.</summary>
    private static string QuoteForTrace(string field) =>
        field.Length > 0 && field.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '/' or '=' or ':' or ',' or '+' or '%' or '@')
            ? field
            : "'" + field.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Looks <paramref name="name"/> up in <c>~/.kazoos/aliases</c> (<c>name=value</c> lines), as the shell's user.</summary>
    private static async Task<string?> TryReadAliasAsync(ShellSession session, string name, CancellationToken cancellationToken)
    {
        if (session.Host is null)
        {
            return null;
        }

        string content;
        try
        {
            using (KazoExecutionContext.Enter(session.Host))
            {
                string path = $"{session.HomeDirectory.TrimEnd('/')}/{AliasFileRelativePath}";
                if (!await KazoFile.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                content = await KazoFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        foreach (string rawLine in content.Split('\n'))
        {
            string entry = rawLine.TrimEnd('\r').Trim();
            int equals = entry.IndexOf('=');
            if (equals > 0 && string.Equals(entry[..equals], name, StringComparison.Ordinal))
            {
                return entry[(equals + 1)..];
            }
        }

        return null;
    }

    /// <summary>What one launched command did.</summary>
    private sealed record StageOutcome(int ExitCode, ShellFailure Failure, IReadOnlyList<FileSystemError> Denials, bool Elevated)
    {
        public static StageOutcome Completed(int exitCode, IReadOnlyList<FileSystemError> denials, bool elevated) =>
            new(exitCode, ShellFailure.None, denials, elevated);

        public static StageOutcome Failed(int exitCode, ShellFailure failure) => new(exitCode, failure, [], false);
    }
}
