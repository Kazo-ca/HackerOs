using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.Diagnostics;

/// <summary>
/// One process of the OS, modeled on <c>System.Diagnostics.Process</c> (ADR 0056, section 4).
/// Replaces <c>IProcessManager</c> and <c>AppLifecycleOrchestrator</c>, which apps may no longer
/// inject (ADR 0055).
/// </summary>
/// <example>
/// <code>
/// foreach (KazoProcess process in await KazoProcess.GetProcessesAsync())
/// {
///     Console.WriteLine($"{process.Id} {process.ProcessName} {process.CpuUsage:0.0}% {process.WorkingSet64}");
/// }
///
/// KazoProcess editor = await KazoProcess.GetProcessByIdAsync(42);
/// await editor.KillAsync();
/// </code>
/// </example>
/// <remarks>
/// <para>
/// <b>Everything is read from <c>/proc</c></b>, through the app's own file gateway, exactly like
/// <c>ps</c>: <c>/proc/&lt;pid&gt;/status</c>, <c>cmdline</c>, <c>cwd</c> and <c>io</c>, plus the
/// creation time of the <c>/proc/&lt;pid&gt;</c> directory as the start time. A process therefore
/// sees its own process, and the others only with <c>process.list</c> (the visibility rule of
/// <c>/proc</c>, ADR 0046). <c>io</c> and <c>environ</c> are private (<c>0400</c>): for another
/// user's process, <see cref="IoCounters"/> is <see langword="null"/>.
/// </para>
/// <para>
/// Like <c>Process</c>, the values are a snapshot: <see cref="RefreshAsync"/> reads them again. A
/// task manager polls <see cref="GetProcessesAsync"/> every second, like <c>top</c>; there is no
/// push notification (ADR 0056, section 4).
/// </para>
/// <para>
/// Stopping a process is a system call, not a file write: <see cref="KillAsync"/>,
/// <see cref="TerminateAsync"/> and <see cref="CloseMainWindowAsync"/> go through
/// <see cref="IAppProcessGateway"/>, and need <c>process.manage</c> for any process other than the
/// caller's own.
/// </para>
/// <para>
/// From inside, a process sees its own stop request through <see cref="Current"/> and gives the
/// thread back during long work with <see cref="CheckpointAsync"/> (ADR 0069).
/// </para>
/// </remarks>
public sealed partial class KazoProcess
{
    /// <summary>Grace period of <see cref="CloseMainWindowAsync"/> when none is given.</summary>
    public static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(5);

    private const string ProcRoot = "/proc";

    /// <summary>Linux's <c>USER_HZ</c>: the unit of <c>utime</c> and <c>stime</c>.</summary>
    private const int UserHz = 100;

    private KazoProcess(long id) => Id = id;

    /// <summary>Gets the process identifier (PID).</summary>
    public long Id { get; }

    /// <summary>Gets the parent's PID, or <see langword="null"/> for a process started by the OS itself (<c>PPid: 0</c>).</summary>
    public long? ParentId { get; private set; }

    /// <summary>Gets the short process name (<c>comm</c>): the last segment of the app id, 15 characters at most.</summary>
    public string ProcessName { get; private set; } = string.Empty;

    /// <summary>Gets the id of the app the process runs (<c>org.kazoos.text-editor</c>).</summary>
    public string AppId { get; private set; } = string.Empty;

    /// <summary>Gets what kind of app the process runs: window, terminal command or service.</summary>
    public AppKind Kind { get; private set; }

    /// <summary>Gets the owner's opaque user id (<c>Uid</c> of <c>status</c>).</summary>
    public string UserId { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the owner's login name when the OS can tell it (the caller's own user), otherwise the
    /// user id, like <c>ps</c> printing a numeric uid that has no <c>/etc/passwd</c> entry.
    /// </summary>
    public string UserName { get; private set; } = string.Empty;

    /// <summary>Gets the login session the process belongs to.</summary>
    public string SessionId { get; private set; } = string.Empty;

    /// <summary>Gets the scheduler state.</summary>
    public KazoProcessState State { get; private set; }

    /// <summary>Gets the one-letter state code <c>ps</c> prints (<c>R</c>, <c>S</c>, <c>T</c>, <c>Z</c>…).</summary>
    public char StateCode { get; private set; }

    /// <summary>Gets when the process started, in simulated UTC time.</summary>
    public DateTimeOffset StartTime { get; private set; }

    /// <summary>Gets the resident memory, in bytes (<c>VmRSS</c>).</summary>
    public long WorkingSet64 { get; private set; }

    /// <summary>
    /// Gets the private memory, in bytes: the process's own share of the heap (<c>RssAnon</c>),
    /// without the assemblies it shares with others nor what the OS holds for it, like
    /// <c>System.Diagnostics.Process.PrivateMemorySize64</c>. When the OS does not measure memory
    /// (<c>MemSource: simulated</c>, no <c>RssAnon</c> line), it equals <see cref="WorkingSet64"/>.
    /// </summary>
    public long PrivateMemorySize64 { get; private set; }

    /// <summary>Gets the CPU usage at the last scheduler sample, in percent of one core (can exceed 100 on several cores).</summary>
    public double CpuUsage { get; private set; }

    /// <summary>
    /// Gets the CPU time the process spent in its own code (<c>utime</c>, field 14 of
    /// <c>/proc/&lt;pid&gt;/stat</c>), like <c>Process.UserProcessorTime</c>. Measured when the OS
    /// measures CPU time (ADR 0067, <c>CpuSource: measured</c>), otherwise rebuilt from the simulator.
    /// Resolution: 10 ms (<c>USER_HZ</c>).
    /// </summary>
    public TimeSpan UserProcessorTime { get; private set; }

    /// <summary>
    /// Gets the CPU time the process spent inside the OS on its behalf (files, network, clipboard:
    /// <c>stime</c>, field 15), like <c>Process.PrivilegedProcessorTime</c>. Zero when CPU time is simulated.
    /// </summary>
    public TimeSpan PrivilegedProcessorTime { get; private set; }

    /// <summary>Gets <see cref="UserProcessorTime"/> plus <see cref="PrivilegedProcessorTime"/>, like <c>Process.TotalProcessorTime</c>.</summary>
    public TimeSpan TotalProcessorTime => UserProcessorTime + PrivilegedProcessorTime;

    /// <summary>
    /// Gets whether the process responds, like <c>Process.Responding</c>: <see langword="false"/> after
    /// one uninterrupted slice of its code held the thread longer than the OS threshold (ADR 0067,
    /// section 4, <c>Responding: 0</c>). Always <see langword="true"/> when CPU time is not measured.
    /// The OS only reports it: nothing is stopped.
    /// </summary>
    public bool Responding { get; private set; } = true;

    /// <summary>
    /// Gets the niceness (field 19 of <c>/proc/&lt;pid&gt;/stat</c>, ADR 0127): <c>-20</c> (most
    /// favored) to <c>19</c>, <c>0</c> by default, the <c>NI</c> column of <c>ps</c> and <c>top</c>.
    /// It only orders the worker pool's queue: on the main thread it changes nothing.
    /// </summary>
    public int Nice { get; private set; }

    /// <summary>Gets the kernel priority shown next to <see cref="Nice"/> (<c>PR</c> of <c>top</c>): <c>20 + Nice</c>.</summary>
    public int BasePriority => 20 + Nice;

    /// <summary>Gets the arguments of the process, the first being the program, like <c>argv</c>.</summary>
    public IReadOnlyList<string> Arguments { get; private set; } = [];

    /// <summary>Gets the command line, arguments joined by a space (quoted when they contain one).</summary>
    public string CommandLine { get; private set; } = string.Empty;

    /// <summary>Gets the working directory (target of <c>/proc/&lt;pid&gt;/cwd</c>), or <see langword="null"/> when unreadable.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>
    /// Gets the I/O counters of <c>/proc/&lt;pid&gt;/io</c>, or <see langword="null"/> when the file is
    /// not readable (another user's process: the file is <c>0400</c>).
    /// </summary>
    public KazoProcessIoCounters? IoCounters { get; private set; }

    /// <summary>Gets the bytes the process read from persistent storage (<c>read_bytes</c>), <c>0</c> when unreadable.</summary>
    public long ReadBytes => IoCounters?.ReadBytes ?? 0;

    /// <summary>Gets the bytes the process wrote to persistent storage (<c>write_bytes</c>), <c>0</c> when unreadable.</summary>
    public long WriteBytes => IoCounters?.WriteBytes ?? 0;

    /// <summary>Gets every field of <c>/proc/&lt;pid&gt;/status</c>, as read, for a detail view.</summary>
    public IReadOnlyDictionary<string, string> Status { get; private set; } = new Dictionary<string, string>();

    /// <summary>Gets whether the process had exited at the last read (its <c>/proc</c> directory was gone).</summary>
    public bool HasExited { get; private set; }

    /// <summary>Gets every process this app can see, by ascending PID.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The processes; only the caller's own without <c>process.list</c>.</returns>
    /// <exception cref="KazoUnauthorizedAccessException">The app may not read <c>/proc</c> (<c>filesystem.system.read</c>).</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static async Task<KazoProcess[]> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        KazoDirectoryInfo[] directories = await new KazoDirectoryInfo(ProcRoot)
            .GetDirectoriesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        List<KazoProcess> processes = [];
        foreach (KazoDirectoryInfo directory in directories)
        {
            // Only the numeric directories are processes; self, net... are skipped.
            if (directory.IsSymbolicLink || ParsePid(directory.Name) is not { } pid)
            {
                continue;
            }

            KazoProcess process = new(pid);
            // A process can exit between the listing and the read; procps skips it the same way.
            if (await process.TryLoadAsync(new DateTimeOffset(directory.CreationTimeUtc, TimeSpan.Zero), cancellationToken).ConfigureAwait(false))
            {
                processes.Add(process);
            }
        }

        return [.. processes.OrderBy(static process => process.Id)];
    }

    /// <summary>Gets one process by PID.</summary>
    /// <param name="processId">The PID.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentException">No such process is running, or this app cannot see it (like <c>Process.GetProcessById</c>).</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static async Task<KazoProcess> GetProcessByIdAsync(long processId, CancellationToken cancellationToken = default)
    {
        KazoProcess process = new(processId);
        await process.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return process.HasExited
            ? throw new ArgumentException($"Process with an Id of {processId} is not running.", nameof(processId))
            : process;
    }

    /// <summary>Gets the process running the current code.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<KazoProcess> GetCurrentProcessAsync(CancellationToken cancellationToken = default) =>
        GetProcessByIdAsync(KazoExecutionContext.Current.ProcessId.Value, cancellationToken);

    /// <summary>
    /// Reads every value again. When the process has exited (or is no longer visible),
    /// <see cref="HasExited"/> becomes <see langword="true"/> and the other values keep their last state.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        KazoDirectoryInfo directory = await KazoDirectoryInfo.GetAsync(ProcessPath(), cancellationToken).ConfigureAwait(false);
        if (!directory.Exists || !await TryLoadAsync(new DateTimeOffset(directory.CreationTimeUtc, TimeSpan.Zero), cancellationToken).ConfigureAwait(false))
        {
            HasExited = true;
        }
    }

    /// <summary>
    /// Reads the process's environment (<c>/proc/&lt;pid&gt;/environ</c>). It is private, like under
    /// Linux: only the owner of the process (or the System authority) can read it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The variables, or <see langword="null"/> when the file is not readable or the process has exited.</returns>
    public async Task<IReadOnlyDictionary<string, string>?> GetEnvironmentVariablesAsync(CancellationToken cancellationToken = default)
    {
        string? text = await TryReadAsync("environ", cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        foreach (string pair in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator > 0)
            {
                variables[pair[..separator]] = pair[(separator + 1)..];
            }
        }

        return variables;
    }

    /// <summary>
    /// Gets the processes whose parent is this one, among <paramref name="processes"/> (typically the
    /// last <see cref="GetProcessesAsync"/>), by ascending PID.
    /// </summary>
    /// <param name="processes">The process list to search.</param>
    public IReadOnlyList<KazoProcess> GetChildren(IEnumerable<KazoProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return [.. processes.Where(process => process.ParentId == Id).OrderBy(static process => process.Id)];
    }

    /// <summary>
    /// Stops the process immediately, like <c>kill -9</c> (<c>SIGKILL</c>); its descendants stop with
    /// it. The process gets no <see cref="KazoCurrentProcess.StopRequested"/>: its pending
    /// continuations are dropped and its gateways throw <see cref="KazoProcessKilledException"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoUnauthorizedAccessException">It is another process and this app lacks <c>process.manage</c>.</exception>
    /// <exception cref="InvalidOperationException">The process has already exited.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public async Task KillAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAppProcessGateway gateway = KazoExecutionContext.Current.Processes;
        try
        {
            // ADR 0070: the asynchronous form, so a caller running in a worker sees the OS's answer.
            await gateway.KillAsync(ProcessId.FromInt64(Id), ProcessExitReason.Killed, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw Exited(exception);
        }

        HasExited = true;
    }

    /// <summary>
    /// Asks the process to close, like clicking the close button of its window, and waits up to
    /// <paramref name="timeout"/> before the OS forces it, like <c>SIGTERM</c> then <c>SIGKILL</c>.
    /// The process receives <see cref="KazoCurrentProcess.StopRequested"/> with
    /// <see cref="ProcessExitReason.CloseRequested"/>.
    /// </summary>
    /// <param name="timeout">Grace period; <see cref="DefaultCloseTimeout"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when the process closed by itself, <see langword="false"/> when it had to be forced.</returns>
    /// <exception cref="KazoUnauthorizedAccessException">It is another process and this app lacks <c>process.manage</c>.</exception>
    /// <exception cref="InvalidOperationException">The process has already exited.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public Task<bool> CloseMainWindowAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        StopAsync(timeout ?? DefaultCloseTimeout, ProcessExitReason.CloseRequested, cancellationToken);

    /// <inheritdoc />
    public override string ToString() => $"{ProcessName} ({Id.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>Parses the <c>Key:\tvalue</c> lines of a <c>/proc/&lt;pid&gt;/status</c> file.</summary>
    /// <param name="text">File content.</param>
    public static IReadOnlyDictionary<string, string> ParseStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator > 0)
            {
                fields[line[..separator]] = line[(separator + 1)..].Trim();
            }
        }

        return fields;
    }

    /// <summary>Parses <c>/proc/&lt;pid&gt;/io</c> (<c>proc_pid_io(5)</c>); missing fields read as <c>0</c>.</summary>
    /// <param name="text">File content.</param>
    public static KazoProcessIoCounters ParseIo(string text)
    {
        IReadOnlyDictionary<string, string> fields = ParseStatus(text);
        return new KazoProcessIoCounters(
            Long(fields, "rchar"),
            Long(fields, "wchar"),
            Long(fields, "syscr"),
            Long(fields, "syscw"),
            Long(fields, "read_bytes"),
            Long(fields, "write_bytes"),
            Long(fields, "cancelled_write_bytes"));
    }

    /// <summary>
    /// Parses the <c>utime</c> and <c>stime</c> fields (14 and 15, in <c>USER_HZ</c>) of a
    /// <c>/proc/&lt;pid&gt;/stat</c> line (<c>proc(5)</c>). The fields are counted after the last
    /// <c>)</c>, since <c>comm</c> may contain spaces and parentheses.
    /// </summary>
    /// <param name="text">File content.</param>
    /// <returns>The two times; zero for a line too short to hold them.</returns>
    public static (TimeSpan User, TimeSpan System) ParseStatTimes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int close = text.LastIndexOf(')');
        string[] rest = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // rest[0] is field 3 (state), so field n is rest[n - 3].
        return rest.Length < 13
            ? (TimeSpan.Zero, TimeSpan.Zero)
            : (Ticks(rest[11]), Ticks(rest[12]));

        static TimeSpan Ticks(string field) =>
            long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) && ticks > 0
                ? TimeSpan.FromMilliseconds(ticks * (1000d / UserHz))
                : TimeSpan.Zero;
    }

    /// <summary>
    /// Parses the <c>nice</c> field (19) of a <c>/proc/&lt;pid&gt;/stat</c> line (<c>proc(5)</c>,
    /// ADR 0127), counted after the last <c>)</c> like <see cref="ParseStatTimes"/>.
    /// </summary>
    /// <param name="text">File content.</param>
    /// <returns>The niceness, clamped to <c>-20</c>..<c>19</c>; <c>0</c> for a line too short to hold it.</returns>
    public static int ParseStatNice(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int close = text.LastIndexOf(')');
        string[] rest = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // rest[0] is field 3, so field 19 is rest[16].
        return rest.Length > 16 && int.TryParse(rest[16], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int nice)
            ? Math.Clamp(nice, -20, 19)
            : 0;
    }

    /// <summary>Reads status, stat, cmdline, cwd and io; <see langword="false"/> when the process vanished.</summary>
    private async Task<bool> TryLoadAsync(DateTimeOffset startTime, CancellationToken cancellationToken)
    {
        string? statusText = await TryReadAsync("status", cancellationToken).ConfigureAwait(false);
        if (statusText is null)
        {
            return false;
        }

        IReadOnlyDictionary<string, string> status = ParseStatus(statusText);
        string? stat = await TryReadAsync("stat", cancellationToken).ConfigureAwait(false);
        string? cmdline = await TryReadAsync("cmdline", cancellationToken).ConfigureAwait(false);
        string? io = await TryReadAsync("io", cancellationToken).ConfigureAwait(false);
        string? cwd = await TryReadLinkAsync("cwd", cancellationToken).ConfigureAwait(false);

        IAppExecutionContext context = KazoExecutionContext.Current;
        Status = status;
        AppId = Field(status, "AppId");
        ProcessName = Field(status, "Name");
        ParentId = Long(status, "PPid") is > 0 and long parent ? parent : null;
        Kind = Enum.TryParse(Field(status, "Kind"), ignoreCase: true, out AppKind kind) ? kind : AppKind.Window;
        UserId = Field(status, "Uid");
        UserName = string.Equals(UserId, context.UserId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(context.UserLoginName)
            ? context.UserLoginName
            : UserId;
        SessionId = Field(status, "Session");
        (State, StateCode) = ParseState(Field(status, "State"));
        StartTime = startTime;
        WorkingSet64 = ParseKilobytes(Field(status, "VmRSS"));
        PrivateMemorySize64 = status.ContainsKey("RssAnon") ? ParseKilobytes(Field(status, "RssAnon")) : WorkingSet64;
        CpuUsage = double.TryParse(Field(status, "Cpu"), NumberStyles.Float, CultureInfo.InvariantCulture, out double cpu) ? cpu : 0;
        (UserProcessorTime, PrivilegedProcessorTime) = stat is null ? (TimeSpan.Zero, TimeSpan.Zero) : ParseStatTimes(stat);
        Responding = Field(status, "Responding") != "0";
        Nice = stat is null ? 0 : ParseStatNice(stat);
        Arguments = cmdline is null ? [] : cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        CommandLine = string.Join(' ', Arguments.Select(static argument => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument));
        WorkingDirectory = cwd;
        IoCounters = io is null ? null : ParseIo(io);
        HasExited = false;
        return true;
    }

    /// <summary>Reads one file of the process directory; <see langword="null"/> when missing or unreadable.</summary>
    private async Task<string?> TryReadAsync(string entry, CancellationToken cancellationToken)
    {
        try
        {
            return await KazoFile.ReadAllTextAsync($"{ProcessPath()}/{entry}", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KazoIOException or KazoUnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the target of one link of the process directory, like <c>readlink</c>: the target
    /// itself is never resolved. Following <c>cwd</c> would stat the working directory on the
    /// persistent disk for every process at every refresh, and a task manager would become the
    /// main reader of the disk it watches.
    /// </summary>
    private async Task<string?> TryReadLinkAsync(string entry, CancellationToken cancellationToken)
    {
        try
        {
            FileSystemEntrySnapshot? link = await KazoFileSystemCore.TryStatAsync(
                KazoFileSystemCore.Gateway,
                VirtualPath.Parse($"{ProcessPath()}/{entry}"),
                FileSystemLinkBehavior.NoFollow,
                KazoEntryTarget.File,
                cancellationToken).ConfigureAwait(false);
            return (link?.Metadata as SymbolicLinkMetadata)?.Target;
        }
        catch (Exception exception) when (exception is KazoIOException or KazoUnauthorizedAccessException)
        {
            return null;
        }
    }

    private string ProcessPath() => $"{ProcRoot}/{Id.ToString(CultureInfo.InvariantCulture)}";

    private InvalidOperationException Exited(Exception inner)
    {
        HasExited = true;
        return new InvalidOperationException($"Process {Id.ToString(CultureInfo.InvariantCulture)} has exited.", inner);
    }

    /// <summary>Maps the <c>State</c> field (<c>R (running)</c>) to its enum value and letter.</summary>
    private static (KazoProcessState State, char Code) ParseState(string text)
    {
        char code = text.Length > 0 ? text[0] : '?';
        KazoProcessState state = code switch
        {
            'R' => KazoProcessState.Running,
            'S' => KazoProcessState.Sleeping,
            'I' => KazoProcessState.Idle,
            'T' => KazoProcessState.Stopping,
            'Z' => KazoProcessState.Zombie,
            _ => KazoProcessState.Dead
        };
        return (state, code);
    }

    /// <summary>Parses <c>"1234 kB"</c> into bytes.</summary>
    private static long ParseKilobytes(string text)
    {
        string number = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0";
        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long kilobytes) ? kilobytes * 1024 : 0;
    }

    private static long? ParsePid(string name) =>
        name.Length > 0 && name.All(char.IsAsciiDigit) && long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out long pid) && pid > 0
            ? pid
            : null;

    private static string Field(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) ? value : string.Empty;

    private static long Long(IReadOnlyDictionary<string, string> fields, string key) =>
        long.TryParse(Field(fields, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
}

/// <summary>Scheduler state of a <see cref="KazoProcess"/>, from the <c>State</c> field of <c>/proc/&lt;pid&gt;/status</c>.</summary>
public enum KazoProcessState
{
    /// <summary><c>R</c>: running.</summary>
    Running,

    /// <summary><c>S</c>: starting or waiting.</summary>
    Sleeping,

    /// <summary><c>I</c>: created, not yet started.</summary>
    Idle,

    /// <summary><c>T</c>: asked to stop, still finishing.</summary>
    Stopping,

    /// <summary><c>Z</c>: faulted, not yet reaped.</summary>
    Zombie,

    /// <summary><c>X</c>: dead.</summary>
    Dead
}

/// <summary>
/// The counters of <c>/proc/&lt;pid&gt;/io</c> (ADR 0057, section 3), since the process started.
/// </summary>
/// <param name="ReadChars"><c>rchar</c>: bytes read, files and network.</param>
/// <param name="WrittenChars"><c>wchar</c>: bytes written, files and network.</param>
/// <param name="ReadSyscalls"><c>syscr</c>: read calls.</param>
/// <param name="WriteSyscalls"><c>syscw</c>: write calls.</param>
/// <param name="ReadBytes"><c>read_bytes</c>: the part of <paramref name="ReadChars"/> that came from the disk.</param>
/// <param name="WriteBytes"><c>write_bytes</c>: the part of <paramref name="WrittenChars"/> that went to the disk.</param>
/// <param name="CancelledWriteBytes"><c>cancelled_write_bytes</c>; always <c>0</c> today.</param>
public sealed record KazoProcessIoCounters(
    long ReadChars,
    long WrittenChars,
    long ReadSyscalls,
    long WriteSyscalls,
    long ReadBytes,
    long WriteBytes,
    long CancelledWriteBytes);
