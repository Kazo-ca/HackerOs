using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Shell scripts (ADR 0064): <c>./script.sh</c> needs the execute bit, as <c>execve</c> does,
/// and runs in a new <c>hsh</c> process, child of the shell, with its own variables, functions
/// and positional parameters. <c>sh script.sh</c> only needs read access.
/// </summary>
public sealed partial class ShellInterpreter
{
    /// <summary>Interpreters whose scripts <c>hsh</c> runs itself (a shebang naming one of them).</summary>
    private static readonly HashSet<string> ShellInterpreters = new(StringComparer.Ordinal) { "sh", "bash", "hsh", "dash", "ash", "zsh", "ksh" };

    /// <summary>
    /// Runs a command named by a path (<c>./deploy.sh</c>, <c>/home/alice/bin/x</c>), as
    /// <c>execve</c> would: the file must exist, not be a directory, be executable by the user
    /// (<c>chmod +x</c>) and readable. A <c>#!</c> line picks the interpreter: a shell runs the
    /// script with <c>hsh</c>, another interpreter that is an installed command (<c>#!/usr/bin/env
    /// python</c>) gets the script path as its first argument; without one, the shell runs it.
    /// </summary>
    private async Task<int> RunPathAsync(Exec exec, string path, IReadOnlyList<string> arguments, IReadOnlyList<(string Name, string Value)> assignments)
    {
        ShellSession session = exec.Session;
        string prefix = ErrorPrefix(exec);
        if (session.Host is null)
        {
            await exec.Err.WriteLineAsync(prefix + exec.Text["Shell.NoCallingAppPrograms", path]).ConfigureAwait(false);
            exec.Line.LastFailure = ShellFailure.Fault;
            return NotExecutableExitCode;
        }

        string content;
        try
        {
            using (HackExecutionContext.Enter(session.Host, session.CurrentDirectory))
            {
                if (await HackDirectory.ExistsAsync(path, exec.Token).ConfigureAwait(false))
                {
                    await exec.Err.WriteLineAsync(prefix + exec.Text["Shell.Error", path, exec.Text["Errno.IsDirectory"]]).ConfigureAwait(false);
                    exec.Line.LastFailure = ShellFailure.Fault;
                    return NotExecutableExitCode;
                }

                HackFileInfo file = await HackFileInfo.GetAsync(path, exec.Token).ConfigureAwait(false);
                if (!file.Exists)
                {
                    await exec.Err.WriteLineAsync(prefix + exec.Text["Shell.Error", path, exec.Text["Errno.NoSuchFile"]]).ConfigureAwait(false);
                    exec.Line.LastFailure = ShellFailure.CommandNotFound;
                    return CommandNotFoundExitCode;
                }

                if (!IsExecutableBy(file, session))
                {
                    // execve(2) refuses a file without the execute bit: EACCES. The hint is ours.
                    await exec.Err.WriteLineAsync(prefix + exec.Text["Shell.Error", path, exec.Text["Errno.PermissionDenied"]]).ConfigureAwait(false);
                    await exec.Err.WriteLineAsync(
                        prefix + exec.Text["Shell.NotExecutableHint", path, QuoteForTrace(path)])
                        .ConfigureAwait(false);
                    exec.Line.LastFailure = ShellFailure.Fault;
                    return NotExecutableExitCode;
                }

                content = await HackFile.ReadAllTextAsync(path, exec.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await exec.Err.WriteLineAsync(prefix + exec.Text["Shell.Error", path, ShellText.DescribeError(exception, exec.Text)]).ConfigureAwait(false);
            exec.Line.LastFailure = ShellFailure.Fault;
            return NotExecutableExitCode;
        }

        if (content.Contains('\0'))
        {
            await exec.Err.WriteLineAsync(prefix + exec.Text["Shell.BinaryFile", path]).ConfigureAwait(false);
            exec.Line.LastFailure = ShellFailure.Fault;
            return NotExecutableExitCode;
        }

        exec.Line.LastFailure = ShellFailure.None;
        if (ReadShebang(content) is { } shebang)
        {
            (string interpreter, string? interpreterArgument) = shebang;
            string interpreterName = HackPath.GetFileName(interpreter) ?? interpreter;
            if (interpreterName == "env" && interpreterArgument is not null)
            {
                // #!/usr/bin/env bash: env looks the interpreter up on PATH.
                interpreterName = interpreterArgument.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                interpreterArgument = null;
            }

            if (!ShellInterpreters.Contains(interpreterName))
            {
                ShellCommandResolution? resolution = Resolver.Resolve(interpreterName, []);
                if (resolution is null || !_enablement.IsEnabled(resolution.AppId))
                {
                    await exec.Err.WriteLineAsync(prefix + exec.Text["Shell.BadInterpreter", path, interpreter]).ConfigureAwait(false);
                    exec.Line.LastFailure = ShellFailure.CommandNotFound;
                    return NotExecutableExitCode;
                }

                // The kernel runs `interpreter [arg] script args…`.
                List<string> interpreterArguments = [];
                if (interpreterArgument is not null)
                {
                    interpreterArguments.Add(interpreterArgument);
                }

                interpreterArguments.Add(path);
                interpreterArguments.AddRange(arguments);
                return await LaunchCommandAsync(exec, resolution with { Arguments = interpreterArguments }, assignments).ConfigureAwait(false);
            }

            if (interpreterArgument is not null)
            {
                // #!/bin/sh -e : options of the shell line apply to the script.
                List<string> options = [.. interpreterArgument.Split(' ', StringSplitOptions.RemoveEmptyEntries), path, .. arguments];
                return await RunShAsync(exec, interpreterName, options, assignments, scriptContent: content).ConfigureAwait(false);
            }
        }

        return await RunScriptProcessAsync(exec, path, arguments, assignments, new ScriptSource.Text(content), new HashSet<char>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets whether the current user may execute <paramref name="file"/>: the owner's <c>x</c> bit
    /// for the owner, the group's or others' bit otherwise; root needs any <c>x</c> bit.
    /// </summary>
    private static bool IsExecutableBy(HackFileInfo file, ShellSession session)
    {
        UnixFileMode mode = file.UnixFileMode;
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((mode & anyExecute) == 0)
        {
            return false;
        }

        if (session.UserName == "root" || session.Host?.IsRootElevated == true)
        {
            return true;
        }

        bool owner = file.Owner is { } ownerId
            && (ownerId == session.Principal.UserId.ToString() || ownerId == session.UserName);
        return owner
            ? (mode & UnixFileMode.UserExecute) != 0
            : (mode & (UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    /// <summary>Reads <c>#!interpreter [argument]</c> from the first line.</summary>
    private static (string Interpreter, string? Argument)? ReadShebang(string content)
    {
        if (!content.StartsWith("#!", StringComparison.Ordinal))
        {
            return null;
        }

        int end = content.IndexOf('\n');
        string line = (end < 0 ? content[2..] : content[2..end]).Trim().TrimEnd('\r');
        if (line.Length == 0)
        {
            return null;
        }

        int space = line.IndexOfAny([' ', '\t']);
        return space < 0 ? (line, null) : (line[..space], line[(space + 1)..].Trim());
    }

    /// <summary>
    /// <c>sh [-exuf] [-c command [name [args]]] [script [args]]</c>, also named <c>bash</c> and
    /// <c>hsh</c>: runs a command string, a script file (read access is enough, no <c>x</c> bit)
    /// or, with neither, the commands read from its standard input.
    /// </summary>
    private async Task<int> RunShAsync(
        Exec exec, string name, IReadOnlyList<string> arguments, IReadOnlyList<(string Name, string Value)> assignments, string? scriptContent = null)
    {
        HashSet<char> options = [];
        string? command = null;
        int index = 0;
        for (; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--")
            {
                index++;
                break;
            }

            if (argument is "-c")
            {
                if (index + 1 >= arguments.Count)
                {
                    await exec.Err.WriteLineAsync(exec.Text["Shell.OptionRequiresArgument", name, "-c"]).ConfigureAwait(false);
                    return SyntaxErrorExitCode;
                }

                command = arguments[++index];
                continue;
            }

            if (argument is "-s" or "-i" or "-l" or "--login" or "--norc" or "--noprofile" or "--posix")
            {
                continue;
            }

            if (argument.Length > 1 && argument[0] == '-' && argument[1..].All(ch => ch is 'e' or 'u' or 'x' or 'f' or 'v'))
            {
                foreach (char letter in argument[1..])
                {
                    options.Add(letter);
                }

                continue;
            }

            if (argument.StartsWith('-') && argument.Length > 1)
            {
                await exec.Err.WriteLineAsync(exec.Text["Shell.InvalidOption", name, argument]).ConfigureAwait(false);
                await exec.Err.WriteLineAsync(exec.Text["Shell.ShUsage", name]).ConfigureAwait(false);
                return SyntaxErrorExitCode;
            }

            break;
        }

        IReadOnlyList<string> rest = [.. arguments.Skip(index)];
        if (command is not null)
        {
            // sh -c 'cmd' name args: $0 is name, $1… are args.
            string scriptName = rest.Count > 0 ? rest[0] : name;
            return await RunScriptProcessAsync(exec, scriptName, [.. rest.Skip(1)], assignments, new ScriptSource.Text(command), options).ConfigureAwait(false);
        }

        if (rest.Count > 0)
        {
            string path = rest[0];
            string? content = scriptContent ?? await ReadScriptFileAsync(exec, name, path).ConfigureAwait(false);
            if (content is null)
            {
                return CommandNotFoundExitCode;
            }

            return await RunScriptProcessAsync(exec, path, [.. rest.Skip(1)], assignments, new ScriptSource.Text(content), options).ConfigureAwait(false);
        }

        // No script: read commands from standard input (`echo ls | sh`, or typed until Ctrl+D).
        return await RunScriptProcessAsync(exec, name, [], assignments, new ScriptSource.Input(exec.In), options).ConfigureAwait(false);
    }

    /// <summary>Reads a script for <c>sh file</c> or <c>. file</c>; prints <c>name: file: reason</c> and returns null when it cannot.</summary>
    private static async Task<string?> ReadScriptFileAsync(Exec exec, string name, string path)
    {
        ShellSession session = exec.Session;
        if (session.Host is null)
        {
            await exec.Err.WriteLineAsync($"{name}: " + exec.Text["Shell.NoCallingAppFiles", path]).ConfigureAwait(false);
            return null;
        }

        try
        {
            using (HackExecutionContext.Enter(session.Host, session.CurrentDirectory))
            {
                if (await HackDirectory.ExistsAsync(path, exec.Token).ConfigureAwait(false))
                {
                    await exec.Err.WriteLineAsync($"{name}: " + exec.Text["Shell.Error", path, exec.Text["Errno.IsDirectory"]]).ConfigureAwait(false);
                    return null;
                }

                return await HackFile.ReadAllTextAsync(path, exec.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await exec.Err.WriteLineAsync($"{name}: " + exec.Text["Shell.Error", path, ShellText.DescribeError(exception, exec.Text)]).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Runs a script in a new <c>hsh</c> process, child of the shell (<c>fork</c> + <c>exec</c>):
    /// its own variables (the exported ones inherited), functions, options and positional
    /// parameters; its <c>cd</c> and <c>exit</c> stay inside. The process ends with the script's
    /// status after its <c>trap … EXIT</c> action ran.
    /// </summary>
    private async Task<int> RunScriptProcessAsync(
        Exec exec,
        string scriptName,
        IReadOnlyList<string> arguments,
        IReadOnlyList<(string Name, string Value)> assignments,
        ScriptSource source,
        IReadOnlySet<char> options)
    {
        ShellSession parent = exec.Session;
        if (parent.Host is null)
        {
            await exec.Err.WriteLineAsync("hsh: " + exec.Text["Shell.NoCallingAppScripts", scriptName]).ConfigureAwait(false);
            return NotExecutableExitCode;
        }

        if (parent.CallDepth >= MaximumCallDepth)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NestingExceeded", scriptName]).ConfigureAwait(false);
            return 1;
        }

        // A script given as text is checked before any process exists, as bash reports a syntax
        // error of the whole file before running its first line.
        ShellParseResult? parsed = null;
        if (source is ScriptSource.Text text)
        {
            parsed = ShellParser.Parse(text.Content);
            if (parsed.Error is { } error)
            {
                await exec.Err.WriteLineAsync(exec.Text["Shell.ScriptLineError", scriptName, LineOf(text.Content, error.Offset), error.Localize(exec.Text)]).ConfigureAwait(false);
                return SyntaxErrorExitCode;
            }
        }

        ProcessRecord process;
        IAppExecutionContext context;
        try
        {
            (process, context) = _orchestrator.StartShellProcess(
                parent.Host,
                parent.Principal,
                [scriptName, .. arguments],
                VirtualPath.Parse(parent.CurrentDirectory),
                ChildEnvironment(parent, assignments),
                exec.ProcessStarted);
        }
        catch (ProcessLimitExceededException)
        {
            // fork(2) failed on the nproc limit (ADR 0125): bash's "fork: Resource temporarily unavailable".
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.ForkFailed"]).ConfigureAwait(false);
            return 1;
        }

        ShellSession script = new(context, parent.Principal, isInteractive: false)
        {
            IsScript = true
        };
        script.ScriptName = scriptName;
        script.PositionalParameters = arguments;
        script.CallDepth = parent.CallDepth + 1;
        foreach (char option in options)
        {
            script.Options.Add(option);
        }

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(exec.Token, context.CancellationToken);
        Exec scriptExec = exec with
        {
            Session = script,
            Token = cancellation.Token,
            Control = new ControlState(),
            InCondition = false,
            SingleStage = false,
            LoopDepth = 0,
            CanReturn = false,
            Source = source is ScriptSource.Text code ? code.Content : string.Empty
        };

        int status = 1;
        try
        {
            using (HackExecutionContext.Enter(context))
            {
                status = source switch
                {
                    ScriptSource.Text when parsed!.Commands is null => 0,
                    ScriptSource.Text => await RunListAsync(scriptExec, parsed!.Commands!).ConfigureAwait(false),
                    ScriptSource.Input input => await RunInputAsync(scriptExec, input.Reader).ConfigureAwait(false),
                    _ => 0
                };
                status = script.ExitRequestedStatus ?? status;
                if (cancellation.IsCancellationRequested)
                {
                    status = exec.Token.IsCancellationRequested ? InterruptedExitCode : 143;
                }

                status = await RunExitTrapAsync(scriptExec, status).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status = exec.Token.IsCancellationRequested ? InterruptedExitCode : 143;
        }
        finally
        {
            // The script's process ends with its status (unless it was killed first).
            _orchestrator.CompleteShellProcess(process.Pid, status);
        }

        return status;
    }

    /// <summary>Runs the <c>trap … EXIT</c> action of a script; an <c>exit n</c> inside it changes the status.</summary>
    private async Task<int> RunExitTrapAsync(Exec exec, int status)
    {
        ShellSession session = exec.Session;
        if (session.ExitTrap is not { } action)
        {
            return status;
        }

        session.ExitTrap = null;
        int? requested = session.ExitRequestedStatus;
        session.LastExitCode = status;
        ShellParseResult parsed = ShellParser.Parse(action);
        if (parsed.Commands is null)
        {
            return status;
        }

        ShellSession trapSession = session.Fork();
        await RunListAsync(exec with { Session = trapSession, Control = new ControlState(), Token = CancellationToken.None }, parsed.Commands).ConfigureAwait(false);
        return trapSession.ExitRequestedStatus ?? requested ?? status;
    }

    /// <summary>
    /// <c>sh</c> reading its standard input: lines are gathered until they form complete commands
    /// (an open <c>if</c> or quote waits for more), then run, until end of input or <c>exit</c>.
    /// </summary>
    private async Task<int> RunInputAsync(Exec exec, TextReader reader)
    {
        System.Text.StringBuilder pending = new();
        int status = 0;
        while (!exec.Token.IsCancellationRequested && exec.Session.ExitRequestedStatus is null)
        {
            string? line = await reader.ReadLineAsync(exec.Token).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            pending.Append(line).Append('\n');
            string text = pending.ToString();
            ShellParseResult parsed = ShellParser.Parse(text);
            if (parsed.IsIncomplete)
            {
                continue;
            }

            pending.Clear();
            if (parsed.Error is { } error)
            {
                await exec.Err.WriteLineAsync(exec.Text["Shell.Error", exec.Session.ScriptName, error.Localize(exec.Text)]).ConfigureAwait(false);
                status = SyntaxErrorExitCode;
                exec.Session.LastExitCode = status;
                continue;
            }

            if (parsed.Commands is not null)
            {
                status = await RunListAsync(exec with { Source = text }, parsed.Commands).ConfigureAwait(false);
                exec.Session.LastExitCode = status;
                exec.Control.Reset();
            }
        }

        if (pending.Length > 0 && ShellParser.Parse(pending.ToString()).Error is { } unfinished)
        {
            await exec.Err.WriteLineAsync(exec.Text["Shell.Error", exec.Session.ScriptName, unfinished.Localize(exec.Text)]).ConfigureAwait(false);
            status = SyntaxErrorExitCode;
        }

        return status;
    }

    /// <summary>Where a script's commands come from.</summary>
    private abstract record ScriptSource
    {
        /// <summary>A whole script or command string.</summary>
        public sealed record Text(string Content) : ScriptSource;

        /// <summary>Commands read from standard input.</summary>
        public sealed record Input(TextReader Reader) : ScriptSource;
    }
}
