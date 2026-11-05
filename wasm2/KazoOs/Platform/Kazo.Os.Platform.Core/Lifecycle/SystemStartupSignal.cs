namespace Kazo.Os.Platform.Core.Lifecycle;

/// <summary>
/// "The system is started" (ADR 0071, section 2): the session is open, the desktop is shown and the
/// startup apps are launched. Work that must never slow the boot down (warming up Web Workers)
/// waits for it. The host raises it once per login, through <c>EcosystemBootCoordinator</c>.
/// </summary>
public sealed class SystemStartupSignal
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the task that completes the first time the system is started.</summary>
    public Task Started => _started.Task;

    /// <summary>Gets whether the system has been started.</summary>
    public bool IsStarted => _started.Task.IsCompleted;

    /// <summary>Marks the system started. Idempotent.</summary>
    public void MarkStarted() => _started.TrySetResult();
}
