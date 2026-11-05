using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// The shell's special built-ins (ADR 0064, amending ADR 0051 "no built-ins"): only the commands
/// that change the shell's own control flow or state in ways no separate process can — loops,
/// functions, positional parameters, options, <c>eval</c>, <c>source</c> and job waiting. Every
/// other command (<c>cd</c>, <c>export</c>, <c>read</c>, <c>test</c>, <c>jobs</c>…) stays in the
/// catalog.
/// </summary>
public sealed partial class ShellInterpreter
{
    /// <summary>Names the shell runs itself (POSIX special built-ins and job control).</summary>
    private static readonly HashSet<string> SpecialBuiltins = new(StringComparer.Ordinal)
    {
        ":", "break", "continue", "return", "shift", "set", "local", "eval", ".", "source", "wait", "fg", "bg", "exec", "trap", "command", "type", "ulimit"
    };

    /// <summary>Names of the shell binary itself: <c>sh script</c>, <c>bash -c "…"</c>.</summary>
    private static readonly HashSet<string> ShellNames = new(StringComparer.Ordinal) { "sh", "bash", "hsh" };

    /// <summary>
    /// Gets every name the shell runs itself, never looked up in the catalog: the special built-ins
    /// and the shell's own names. The host build refuses an app command or alias with one of these
    /// names (<c>KOSAPP0022</c>, ADR 0098 §4).
    /// </summary>
    public static IReadOnlySet<string> ReservedNames { get; } = new HashSet<string>(SpecialBuiltins.Concat(ShellNames), StringComparer.Ordinal);

    /// <summary>The shell's own entries for <c>help</c> and completion.</summary>
    private static readonly ShellCommandInfo[] BuiltinCommands =
    [
        new("sh", ["bash", "hsh"], "sh [-c command] [-exu] [script [args...]]", "Run a shell script or a command string with hsh"),
        new(":", [], ":", "Do nothing, successfully (shell builtin)"),
        new("break", [], "break [n]", "Leave the n-th enclosing loop (shell builtin)"),
        new("continue", [], "continue [n]", "Start the next iteration of the n-th enclosing loop (shell builtin)"),
        new("return", [], "return [n]", "Return from a function or sourced script (shell builtin)"),
        new("shift", [], "shift [n]", "Drop the first n positional parameters (shell builtin)"),
        new("set", [], "set [-eux] [-o option] [-- args...]", "Set shell options and positional parameters (shell builtin)"),
        new("local", [], "local name[=value]...", "Declare function-local variables (shell builtin)"),
        new("eval", [], "eval [args...]", "Run the arguments as a shell command (shell builtin)"),
        new("source", ["."], "source file [args...]", "Run a file in the current shell (shell builtin)"),
        new("wait", [], "wait [%job|pid...]", "Wait for background jobs (shell builtin)"),
        new("fg", [], "fg [%job]", "Bring a background job to the foreground (shell builtin)"),
        new("bg", [], "bg [%job]", "Resume a job in the background (shell builtin)"),
        new("exec", [], "exec command [args...]", "Replace the shell with a command (shell builtin)"),
        new("trap", [], "trap [action] [EXIT|signal...]", "Run an action when the shell exits (shell builtin)"),
        new("command", [], "command [-v|-V] name [args...]", "Run a command ignoring functions, or describe it (shell builtin)"),
        new("type", [], "type name...", "Describe how a name would be run (shell builtin)"),
        new("ulimit", [], "ulimit [-SH] [-a | -f | -m | -n | -t | -u] [limit]", "Show or change the resource limits of the shell (shell builtin)")
    ];

    /// <summary>
    /// The built-ins as <c>help</c> lists them, their descriptions in <paramref name="culture"/>
    /// (ADR 0104): keys <c>Builtin.&lt;name&gt;</c> of <c>Resources/Strings.resx</c>, usages untouched.
    /// </summary>
    private static IEnumerable<ShellCommandInfo> LocalizeBuiltins(System.Globalization.CultureInfo culture)
    {
        Microsoft.Extensions.Localization.IStringLocalizer texts = Strings.For(culture);
        return BuiltinCommands.Select(command => command with { Description = texts["Builtin." + command.Name] });
    }

    private static bool IsSpecialBuiltin(string name) => SpecialBuiltins.Contains(name);

    /// <summary>Runs a special built-in.</summary>
    private async Task<int> RunBuiltinAsync(Exec exec, string name, IReadOnlyList<string> arguments)
    {
        ShellSession session = exec.Session;
        switch (name)
        {
            case ":":
                return 0;

            case "break" or "continue":
                if (!TryParseCount(arguments, out int levels))
                {
                    await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.LoopCountOutOfRange", name, arguments[0]]).ConfigureAwait(false);
                    return 1;
                }

                if (exec.LoopDepth == 0)
                {
                    await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.OnlyInLoop", name]).ConfigureAwait(false);
                    return 0;
                }

                exec.Control.Kind = name == "break" ? ControlKind.Break : ControlKind.Continue;
                exec.Control.Count = Math.Min(levels, exec.LoopDepth);
                return 0;

            case "return":
                if (!exec.CanReturn)
                {
                    await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.ReturnOutsideFunction"]).ConfigureAwait(false);
                    return 1;
                }

                int returned = session.LastExitCode;
                if (arguments.Count > 0 && !TryParseStatus(arguments[0], out returned))
                {
                    await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NumericArgumentRequired", "return", arguments[0]]).ConfigureAwait(false);
                    returned = 2;
                }

                exec.Control.Kind = ControlKind.Return;
                exec.Control.Count = 1;
                return returned;

            case "shift":
                int count = 1;
                if (arguments.Count > 0 && (!int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out count)))
                {
                    await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NumericArgumentRequired", "shift", arguments[0]]).ConfigureAwait(false);
                    return 1;
                }

                if (count > session.PositionalParameters.Count)
                {
                    return 1;
                }

                session.PositionalParameters = [.. session.PositionalParameters.Skip(count)];
                return 0;

            case "set":
                return await RunSetAsync(exec, arguments).ConfigureAwait(false);

            case "local":
                return await RunLocalAsync(exec, arguments).ConfigureAwait(false);

            case "eval":
                return await RunEvalAsync(exec, string.Join(' ', arguments)).ConfigureAwait(false);

            case "." or "source":
                return await RunSourceAsync(exec, name, arguments).ConfigureAwait(false);

            case "wait":
                return await RunWaitAsync(exec, arguments).ConfigureAwait(false);

            case "fg":
                return await RunForegroundAsync(exec, arguments).ConfigureAwait(false);

            case "bg":
                ShellJob? background = session.Jobs.Find(arguments.Count > 0 ? arguments[0] : "%%");
                if (background is null)
                {
                    await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NoSuchJob", "bg", arguments.Count > 0 ? arguments[0] : exec.Text["Shell.CurrentJob"]]).ConfigureAwait(false);
                    return 1;
                }

                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.JobAlreadyInBackground", background.Id]).ConfigureAwait(false);
                return 0;

            case "exec":
                if (arguments.Count == 0)
                {
                    return 0;
                }

                int status = await DispatchAsync(exec, arguments, []).ConfigureAwait(false);
                session.RequestExit(status);
                return status;

            case "trap":
                return await RunTrapAsync(exec, arguments).ConfigureAwait(false);

            case "command":
                return await RunCommandBuiltinAsync(exec, arguments).ConfigureAwait(false);

            case "ulimit":
                return await RunUlimitAsync(exec, arguments).ConfigureAwait(false);

            case "type":
                int typeStatus = 0;
                foreach (string argument in arguments)
                {
                    string? description = Describe(session, argument, verbose: true, exec.Text);
                    if (description is null)
                    {
                        await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NotFound", "type", argument]).ConfigureAwait(false);
                        typeStatus = 1;
                    }
                    else
                    {
                        await exec.Out.WriteLineAsync(description).ConfigureAwait(false);
                    }
                }

                return typeStatus;

            default:
                return CommandNotFoundExitCode;
        }
    }

    /// <summary>Calls a function: new positional parameters and <c>local</c> scope, <c>return</c> allowed.</summary>
    private async Task<int> CallFunctionAsync(Exec exec, ShellFunctionDefinition function, IReadOnlyList<string> arguments)
    {
        ShellSession session = exec.Session;
        if (session.CallDepth >= MaximumCallDepth)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.FunctionNestingExceeded", function.Name, MaximumCallDepth]).ConfigureAwait(false);
            return 1;
        }

        IReadOnlyList<string> saved = session.PositionalParameters;
        session.PositionalParameters = arguments;
        session.CallDepth++;
        session.PushLocalScope();
        ControlState control = new();
        try
        {
            int status = await RunCommandAsync(exec with { Control = control, CanReturn = true, LoopDepth = 0 }, function.Body).ConfigureAwait(false);
            return status;
        }
        finally
        {
            session.PopLocalScope();
            session.CallDepth--;
            session.PositionalParameters = saved;
        }
    }

    /// <summary><c>set</c>: options (<c>-e -u -x -f</c>, <c>-o name</c>), positional parameters, or the list of variables.</summary>
    private static async Task<int> RunSetAsync(Exec exec, IReadOnlyList<string> arguments)
    {
        ShellSession session = exec.Session;
        if (arguments.Count == 0)
        {
            Dictionary<string, string> all = new(session.GetVariables(), StringComparer.Ordinal);
            foreach ((string name, string value) in session.GetShellVariables())
            {
                all[name] = value;
            }

            foreach ((string name, string value) in all.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                await exec.Out.WriteLineAsync($"{name}={QuoteForTrace(value)}").ConfigureAwait(false);
            }

            return 0;
        }

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--")
            {
                session.PositionalParameters = [.. arguments.Skip(index + 1)];
                return 0;
            }

            if (argument is "-o" or "+o")
            {
                bool enable = argument[0] == '-';
                if (index + 1 >= arguments.Count)
                {
                    foreach ((string option, char letter) in LongOptions)
                    {
                        await exec.Out.WriteLineAsync($"{option,-15}{(session.Options.Contains(letter) ? "on" : "off")}").ConfigureAwait(false);
                    }

                    return 0;
                }

                string longName = arguments[++index];
                char? flag = LongOptions.FirstOrDefault(pair => pair.Name == longName).Letter;
                if (flag is null or '\0')
                {
                    await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.InvalidOptionName", "set", longName]).ConfigureAwait(false);
                    return 2;
                }

                SetOption(session, flag.Value, enable);
                continue;
            }

            if (argument.Length > 1 && argument[0] is '-' or '+')
            {
                bool enable = argument[0] == '-';
                foreach (char letter in argument[1..])
                {
                    if (letter is not ('e' or 'u' or 'x' or 'f' or 'v' or 'C' or 'n' or 'a' or 'h' or 'm' or 'b'))
                    {
                        await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.InvalidOption", "set", $"{argument[0]}{letter}"]).ConfigureAwait(false);
                        return 2;
                    }

                    SetOption(session, letter, enable);
                }

                continue;
            }

            session.PositionalParameters = [.. arguments.Skip(index)];
            return 0;
        }

        return 0;
    }

    private static readonly (string Name, char Letter)[] LongOptions =
    [
        ("errexit", 'e'), ("noglob", 'f'), ("nounset", 'u'), ("xtrace", 'x'), ("verbose", 'v'), ("noclobber", 'C')
    ];

    private static void SetOption(ShellSession session, char letter, bool enable)
    {
        if (enable)
        {
            session.Options.Add(letter);
        }
        else
        {
            session.Options.Remove(letter);
        }
    }

    private static async Task<int> RunLocalAsync(Exec exec, IReadOnlyList<string> arguments)
    {
        ShellSession session = exec.Session;
        if (!session.InFunction)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.LocalOutsideFunction"]).ConfigureAwait(false);
            return 1;
        }

        int status = 0;
        foreach (string argument in arguments)
        {
            int equals = argument.IndexOf('=');
            string name = equals < 0 ? argument : argument[..equals];
            if (!ShellParser.IsName(name))
            {
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NotValidIdentifier", "local", argument]).ConfigureAwait(false);
                status = 1;
                continue;
            }

            session.MakeLocal(name);
            session.AssignVariable(name, equals < 0 ? session.GetVariable(name) ?? string.Empty : argument[(equals + 1)..]);
        }

        return status;
    }

    /// <summary><c>eval</c>: parses and runs the joined arguments in the current shell.</summary>
    private async Task<int> RunEvalAsync(Exec exec, string text)
    {
        ShellParseResult parsed = ShellParser.Parse(text);
        if (parsed.Error is { } error)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.Error", "eval", error.Localize(exec.Text)]).ConfigureAwait(false);
            return SyntaxErrorExitCode;
        }

        if (parsed.Commands is null)
        {
            return 0;
        }

        exec.Session.CallDepth++;
        try
        {
            if (exec.Session.CallDepth >= MaximumCallDepth)
            {
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NestingExceeded", "eval"]).ConfigureAwait(false);
                return 1;
            }

            return await RunListAsync(exec with { Source = text }, parsed.Commands).ConfigureAwait(false);
        }
        finally
        {
            exec.Session.CallDepth--;
        }
    }

    /// <summary><c>. file [args]</c>: runs a file in the current shell; <c>return</c> leaves it.</summary>
    private async Task<int> RunSourceAsync(Exec exec, string name, IReadOnlyList<string> arguments)
    {
        ShellSession session = exec.Session;
        if (arguments.Count == 0)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.FilenameArgumentRequired", name]).ConfigureAwait(false);
            return 2;
        }

        string? content = await ReadScriptFileAsync(exec, name, arguments[0]).ConfigureAwait(false);
        if (content is null)
        {
            return 1;
        }

        ShellParseResult parsed = ShellParser.Parse(content);
        if (parsed.Error is { } error)
        {
            await exec.Err.WriteLineAsync(exec.Text["Shell.ScriptLineError", arguments[0], LineOf(content, error.Offset), error.Localize(exec.Text)]).ConfigureAwait(false);
            return SyntaxErrorExitCode;
        }

        if (parsed.Commands is null)
        {
            return 0;
        }

        IReadOnlyList<string> saved = session.PositionalParameters;
        if (arguments.Count > 1)
        {
            session.PositionalParameters = [.. arguments.Skip(1)];
        }

        session.CallDepth++;
        ControlState control = new();
        try
        {
            if (session.CallDepth >= MaximumCallDepth)
            {
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NestingExceeded", name]).ConfigureAwait(false);
                return 1;
            }

            int status = await RunListAsync(exec with { Control = control, CanReturn = true, Source = content }, parsed.Commands).ConfigureAwait(false);
            return status;
        }
        finally
        {
            session.CallDepth--;
            if (arguments.Count > 1)
            {
                session.PositionalParameters = saved;
            }
        }
    }

    /// <summary><c>wait [%n|pid…]</c>: waits for the jobs (all of them without argument); status of the last one.</summary>
    private static async Task<int> RunWaitAsync(Exec exec, IReadOnlyList<string> arguments)
    {
        ShellJobTable jobs = exec.Session.Jobs;
        List<ShellJob> targets = [];
        if (arguments.Count == 0)
        {
            targets.AddRange(jobs.Jobs);
        }
        else
        {
            foreach (string argument in arguments)
            {
                ShellJob? job = argument.StartsWith('%')
                    ? jobs.Find(argument)
                    : jobs.Jobs.FirstOrDefault(candidate => candidate.Processes.Any(pid => pid.Value.ToString(CultureInfo.InvariantCulture) == argument));
                if (job is null)
                {
                    await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NoSuchJob", "wait", argument]).ConfigureAwait(false);
                    return 127;
                }

                targets.Add(job);
            }
        }

        int status = 0;
        foreach (ShellJob job in targets)
        {
            status = await job.Completion.WaitAsync(exec.Token).ConfigureAwait(false);
            status = job.ExitStatus ?? status;
        }

        // `wait` alone waits for everything and, as in bash, returns 0.
        return arguments.Count == 0 ? 0 : status;
    }

    /// <summary><c>fg [%n]</c>: waits for a job in the foreground; Ctrl+C stops it.</summary>
    private async Task<int> RunForegroundAsync(Exec exec, IReadOnlyList<string> arguments)
    {
        ShellSession session = exec.Session;
        ShellJob? job = session.Jobs.Find(arguments.Count > 0 ? arguments[0] : "%%");
        if (job is null)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NoSuchJob", "fg", arguments.Count > 0 ? arguments[0] : exec.Text["Shell.CurrentJob"]]).ConfigureAwait(false);
            return 1;
        }

        await exec.Out.WriteLineAsync(job.Command).ConfigureAwait(false);
        try
        {
            await job.Completion.WaitAsync(exec.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (exec.Token.IsCancellationRequested)
        {
            KillJob(session, job.Id, force: true);
            job.Reported = true;
            session.Jobs.RemoveReported();
            return InterruptedExitCode;
        }

        // A job brought back and finished is not reported again as Done.
        job.Reported = true;
        session.Jobs.RemoveReported();
        return job.ExitStatus ?? 0;
    }

    /// <summary><c>trap action EXIT</c>: the action runs when the shell (or script) exits; signals are accepted and ignored.</summary>
    private static async Task<int> RunTrapAsync(Exec exec, IReadOnlyList<string> arguments)
    {
        ShellSession session = exec.Session;
        if (arguments.Count == 0)
        {
            if (session.ExitTrap is { } action)
            {
                await exec.Out.WriteLineAsync($"trap -- {QuoteForTrace(action)} EXIT").ConfigureAwait(false);
            }

            return 0;
        }

        if (arguments.Count == 1)
        {
            // `trap - ` alone resets nothing in particular; `trap cmd` without a signal is an error in bash.
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.TrapUsage"]).ConfigureAwait(false);
            return 2;
        }

        string command = arguments[0];
        foreach (string signal in arguments.Skip(1))
        {
            if (signal is "EXIT" or "0")
            {
                session.ExitTrap = command is "-" or "" ? null : command;
            }
        }

        return 0;
    }

    /// <summary><c>command [-v|-V] name [args]</c>: runs a command ignoring functions, or tells how a name resolves.</summary>
    private async Task<int> RunCommandBuiltinAsync(Exec exec, IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 0 && arguments[0] is "-v" or "-V")
        {
            bool verbose = arguments[0] == "-V";
            int status = 0;
            foreach (string name in arguments.Skip(1))
            {
                string? description = Describe(exec.Session, name, verbose);
                if (description is null)
                {
                    if (verbose)
                    {
                        await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.NotFound", "command", name]).ConfigureAwait(false);
                    }

                    status = 1;
                    continue;
                }

                await exec.Out.WriteLineAsync(description).ConfigureAwait(false);
            }

            return status;
        }

        if (arguments.Count == 0)
        {
            return 0;
        }

        ShellSession session = exec.Session;
        string commandName = arguments[0];
        IReadOnlyList<string> commandArguments = [.. arguments.Skip(1)];
        if (IsSpecialBuiltin(commandName))
        {
            return await RunBuiltinAsync(exec, commandName, commandArguments).ConfigureAwait(false);
        }

        // Functions are skipped: dispatch on a session view without them.
        Dictionary<string, ShellFunctionDefinition> functions = new(session.Functions, StringComparer.Ordinal);
        session.Functions.Clear();
        try
        {
            return await DispatchAsync(exec, arguments, []).ConfigureAwait(false);
        }
        finally
        {
            foreach ((string name, ShellFunctionDefinition definition) in functions)
            {
                session.Functions.TryAdd(name, definition);
            }
        }
    }

    /// <summary>How <paramref name="name"/> would run, as <c>command -v</c> (short) or <c>type</c> (verbose) say it.</summary>
    /// <param name="session">The shell.</param>
    /// <param name="name">The name to describe.</param>
    /// <param name="verbose">Whether to print the <c>type</c> sentence (translated) rather than the bare path.</param>
    /// <param name="texts">The shell's texts for the verbose sentence; English when <see langword="null"/>.</param>
    private string? Describe(ShellSession session, string name, bool verbose, Microsoft.Extensions.Localization.IStringLocalizer? texts = null)
    {
        texts ??= ShellText.English;
        if (name is "if" or "then" or "else" or "elif" or "fi" or "for" or "while" or "until" or "do" or "done" or "case" or "esac" or "in" or "function")
        {
            return verbose ? texts["Shell.Type.Keyword", name] : name;
        }

        if (session.Functions.ContainsKey(name))
        {
            return verbose ? texts["Shell.Type.Function", name] : name;
        }

        if (IsSpecialBuiltin(name))
        {
            return verbose ? texts["Shell.Type.Builtin", name] : name;
        }

        if (Resolver.Resolve(name, []) is { } command && _enablement.IsEnabled(command.AppId))
        {
            return verbose ? texts["Shell.Type.File", name, $"/usr/bin/{command.Manifest.Terminal!.Name} ({command.AppId})"] : $"/usr/bin/{name}";
        }

        if (ShellNames.Contains(name))
        {
            return verbose ? texts["Shell.Type.File", name, $"/bin/{name}"] : $"/bin/{name}";
        }

        if (Resolver.ResolveApplication(name) is { } application && _enablement.IsEnabled(application.Id))
        {
            return verbose ? texts["Shell.Type.Application", name, application.Name, application.Id] : name;
        }

        return name.Contains('/') ? name : null;
    }

    private static bool TryParseCount(IReadOnlyList<string> arguments, out int count)
    {
        count = 1;
        return arguments.Count == 0
            || (int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 1);
    }

    /// <summary>Parses an exit status argument, reduced modulo 256 like a process status.</summary>
    private static bool TryParseStatus(string text, out int status)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
        {
            status = (int)(((value % 256) + 256) % 256);
            return true;
        }

        status = 0;
        return false;
    }

    /// <summary>1-based line number of an offset, for script error messages.</summary>
    private static int LineOf(string text, int offset) =>
        text.AsSpan(0, Math.Clamp(offset, 0, text.Length)).Count('\n') + 1;
}
