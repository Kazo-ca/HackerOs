using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>What one OOM kill was for.</summary>
public enum OomKillCause
{
    /// <summary><c>MemAvailable</c> stayed under the threshold of <c>MemTotal</c> (global OOM).</summary>
    SystemMemory,

    /// <summary>A runtime of the page neared the browser's real memory limit (the guard of ADR 0126, section 5).</summary>
    BrowserLimit
}

/// <summary>One kill of the OOM killer.</summary>
/// <param name="Pid">The victim.</param>
/// <param name="Cause">Why.</param>
/// <param name="Score">Its <c>oom_score</c> when chosen.</param>
/// <param name="RuntimeId">The runtime the guard acted for, or <see langword="null"/> for a global OOM.</param>
public sealed record OomKill(ProcessId Pid, OomKillCause Cause, long Score, string? RuntimeId);

/// <summary>
/// The OOM killer (ADR 0126): off by default, enabled in <c>/etc/kazoos/memory.config</c>, and only
/// where memory is measured. Every simulated second it samples <c>MemAvailable</c>; two samples in a
/// row under <c>min-free-percent</c> of <c>MemTotal</c> make it kill the process with the highest
/// <c>oom_score</c>, as Linux's <c>out_of_memory()</c> does, and write the kill to the kernel log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Victim.</b> <c>oom_score = VmRSS × 1000 / MemTotal + oom_score_adj</c>, bounded to <c>[0, 2000]</c>;
/// <c>-1000</c> is never chosen; on a tie, the most recent process loses. The victim gets
/// <c>SIGKILL</c> (<see cref="ProcessExitReason.OutOfMemory"/>, exit 137; in a worker, ADR 0069 step 4
/// if it does not drop it); no other victim is chosen until it has ended, like the kernel's
/// <c>oom_reaper</c> wait.
/// </para>
/// <para>
/// <b>Browser guard (section 5).</b> At each sample, every real runtime of the page (main thread,
/// each Web Worker) is compared with its memory ceiling: at 80 % a warning is written to the kernel log
/// and the user is told, once per runtime; if the OOM killer is active, it also kills the highest
/// <c>oom_score</c> among that runtime's processes, even while the simulated <c>MemAvailable</c> is high.
/// </para>
/// <para>
/// <b>Traces (section 3).</b> The kernel log (<c>dmesg</c>, <c>kern.log</c>, <c>syslog</c>) gets the
/// lines of Linux: <c>oom-kill:constraint=…</c> then <c>Out of memory: Killed process …</c> (or
/// <c>Memory cgroup out of memory: …</c> for the guard); <see cref="KillCount"/> is
/// <c>oom_kill</c> of <c>/proc/vmstat</c>; a window app's user gets a notification.
/// </para>
/// </remarks>
public sealed class OomKiller : IDisposable
{
    /// <summary>Interval between two samples: one simulated second.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>Samples in a row under the threshold before a kill.</summary>
    public const int SamplesBeforeKill = 2;

    /// <summary>Share of a runtime's ceiling at which the browser guard acts.</summary>
    public const double BrowserGuardRatio = 0.8;

    private const int MaximumNameLength = 15;

    private readonly ISimulationClock _clock;
    private readonly Func<IProcessManager> _processManager;
    private readonly SystemMemoryProbe _memory;
    private readonly Func<InMemoryProcessOomScoreTable?> _oomScores;
    private readonly KernelLog _kernelLog;
    private readonly IRuntimeMemorySource? _runtimes;
    private readonly IOomNotifier? _notifier;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _warnedRuntimes = new(StringComparer.Ordinal);
    private OomKillerSettings _settings = OomKillerSettings.Default;
    private bool _notMeasuredLogged;
    private int _lowSamples;
    private ProcessId? _pendingVictim;
    private long _killCount;
    private IDisposable? _scheduled;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a stopped OOM killer with the default settings (off).</summary>
    /// <param name="clock">Simulated clock the samples are scheduled on.</param>
    /// <param name="processManager">Lazily resolves the process table.</param>
    /// <param name="memory">The memory figures of <c>/proc/meminfo</c> and <c>/proc/&lt;pid&gt;/status</c>.</param>
    /// <param name="oomScores">Lazily resolves the <c>oom_score_adj</c> table; every process reads <c>0</c> without it.</param>
    /// <param name="kernelLog">The kernel's ring buffer the kills are written to.</param>
    /// <param name="runtimes">The page's real runtimes for the browser guard; <see langword="null"/> disables the guard.</param>
    /// <param name="notifier">Tells the user; <see langword="null"/> tells nobody.</param>
    /// <param name="interval">Simulated time between two samples; <see cref="DefaultInterval"/> when omitted.</param>
    public OomKiller(
        ISimulationClock clock,
        Func<IProcessManager> processManager,
        SystemMemoryProbe memory,
        Func<InMemoryProcessOomScoreTable?> oomScores,
        KernelLog kernelLog,
        IRuntimeMemorySource? runtimes = null,
        IOomNotifier? notifier = null,
        TimeSpan? interval = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _oomScores = oomScores ?? throw new ArgumentNullException(nameof(oomScores));
        _kernelLog = kernelLog ?? throw new ArgumentNullException(nameof(kernelLog));
        _runtimes = runtimes;
        _notifier = notifier;
        Interval = interval ?? DefaultInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Interval, TimeSpan.Zero, nameof(interval));
    }

    /// <summary>Gets the simulated time between two samples.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Gets the settings in force.</summary>
    public OomKillerSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    /// <summary>Gets whether the OOM killer may kill: enabled, and memory measured (section 1).</summary>
    public bool IsActive => Settings.Enabled && _memory.IsMeasuring;

    /// <summary>Gets the number of kills since boot, <c>oom_kill</c> of <c>/proc/vmstat</c>.</summary>
    public long KillCount => Interlocked.Read(ref _killCount);

    /// <summary>
    /// Applies new settings (read from <c>memory.config</c> at boot and after each change). Writes the
    /// change to the kernel log; enabled where memory is not measured, it says once that it stays idle.
    /// </summary>
    /// <param name="settings">The settings.</param>
    public void Configure(OomKillerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool changed;
        lock (_gate)
        {
            changed = settings != _settings;
            _settings = settings;
            _lowSamples = 0;
        }

        if (settings.Enabled && !_memory.IsMeasuring)
        {
            bool first;
            lock (_gate)
            {
                first = !_notMeasuredLogged;
                _notMeasuredLogged = true;
            }

            if (first)
            {
                _kernelLog.Write(KernelLogLevel.Warning, "oom-killer: disabled, memory is not measured");
            }

            return;
        }

        if (changed)
        {
            _kernelLog.Write(KernelLogLevel.Info, settings.Enabled
                ? string.Create(CultureInfo.InvariantCulture, $"oom-killer: enabled, min-free-percent={settings.MinFreePercent}")
                : "oom-killer: disabled");
        }
    }

    /// <summary>
    /// Schedules the first sample one interval from now. Idempotent. Samples run even while the OOM
    /// killer is off, for the browser guard's warning.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _scheduled = _clock.Schedule(Interval, OnDue);
        }
    }

    /// <summary>
    /// Reads <c>/etc/kazoos/memory.config</c> now, applies it, and follows its changes: an
    /// administrator's <c>sudo nano</c> or Settings &gt; Memory takes effect at once, like a
    /// <c>sysctl -w</c>. A value out of range is logged to <c>syslog</c> and brought back into range.
    /// </summary>
    /// <param name="settings">The OS settings service.</param>
    /// <param name="cancellationToken">Cancels the first read and the watch's subscription.</param>
    public async Task AttachAsync(Settings.IOsSettingsService settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await ApplyAsync(settings, cancellationToken).ConfigureAwait(false);
        settings.DocumentChanged += (_, change) =>
        {
            if (change.Concerns(MemorySettingsDocuments.DocumentId))
            {
                _ = ApplyAsync(settings, CancellationToken.None);
            }
        };
        await settings.WatchAsync(MemorySettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAsync(Settings.IOsSettingsService settings, CancellationToken cancellationToken)
    {
        try
        {
            (OomKillerSettings value, string? warning) = await MemorySettingsDocuments.ReadAsync(settings, cancellationToken).ConfigureAwait(false);
            if (warning is not null)
            {
                _kernelLog.Write(KernelLogLevel.Warning, warning);
            }

            Configure(value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreadable file leaves the settings in force, like a sysctl.conf that fails to load.
            _kernelLog.Write(KernelLogLevel.Warning, $"memory.config: could not be read ({exception.GetType().Name}); settings unchanged");
        }
    }

    /// <summary>Takes one sample now and acts on it. Called by the clock; public for tests.</summary>
    /// <returns>The kill made, if any.</returns>
    public OomKill? CheckNow()
    {
        IProcessManager manager = _processManager();
        lock (_gate)
        {
            if (_pendingVictim is { } pending)
            {
                if (manager.TryGetActive(pending, out ProcessRecord? still) && !still.IsTerminal)
                {
                    // The previous victim has not ended yet: its memory is not back.
                    return null;
                }

                _pendingVictim = null;
            }
        }

        IReadOnlyList<ProcessRecord> active = _memory.ActiveProcesses();
        bool canKill = IsActive;
        OomKill? kill = CheckBrowserGuard(manager, active, canKill);
        if (kill is not null || !canKill)
        {
            return kill;
        }

        SystemMemorySnapshot system = _memory.ReadSystem(active);
        int percent = Settings.MinFreePercent;
        bool low = system.AvailableBytes * 100 < system.TotalBytes * (long)percent;
        lock (_gate)
        {
            _lowSamples = low ? _lowSamples + 1 : 0;
            if (_lowSamples < SamplesBeforeKill)
            {
                return null;
            }

            _lowSamples = 0;
        }

        return KillVictim(manager, active, OomKillCause.SystemMemory, runtime: null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _scheduled?.Dispose();
            _scheduled = null;
        }
    }

    /// <summary>The browser guard: warns once per runtime at 80 % of its ceiling, and kills in it when active.</summary>
    private OomKill? CheckBrowserGuard(IProcessManager manager, IReadOnlyList<ProcessRecord> active, bool canKill)
    {
        if (_runtimes is null)
        {
            return null;
        }

        IReadOnlyList<RuntimeMemoryReading> readings;
        try
        {
            readings = _runtimes.Read();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        foreach (RuntimeMemoryReading runtime in readings)
        {
            if (runtime.Ratio < BrowserGuardRatio)
            {
                continue;
            }

            bool first;
            lock (_gate)
            {
                first = _warnedRuntimes.Add(runtime.Id);
            }

            if (first)
            {
                _kernelLog.Write(KernelLogLevel.Warning, string.Create(CultureInfo.InvariantCulture,
                    $"{runtime.Name}: memory {Kilobytes(runtime.UsedBytes)}kB of {Kilobytes(runtime.CeilingBytes)}kB ({runtime.Ratio:P0}), nearing the browser's limit"));
                _notifier?.ProcessesSafely(notifier => notifier.RuntimeNearLimit(runtime));
            }

            if (!canKill)
            {
                continue;
            }

            HashSet<long> others = runtime.ProcessIds is null
                ? [.. readings.Where(static reading => reading.ProcessIds is not null).SelectMany(static reading => reading.ProcessIds!)]
                : [];
            IReadOnlyList<ProcessRecord> hosted = runtime.ProcessIds is { } ids
                ? [.. active.Where(process => ids.Contains(process.Pid.Value))]
                : [.. active.Where(process => !others.Contains(process.Pid.Value))];
            if (KillVictim(manager, hosted, OomKillCause.BrowserLimit, runtime) is { } kill)
            {
                _runtimes.Collect(runtime.Id);
                return kill;
            }
        }

        return null;
    }

    /// <summary>Chooses and kills the victim among <paramref name="candidates"/>, and writes the kernel's lines.</summary>
    private OomKill? KillVictim(IProcessManager manager, IReadOnlyList<ProcessRecord> candidates, OomKillCause cause, RuntimeMemoryReading? runtime)
    {
        InMemoryProcessOomScoreTable? table = _oomScores();
        IReadOnlyList<ProcessRecord> active = _memory.ActiveProcesses();
        ProcessRecord? victim = null;
        long victimScore = -1;
        int victimAdjust = 0;
        ProcessMemoryReading victimMemory = default;
        foreach (ProcessRecord process in candidates)
        {
            if (process.IsTerminal)
            {
                continue;
            }

            int adjust = table?.GetOrDefault(process.Pid) ?? 0;
            if (adjust <= InMemoryProcessOomScoreTable.Exempt)
            {
                continue;
            }

            ProcessMemoryReading memory = _memory.ReadProcess(process, active);
            long score = _memory.OomScore(memory.ResidentBytes, adjust);
            // The highest score loses; on a tie, the most recent process (the higher PID).
            if (victim is null || score > victimScore || (score == victimScore && process.Pid.Value > victim.Pid.Value))
            {
                victim = process;
                victimScore = score;
                victimAdjust = adjust;
                victimMemory = memory;
            }
        }

        if (victim is null)
        {
            _kernelLog.Write(KernelLogLevel.Error, cause == OomKillCause.SystemMemory
                ? "Out of memory and no killable processes..."
                : string.Create(CultureInfo.InvariantCulture, $"Memory cgroup out of memory: no killable process in {runtime!.Name}"));
            return null;
        }

        try
        {
            // Before the signal: a worker victim may be gone before Kill returns, and its worker must
            // then be replaced at once to give its WebAssembly memory back (ADR 0142).
            ReleaseRuntime(victim.Pid.Value);
            manager.Kill(victim.Pid, ProcessExitReason.OutOfMemory);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            // The process ended between the choice and the signal: nothing to kill.
            return null;
        }

        lock (_gate)
        {
            _pendingVictim = victim.Pid;
        }

        Interlocked.Increment(ref _killCount);
        WriteKillLines(victim, victimMemory, victimAdjust, cause, runtime);
        if (victim.Kind == AppKind.Window)
        {
            _notifier?.ProcessesSafely(notifier => notifier.ProcessKilled(victim));
        }

        return new OomKill(victim.Pid, cause, victimScore, runtime?.Id);
    }

    private void ReleaseRuntime(long pid)
    {
        try
        {
            _runtimes?.ReleaseProcess(pid);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The pool is shutting down: nothing to give back.
        }
    }

    /// <summary>The two lines Linux prints for an OOM kill (<c>oom_kill_process</c>).</summary>
    private void WriteKillLines(ProcessRecord victim, ProcessMemoryReading memory, int adjust, OomKillCause cause, RuntimeMemoryReading? runtime)
    {
        string comm = Comm(victim.AppId);
        string uid = victim.UserId.ToString();
        string constraint = cause == OomKillCause.SystemMemory
            ? "constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/"
            : $"constraint=CONSTRAINT_MEMCG,nodemask=(null),cpuset=/,mems_allowed=0,oom_memcg=/kazoos/{runtime!.Id},task_memcg=/kazoos/{runtime.Id}";
        _kernelLog.Write(KernelLogLevel.Info, string.Create(CultureInfo.InvariantCulture,
            $"oom-kill:{constraint},task={comm},pid={victim.Pid.Value},uid={uid}"));

        long rssKb = Kilobytes(memory.ResidentBytes);
        long anonKb = memory.Layers is { } layers ? Kilobytes(layers.AnonymousBytes + layers.KernelBytes) : rssKb;
        long fileKb = memory.Layers is { } file ? Kilobytes(file.FileBytes) : 0;
        string prefix = cause == OomKillCause.SystemMemory ? "Out of memory" : "Memory cgroup out of memory";
        _kernelLog.Write(KernelLogLevel.Error, string.Create(CultureInfo.InvariantCulture,
            $"{prefix}: Killed process {victim.Pid.Value} ({comm}) total-vm:{rssKb}kB, anon-rss:{anonKb}kB, file-rss:{fileKb}kB, shmem-rss:0kB, UID:{uid} oom_score_adj:{adjust}"));
    }

    private static long Kilobytes(long bytes) => (long)Math.Round(Math.Max(0, bytes) / 1024d, MidpointRounding.AwayFromZero);

    /// <summary>The last dotted segment of an app id, cut to 15 characters like Linux's <c>comm</c>.</summary>
    private static string Comm(string appId)
    {
        string name = appId[(appId.LastIndexOf('.') + 1)..];
        if (name.Length == 0)
        {
            name = appId;
        }

        return name.Length > MaximumNameLength ? name[..MaximumNameLength] : name;
    }

    private void OnDue()
    {
        lock (_gate)
        {
            if (_disposed || _scheduled is null)
            {
                return;
            }
        }

        try
        {
            CheckNow();
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            // A process table being torn down (logout, shutdown): the next sample starts afresh.
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _scheduled = _clock.Schedule(Interval, OnDue);
                }
            }
        }
    }
}

/// <summary>Calls a notifier without letting its failure stop the OOM killer.</summary>
internal static class OomNotifierExtensions
{
    /// <summary>Runs <paramref name="action"/>; a failing notification is dropped, the kill stands.</summary>
    public static void ProcessesSafely(this IOomNotifier notifier, Action<IOomNotifier> action)
    {
        try
        {
            action(notifier);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // The queue is gone (logout) or rejected the text: nothing else to do.
        }
    }
}
