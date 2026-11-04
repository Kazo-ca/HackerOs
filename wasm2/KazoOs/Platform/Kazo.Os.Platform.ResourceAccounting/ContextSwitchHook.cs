using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting;

/// <summary>
/// Installs the context-switch hook shared by the CPU and memory measurements (ADR 0067, section 1)
/// on <see cref="HackExecutionContext"/>'s ambient frame.
/// </summary>
/// <remarks>
/// <para>
/// Once installed, the frame is an <see cref="AsyncLocal{T}"/> built with a value-changed callback.
/// The runtime calls it each time the running process changes on the thread: when the OS opens or
/// closes a scope, and when it restores an <see cref="ExecutionContext"/> at the start or end of a
/// continuation, a timer or an event callback. That is a kernel's context switch, without replacing
/// any <c>TaskScheduler</c> or loop.
/// </para>
/// <para>
/// The hook is process-wide and installed at most once, at service composition, before any app code
/// runs, and only when at least one measurement is active (ADR 0066, section 3).
/// </para>
/// </remarks>
public static class ContextSwitchHook
{
    /// <summary>Gets whether the hook is installed.</summary>
    public static bool IsInstalled => HackExecutionContext.IsContextSwitchHookInstalled;

    /// <summary>Installs the hook with its single observer (normally a <see cref="ContextSwitchDispatcher"/>).</summary>
    /// <param name="observer">The observer receiving every context switch.</param>
    /// <returns>An installation whose disposal removes the hook (tests; the OS keeps it for its lifetime).</returns>
    /// <exception cref="InvalidOperationException">A hook is already installed, or an execution scope is open.</exception>
    public static IDisposable Install(IExecutionContextSwitchObserver observer)
    {
        HackExecutionContext.InstallContextSwitchHook(observer);
        return new Installation();
    }

    private sealed class Installation : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                HackExecutionContext.UninstallContextSwitchHook();
            }
        }
    }
}
