using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>
/// Test observer of the context-switch hook (ADR 0067, section 1): records every switch, so a test
/// can prove the hook sees each process enter and leave, including in async continuations.
/// </summary>
internal sealed class RecordingObserver : IExecutionContextSwitchObserver
{
    private readonly Lock _gate = new();
    private readonly List<ExecutionContextSwitch> _switches = [];

    public IReadOnlyList<ExecutionContextSwitch> Switches
    {
        get
        {
            lock (_gate)
            {
                return [.. _switches];
            }
        }
    }

    public void OnContextSwitch(in ExecutionContextSwitch contextSwitch)
    {
        lock (_gate)
        {
            _switches.Add(contextSwitch);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _switches.Clear();
        }
    }

    /// <summary>Gets whether some switch entered <paramref name="pid"/>.</summary>
    public bool Entered(long pid, bool? threadContextChanged = null) =>
        Switches.Any(s => s.To?.Value == pid && (threadContextChanged is null || s.ThreadContextChanged == threadContextChanged));

    /// <summary>Gets whether some switch left <paramref name="pid"/>.</summary>
    public bool Left(long pid, bool? threadContextChanged = null) =>
        Switches.Any(s => s.From?.Value == pid && (threadContextChanged is null || s.ThreadContextChanged == threadContextChanged));
}
