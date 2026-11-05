using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>Shared desktop/mobile bridge for apps launched indirectly through SDK intents or file handlers.</summary>
internal sealed class WindowProcessObserver : IDisposable
{
    private readonly IDisposable _subscription;
    private bool _disposed;

    internal WindowProcessObserver(IEventBus events, IProcessManager processes, AppLifecycleOrchestrator apps,
        WindowRuntime windows, WindowLaunchCoordinator launcher, Func<Func<Task>, Task> dispatch)
    {
        _subscription = events.Subscribe<ProcessStateChangedEvent>(change => _ = dispatch(async () =>
        {
            if (_disposed) return;
            if (change.NewState is ProcessState.Stopped or ProcessState.Faulted)
            {
                ProcessRecord? process = processes.TryGetActive(change.Pid, out ProcessRecord active)
                    ? active : processes.GetHistory().LastOrDefault(record => record.Pid == change.Pid);
                if (process is null) return;
                WindowOwnerId owner = WindowOwnerId.FromGuid(process.AppInstanceId.Value);
                foreach (WindowRuntimeState window in windows.Windows.Where(window => window.OwnerInstanceId == owner).ToArray())
                    windows.Apply(new ForceWindowCloseCommand(window.Id));
            }
            else if (change.NewState == ProcessState.Running
                && apps.TryGetRunningInstance(change.Pid, out AppDescriptor? descriptor, out _)
                && descriptor.Manifest.Kind == AppKind.Window
                && processes.TryGetActive(change.Pid, out ProcessRecord running))
            {
                await launcher.PresentAsync(descriptor.Manifest, running);
            }
        }));
    }

    /// <summary>Stops observing before the owning shell leaves the render tree.</summary>
    public void Dispose() { _disposed = true; _subscription.Dispose(); }
}
