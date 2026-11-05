using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// In-memory <see cref="IProcessEnvironmentTable"/> (ADR 0050, section 5). Entries are created and
/// dropped only by <see cref="InMemoryProcessManager"/>, at process creation and termination.
/// </summary>
/// <remarks>
/// Each entry owns private copies of its argument list and environment: a child created from a
/// parent gets a copy taken at that instant, so later changes on either side never leak to the
/// other (like the address-space copy of <c>fork</c>).
/// </remarks>
public sealed class InMemoryProcessEnvironmentTable : IProcessEnvironmentTable
{
    private static readonly VirtualPath Root = VirtualPath.Parse("/");

    private readonly Lock _gate = new();
    private readonly Dictionary<ProcessId, Entry> _entries = [];

    /// <inheritdoc />
    public bool TryGet(ProcessId pid, out ProcessEnvironmentSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(pid, out Entry? entry))
            {
                snapshot = entry.ToSnapshot();
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    /// <inheritdoc />
    public ProcessEnvironmentSnapshot Get(ProcessId pid) => TryGet(pid, out ProcessEnvironmentSnapshot snapshot)
        ? snapshot
        : throw new KeyNotFoundException($"No active process with PID '{pid}'.");

    /// <inheritdoc />
    public void SetWorkingDirectory(ProcessId pid, VirtualPath directory)
    {
        lock (_gate)
        {
            GetEntry(pid).WorkingDirectory = directory;
        }
    }

    /// <inheritdoc />
    public void SetVariable(ProcessId pid, string name, string? value)
    {
        ProcessEnvironmentRules.ValidateName(name);
        if (value is not null)
        {
            ProcessEnvironmentRules.ValidateValue(value);
        }

        lock (_gate)
        {
            Entry entry = GetEntry(pid);
            if (value is null)
            {
                entry.Variables.Remove(name);
            }
            else
            {
                entry.Variables[name] = value;
            }
        }
    }

    /// <summary>
    /// Creates the entry of a new process: copies the parent's directory and environment when a
    /// live parent is given, then applies the request's values over them.
    /// </summary>
    /// <param name="request">The start request of the new process.</param>
    /// <param name="pid">The PID just assigned to it.</param>
    /// <exception cref="ArgumentException">A requested variable is invalid.</exception>
    internal void Create(ProcessStartRequest request, ProcessId pid)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        if (request.Environment is { } requested)
        {
            foreach ((string name, string value) in requested)
            {
                ProcessEnvironmentRules.ValidateName(name, nameof(request));
                ProcessEnvironmentRules.ValidateValue(value, nameof(request));
            }
        }

        string[] arguments = request.Arguments is { Count: > 0 } argv ? [.. argv] : [request.AppId];

        lock (_gate)
        {
            VirtualPath workingDirectory = Root;
            if (request.ParentPid is { } parentPid && _entries.TryGetValue(parentPid, out Entry? parent))
            {
                workingDirectory = parent.WorkingDirectory;
                foreach ((string name, string value) in parent.Variables)
                {
                    variables[name] = value;
                }
            }

            if (request.Environment is { } overrides)
            {
                foreach ((string name, string value) in overrides)
                {
                    variables[name] = value;
                }
            }

            _entries[pid] = new Entry(arguments, request.WorkingDirectory ?? workingDirectory, variables);
        }
    }

    /// <summary>Drops the entry of a process that reached a terminal state.</summary>
    internal void Remove(ProcessId pid)
    {
        lock (_gate)
        {
            _entries.Remove(pid);
        }
    }

    private Entry GetEntry(ProcessId pid) => _entries.TryGetValue(pid, out Entry? entry)
        ? entry
        : throw new KeyNotFoundException($"No active process with PID '{pid}'.");

    private sealed class Entry(string[] arguments, VirtualPath workingDirectory, Dictionary<string, string> variables)
    {
        public string[] Arguments { get; } = arguments;

        public VirtualPath WorkingDirectory { get; set; } = workingDirectory;

        public Dictionary<string, string> Variables { get; } = variables;

        public ProcessEnvironmentSnapshot ToSnapshot() => new(
            Array.AsReadOnly(Arguments),
            WorkingDirectory.Value,
            new Dictionary<string, string>(Variables, StringComparer.Ordinal));
    }
}
