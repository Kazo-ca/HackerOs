using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// The state of one shell (ADR 0050, section 1): its process, its history and the exit code of
/// the last pipeline. The working directory and the environment are deliberately <b>not</b>
/// copied here: they are those of the shell's process (section 5), read and changed through
/// <see cref="IAppEnvironmentGateway"/>, so <c>ps</c>, <c>/proc/&lt;pid&gt;/cwd</c> and every
/// command the shell starts see exactly what the shell sees.
/// </summary>
/// <remarks>
/// The shell's process is the process of the app hosting it: the Terminal window for the
/// terminal, the calling app for <c>ExecuteCommandIntent</c>. A host that embeds several shells
/// (e.g. one terminal tab per folder in the code editor) therefore shares one directory between
/// them, as threads of one Linux process share theirs.
/// </remarks>
public sealed class ShellSession
{
    private readonly List<string> _history = [];
    private readonly IAppEnvironmentGateway _environment;
    private readonly Dictionary<string, string> _shellVariables = new(StringComparer.Ordinal);
    private readonly List<List<SavedVariable>> _localScopes = [];
    private int _historyIndex;

    /// <summary>Creates a shell running in the process of <paramref name="host"/>.</summary>
    /// <param name="host">
    /// The hosting app's context: its process becomes the parent of every command, its file
    /// gateway opens redirection targets (with its capabilities and the file's mode bits, D16).
    /// </param>
    /// <param name="principal">The user the shell and its commands run as.</param>
    /// <param name="isInteractive">
    /// <see langword="true"/> for a terminal (history, approval prompts, streamed output);
    /// <see langword="false"/> for <c>ExecuteCommandIntent</c> (ADR 0050, section 7).
    /// </param>
    public ShellSession(IAppExecutionContext host, AuthenticatedPrincipal principal, bool isInteractive = true)
        : this(principal, host.Environment, host, host.ProcessId, isInteractive)
    {
        // ADR 0050 section 5: the shell adds TERM to its own environment and keeps PWD current.
        TrySetVariable("TERM", "xterm-256color");
        TrySetVariable("PWD", CurrentDirectory);
    }

    private ShellSession(
        AuthenticatedPrincipal principal,
        IAppEnvironmentGateway environment,
        IAppExecutionContext? host,
        ProcessId? processId,
        bool isInteractive)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Host = host;
        ProcessId = processId;
        IsInteractive = isInteractive;
    }

    /// <summary>
    /// Creates a non-interactive shell with no hosting process (an <c>ExecuteCommandIntent</c>
    /// whose caller supplied no context): its commands start with no parent, so they get the login
    /// environment, and it cannot open redirection targets.
    /// </summary>
    /// <param name="principal">The user the commands run as.</param>
    /// <param name="workingDirectory">Starting directory; the home directory when omitted.</param>
    /// <param name="hostName">Machine name for <c>$HOSTNAME</c>-like displays.</param>
    internal static ShellSession CreateDetached(AuthenticatedPrincipal principal, VirtualPath? workingDirectory, string hostName)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string directory = workingDirectory?.Value ?? principal.HomePath;
        Dictionary<string, string> variables = new(
            Processes.LoginEnvironment.Build(principal.LoginName.Value, principal.HomePath, environmentFile: null),
            StringComparer.Ordinal)
        {
            ["PWD"] = directory
        };
        return new ShellSession(
            principal,
            new DetachedShellEnvironment(principal.LoginName.Value, hostName, directory, variables),
            host: null,
            processId: null,
            isInteractive: false);
    }

    /// <summary>Gets the hosting app's context, or <see langword="null"/> for a detached shell.</summary>
    public IAppExecutionContext? Host { get; }

    /// <summary>Gets the user the shell runs as.</summary>
    public AuthenticatedPrincipal Principal { get; }

    /// <summary>Gets the shell's process, parent of every command it starts; <see langword="null"/> when detached.</summary>
    public ProcessId? ProcessId { get; }

    /// <summary>Gets whether this is an interactive shell (a terminal).</summary>
    public bool IsInteractive { get; }

    /// <summary>
    /// Gets whether this shell runs a script (ADR 0064): not interactive (no history, no alias
    /// expansion, no job notices), but its commands such as <c>cd</c> and <c>read</c> act on it.
    /// </summary>
    public bool IsScript { get; init; }

    /// <summary>Gets whether commands declaring <c>shell.control</c> receive this shell (ADR 0051): a terminal or a script.</summary>
    public bool ProvidesShellControl => IsInteractive || IsScript;

    /// <summary>Gets or sets the exit code of the last pipeline, read by <c>$?</c>.</summary>
    public int LastExitCode { get; set; }

    /// <summary>
    /// Gets the status <c>exit</c> asked the shell to end with, or <see langword="null"/> while it
    /// keeps running. The interpreter stops the line once it is set; the host of an interactive
    /// shell then ends the shell's process with it (ADR 0058, section 4).
    /// </summary>
    public int? ExitRequestedStatus { get; private set; }

    /// <summary>Records an <c>exit</c> request, reduced modulo 256 as a process exit status is.</summary>
    /// <param name="exitStatus">The requested status.</param>
    /// <returns>The status the shell will exit with (0 to 255).</returns>
    public int RequestExit(int exitStatus)
    {
        // `exit 300` exits with 44 and `exit -1` with 255, as under Linux.
        int status = ((exitStatus % 256) + 256) % 256;
        ExitRequestedStatus = status;
        return status;
    }

    /// <summary>Gets the shell process's working directory.</summary>
    public string CurrentDirectory => _environment.CurrentDirectory;

    /// <summary>Gets <c>HOME</c>, or the principal's home directory when it is unset.</summary>
    public string HomeDirectory => _environment.GetVariable("HOME") is { Length: > 0 } home ? home : Principal.HomePath;

    /// <summary>Gets the login name the shell runs as.</summary>
    public string UserName => _environment.UserName;

    /// <summary>Gets the machine name (first line of <c>/etc/hostname</c>, read at session start).</summary>
    public string HostName => _environment.MachineName;

    /// <summary>Gets the commands entered in this shell, oldest first.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>
    /// Gets one variable of the shell: an exported variable of its process or an unexported shell
    /// variable (ADR 0064), or <see langword="null"/> when it is not set.
    /// </summary>
    public string? GetVariable(string name) =>
        _shellVariables.TryGetValue(name, out string? value) ? value : _environment.GetVariable(name);

    /// <summary>Gets whether <paramref name="name"/> is an exported variable (part of the process environment).</summary>
    public bool IsExported(string name) => !_shellVariables.ContainsKey(name) && _environment.GetVariable(name) is not null;

    /// <summary>Gets the unexported shell variables (what <c>set</c> adds to <c>env</c>).</summary>
    public IReadOnlyDictionary<string, string> GetShellVariables() => new Dictionary<string, string>(_shellVariables, StringComparer.Ordinal);

    /// <summary>
    /// Assigns a variable as <c>NAME=value</c> does (ADR 0064): an exported variable keeps being
    /// exported (its process environment changes), any other becomes an unexported shell variable
    /// that commands do not inherit.
    /// </summary>
    /// <exception cref="ArgumentException">The name is invalid.</exception>
    public void AssignVariable(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ShellParser.IsName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid identifier.", nameof(name));
        }

        if (!_shellVariables.ContainsKey(name) && _environment.GetVariable(name) is not null)
        {
            _environment.SetVariable(name, value);
            return;
        }

        _shellVariables[name] = value;
    }

    /// <summary>Gets or sets the positional parameters <c>$1</c>… of the running script or function.</summary>
    public IReadOnlyList<string> PositionalParameters { get; set; } = [];

    /// <summary>Gets or sets the command <c>trap … EXIT</c> registered, run when the script or shell exits.</summary>
    public string? ExitTrap { get; set; }

    /// <summary>Gets or sets <c>$0</c>: the shell's name, or the path of the script it runs.</summary>
    public string ScriptName { get; set; } = "hsh";

    /// <summary>Gets the functions defined in this shell, by name.</summary>
    public Dictionary<string, ShellFunctionDefinition> Functions { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the shell's single-letter options set by <c>set -e</c>, <c>set -u</c>, <c>set -x</c>,
    /// <c>set -f</c> (ADR 0064): errexit, nounset, xtrace, noglob.
    /// </summary>
    public HashSet<char> Options { get; private set; } = [];

    /// <summary>Gets the shell's background jobs (<c>&amp;</c>), shared with its subshell copies.</summary>
    public ShellJobTable Jobs { get; private set; } = new();

    /// <summary>Gets or sets the process id of the last background job's last command (<c>$!</c>).</summary>
    public long? LastBackgroundProcessId { get; set; }

    /// <summary>Gets whether this is a subshell copy (<see cref="Fork"/>): its environment lives only in the copy.</summary>
    public bool IsSubshell { get; private set; }

    /// <summary>Gets or sets how deep in nested scripts and functions the shell is, to stop runaway recursion.</summary>
    internal int CallDepth { get; set; }

    /// <summary>Starts a function's scope for <c>local</c> variables.</summary>
    internal void PushLocalScope() => _localScopes.Add([]);

    /// <summary>Ends a function's scope: every variable made <c>local</c> in it gets its previous value back.</summary>
    internal void PopLocalScope()
    {
        if (_localScopes.Count == 0)
        {
            return;
        }

        List<SavedVariable> saved = _localScopes[^1];
        _localScopes.RemoveAt(_localScopes.Count - 1);
        for (int index = saved.Count - 1; index >= 0; index--)
        {
            SavedVariable variable = saved[index];
            _shellVariables.Remove(variable.Name);
            TryUnsetEnvironment(variable.Name);
            if (variable.Value is null)
            {
                continue;
            }

            if (variable.Exported)
            {
                TrySetVariable(variable.Name, variable.Value);
            }
            else
            {
                _shellVariables[variable.Name] = variable.Value;
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="name"/> local to the running function (<c>local</c>): its current value
    /// is saved and restored when the function returns. Returns false outside a function.
    /// </summary>
    internal bool MakeLocal(string name)
    {
        if (_localScopes.Count == 0)
        {
            return false;
        }

        List<SavedVariable> scope = _localScopes[^1];
        if (scope.Any(variable => variable.Name == name))
        {
            return true;
        }

        scope.Add(new SavedVariable(name, GetVariable(name), IsExported(name)));
        return true;
    }

    /// <summary>Gets whether a function is running (for <c>local</c> and <c>return</c>).</summary>
    internal bool InFunction => _localScopes.Count > 0;

    private void TryUnsetEnvironment(string name)
    {
        try
        {
            _environment.SetVariable(name, null);
        }
        catch (NotSupportedException)
        {
        }
    }

    private sealed record SavedVariable(string Name, string? Value, bool Exported);

    /// <summary>Gets a copy of the shell process's environment.</summary>
    public IReadOnlyDictionary<string, string> GetVariables() => _environment.GetVariables();

    /// <summary>
    /// Sets (or, with <see langword="null"/>, removes) an exported variable of the shell's own
    /// process, as <c>export</c> does: an unexported shell variable of that name becomes exported.
    /// </summary>
    /// <exception cref="ArgumentException">The name or value is invalid.</exception>
    public void SetVariable(string name, string? value)
    {
        _shellVariables.Remove(name);
        _environment.SetVariable(name, value);
    }

    /// <summary>Removes a variable of the shell, exported or not; nothing happens when it is not set.</summary>
    public void UnsetVariable(string name)
    {
        _shellVariables.Remove(name);
        _environment.SetVariable(name, null);
    }

    /// <summary>
    /// Changes the shell's working directory, like the <c>cd</c> built-in. The target is checked
    /// (existence, type, permission to traverse) under the identity of the code calling: the
    /// <c>cd</c> command's own context when it runs (ADR 0051, section 3), otherwise the host's.
    /// Then the shell's directory, <c>PWD</c> and <c>OLDPWD</c> are updated at once.
    /// </summary>
    /// <param name="path">Absolute, relative (to the shell's directory) or <c>~</c>-prefixed directory.</param>
    /// <param name="cancellationToken">Cancels the existence check.</param>
    /// <exception cref="Kazo.Os.AppSdk.IO.HackDirectoryNotFoundException">The path does not exist.</exception>
    /// <exception cref="Kazo.Os.AppSdk.IO.HackIOException">The path is not a directory.</exception>
    /// <exception cref="Kazo.Os.AppSdk.IO.HackUnauthorizedAccessException">The path cannot be looked up.</exception>
    /// <exception cref="NotSupportedException">The shell is detached.</exception>
    public async Task ChangeDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Host is null)
        {
            throw new NotSupportedException("A detached shell has no process to change directory.");
        }

        string previous = CurrentDirectory;
        IAppExecutionContext identity = HackExecutionContext.CurrentOrNull ?? Host;
        bool sameProcess = ReferenceEquals(identity.Environment, _environment);
        string identityDirectory = identity.Environment.CurrentDirectory;
        string target;

        // The check is a real chdir of the checking process, resolved from the shell's directory
        // (pinned for the scope): like the `cd` built-in, a relative path is the shell's, not the
        // command's. The resulting canonical path is then adopted by the shell.
        using (HackExecutionContext.Enter(identity, previous))
        {
            await HackEnvironment.SetCurrentDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            target = identity.Environment.CurrentDirectory;
        }

        if (!sameProcess)
        {
            // Only the shell moves: the checking process (the `cd` command, or the host of a
            // subshell copy) returns to where it was.
            identity.Environment.SetCurrentDirectory(VirtualPath.Parse(identityDirectory));
            _environment.SetCurrentDirectory(VirtualPath.Parse(target));
        }

        TrySetVariable("OLDPWD", previous);
        TrySetVariable("PWD", target);
    }

    /// <summary>
    /// Creates a copy of this shell, as bash forks a subshell to run a built-in inside a pipe
    /// (ADR 0051, section 3): same user, host, process and history, but a private directory and
    /// environment, so a <c>cd</c> or <c>export</c> in <c>cd /tmp | cat</c> is lost when the stage ends.
    /// </summary>
    public ShellSession Fork()
    {
        ShellSession copy = new(
            Principal,
            new DetachedShellEnvironment(
                _environment.UserName,
                _environment.MachineName,
                CurrentDirectory,
                new Dictionary<string, string>(GetVariables(), StringComparer.Ordinal)),
            Host,
            ProcessId,
            IsInteractive)
        {
            LastExitCode = LastExitCode,
            PositionalParameters = PositionalParameters,
            ScriptName = ScriptName,
            Functions = new Dictionary<string, ShellFunctionDefinition>(Functions, StringComparer.Ordinal),
            Options = [.. Options],
            Jobs = Jobs,
            LastBackgroundProcessId = LastBackgroundProcessId,
            IsSubshell = true,
            IsScript = IsScript,
            CallDepth = CallDepth
        };
        foreach ((string name, string value) in _shellVariables)
        {
            copy._shellVariables[name] = value;
        }

        copy._history.AddRange(_history);
        copy._historyIndex = copy._history.Count;
        return copy;
    }

    /// <summary>Appends a command line to the history (skipping blanks and immediate repeats) and resets navigation.</summary>
    public void AddHistory(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return;
        }

        if (_history.Count == 0 || _history[^1] != commandLine)
        {
            _history.Add(commandLine);
        }

        _historyIndex = _history.Count;
    }

    /// <summary>
    /// Moves through the history: up (previous, <paramref name="delta"/> &lt; 0) or down (next,
    /// &gt; 0). Returns the entry to show, an empty string past the newest entry, or
    /// <see langword="null"/> when there is no history.
    /// </summary>
    public string? NavigateHistory(int delta)
    {
        if (_history.Count == 0)
        {
            return null;
        }

        _historyIndex = Math.Clamp(_historyIndex + delta, 0, _history.Count);
        return _historyIndex == _history.Count ? string.Empty : _history[_historyIndex];
    }

    /// <summary>
    /// Gets the prompt, <c>user@host:dir$</c> (<c>#</c> for root), with the home directory shown
    /// as <c>~</c>, like bash's default <c>PS1</c>.
    /// </summary>
    public string GetPrompt()
    {
        string directory = CurrentDirectory;
        string home = HomeDirectory.TrimEnd('/');
        if (home.Length > 0 && (directory == home || directory.StartsWith(home + "/", StringComparison.Ordinal)))
        {
            directory = "~" + directory[home.Length..];
        }

        char promptChar = UserName == "root" ? '#' : '$';
        return $"{UserName}@{HostName}:{directory}{promptChar}";
    }

    /// <summary>Sets a variable the shell maintains itself, ignoring a host that has no live environment.</summary>
    private void TrySetVariable(string name, string value)
    {
        try
        {
            _environment.SetVariable(name, value);
        }
        catch (NotSupportedException)
        {
            // A context with no live process environment (tests, legacy hosts): nothing to keep current.
        }
    }

    /// <summary>
    /// The environment of a detached shell or of a subshell copy: a private copy, since there is
    /// no process entry of its own.
    /// </summary>
    private sealed class DetachedShellEnvironment(
        string userName,
        string machineName,
        string directory,
        Dictionary<string, string> variables) : IAppEnvironmentGateway
    {
        public string CurrentDirectory { get; private set; } = directory;

        public string UserName => userName;

        public string MachineName => machineName;

        public IReadOnlyList<string> GetCommandLineArgs() => ["hsh"];

        public string? GetVariable(string name) => variables.GetValueOrDefault(name);

        public IReadOnlyDictionary<string, string> GetVariables() => new Dictionary<string, string>(variables, StringComparer.Ordinal);

        public void SetCurrentDirectory(VirtualPath directory) => CurrentDirectory = directory.Value;

        public void SetVariable(string name, string? value)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            if (value is null)
            {
                variables.Remove(name);
            }
            else
            {
                variables[name] = value;
            }
        }
    }
}
