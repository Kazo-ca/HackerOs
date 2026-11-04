using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// One background job of a shell (<c>command &amp;</c>, ADR 0064): the and-or list runs in a
/// subshell copy while the shell reads its next line, its processes are remembered for
/// <c>jobs -l</c>, <c>$!</c> and <c>kill %n</c>.
/// </summary>
public sealed class ShellJob
{
    private readonly object _sync = new();
    private readonly List<ProcessId> _processes = [];

    internal ShellJob(int id, string command, CancellationTokenSource cancellation)
    {
        Id = id;
        Command = command;
        Cancellation = cancellation;
    }

    /// <summary>Gets the job number.</summary>
    public int Id { get; }

    /// <summary>Gets the command line as typed.</summary>
    public string Command { get; }

    /// <summary>Gets the job's state.</summary>
    public ShellJobState State { get; private set; } = ShellJobState.Running;

    /// <summary>Gets the exit status once the job ended.</summary>
    public int? ExitStatus { get; private set; }

    /// <summary>Gets whether the end of the job was already reported (<c>[1]+ Done</c>).</summary>
    internal bool Reported { get; set; }

    /// <summary>Gets the task running the job; its result is the job's exit status.</summary>
    internal Task<int> Completion { get; set; } = Task.FromResult(0);

    /// <summary>Cancels the job's commands (<c>kill %n</c>, the shell exiting).</summary>
    internal CancellationTokenSource Cancellation { get; }

    /// <summary>Gets the processes the job started, oldest first.</summary>
    public IReadOnlyList<ProcessId> Processes
    {
        get
        {
            lock (_sync)
            {
                return [.. _processes];
            }
        }
    }

    internal void AddProcess(ProcessId pid)
    {
        lock (_sync)
        {
            _processes.Add(pid);
        }
    }

    internal void Finish(int exitStatus, bool terminated)
    {
        lock (_sync)
        {
            if (State != ShellJobState.Running)
            {
                return;
            }

            ExitStatus = exitStatus;
            State = terminated ? ShellJobState.Terminated : ShellJobState.Done;
        }
    }

    /// <summary>The job as the SDK shows it.</summary>
    internal ShellJobInfo Describe(bool isCurrent) =>
        new(Id, Command, State, ExitStatus, [.. Processes.Select(pid => checked((int)pid.Value))], isCurrent);
}

/// <summary>The jobs of one shell, shared by the shell and its subshell copies (bash's job table).</summary>
public sealed class ShellJobTable
{
    private readonly object _sync = new();
    private readonly List<ShellJob> _jobs = [];

    /// <summary>Gets the jobs not yet reported as ended, oldest first.</summary>
    public IReadOnlyList<ShellJob> Jobs
    {
        get
        {
            lock (_sync)
            {
                return [.. _jobs];
            }
        }
    }

    /// <summary>Gets the current job (<c>%%</c>, <c>%+</c>): the most recent one.</summary>
    public ShellJob? Current
    {
        get
        {
            lock (_sync)
            {
                return _jobs.Count == 0 ? null : _jobs[^1];
            }
        }
    }

    /// <summary>Adds a job with the next free number (numbers restart at 1 when the table empties, as in bash).</summary>
    internal ShellJob Add(string command, CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            int id = _jobs.Count == 0 ? 1 : _jobs.Max(job => job.Id) + 1;
            ShellJob job = new(id, command, cancellation);
            _jobs.Add(job);
            return job;
        }
    }

    /// <summary>Finds a job by a job spec: <c>%n</c>, <c>%%</c>, <c>%+</c>, <c>%-</c>, <c>%string</c> (command prefix), or a bare number.</summary>
    public ShellJob? Find(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_sync)
        {
            string body = spec.StartsWith('%') ? spec[1..] : spec;
            if (body is "" or "%" or "+")
            {
                return _jobs.Count == 0 ? null : _jobs[^1];
            }

            if (body == "-")
            {
                return _jobs.Count < 2 ? null : _jobs[^2];
            }

            if (int.TryParse(body, out int id))
            {
                return _jobs.FirstOrDefault(job => job.Id == id);
            }

            return _jobs.LastOrDefault(job => job.Command.StartsWith(body, StringComparison.Ordinal));
        }
    }

    /// <summary>Removes the ended jobs that were reported, so their numbers can be reused.</summary>
    internal void RemoveReported()
    {
        lock (_sync)
        {
            _jobs.RemoveAll(job => job.Reported && job.State != ShellJobState.Running);
        }
    }

    /// <summary>Gets the jobs as the SDK shows them.</summary>
    internal IReadOnlyList<ShellJobInfo> Describe()
    {
        lock (_sync)
        {
            return [.. _jobs.Select((job, index) => job.Describe(index == _jobs.Count - 1))];
        }
    }
}
