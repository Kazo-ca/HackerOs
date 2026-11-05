using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>Coordinates optional confirmation, process cancellation, and final window removal.</summary>
public sealed class WindowCloseCoordinator
{
    private readonly WindowRuntime _runtime;
    private readonly AppLifecycleOrchestrator _lifecycle;

    /// <summary>Creates a close coordinator for one window runtime.</summary>
    public WindowCloseCoordinator(WindowRuntime runtime, AppLifecycleOrchestrator lifecycle)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    /// <summary>Requests close and removes the window only after confirmation and process stop.</summary>
    /// <param name="windowId">Window requested by the user.</param>
    /// <param name="confirmAsync">Optional unsaved-change confirmation callback.</param>
    /// <returns>Ordered window events, or an empty list when confirmation rejects close.</returns>
    /// <param name="cancellationToken">Cancels the confirmation; once confirmed, cancellation is checked before the process is stopped.</param>
    public async Task<IReadOnlyList<WindowEvent>> CloseAsync(
        WindowId windowId,
        Func<CancellationToken, ValueTask<bool>>? confirmAsync = null,
        CancellationToken cancellationToken = default)
    {
        WindowRuntimeState? window = _runtime.Windows.FirstOrDefault(item => item.Id == windowId);
        if (window is null)
        {
            return [];
        }

        List<WindowEvent> events = [.. _runtime.Apply(new RequestWindowCloseCommand(windowId))];

        if (confirmAsync is not null && !await confirmAsync(cancellationToken))
        {
            _runtime.Apply(new CancelWindowCloseCommand(windowId));
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycle.StopAsync(AppInstanceId.FromGuid(window.OwnerInstanceId.Value), ProcessExitReason.CloseRequested);
        events.AddRange(_runtime.Apply(new ForceWindowCloseCommand(windowId)));
        return events;
    }
}
