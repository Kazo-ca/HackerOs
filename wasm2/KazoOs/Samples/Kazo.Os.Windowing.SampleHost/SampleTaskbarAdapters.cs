using Kazo.Os.Taskbar.Blazor;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Windowing.SampleHost;

/// <summary>Projects the sample host's window runtime into the taskbar's window contract.</summary>
public sealed class SampleWindowSource : ITaskbarWindowSource, IDisposable
{
    private readonly WindowRuntime _runtime;

    /// <summary>Creates a window source over the sample host's own runtime instance.</summary>
    public SampleWindowSource(WindowRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runtime.StateChanged += OnRuntimeStateChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<TaskbarWindowItem> Windows => [.. _runtime.Windows.Select(ToItem)];

    /// <inheritdoc />
    public event Action? Changed;

    private static TaskbarWindowItem ToItem(WindowRuntimeState window) => new(
        window.Id,
        window.Title,
        window.IconAssetPath,
        window.IsFocused,
        window.VisualState == WindowVisualState.Minimized);

    private void OnRuntimeStateChanged() => Changed?.Invoke();

    /// <inheritdoc />
    public void Dispose() => _runtime.StateChanged -= OnRuntimeStateChanged;
}

/// <summary>Dispatches taskbar-initiated window commands directly onto the sample host's runtime.</summary>
public sealed class SampleCommands(WindowRuntime runtime) : ITaskbarCommandDispatcher
{
    /// <inheritdoc />
    public void Activate(WindowId id) => runtime.Apply(new FocusWindowCommand(id));

    /// <inheritdoc />
    public void Minimize(WindowId id) => runtime.Apply(new MinimizeWindowCommand(id));

    /// <inheritdoc />
    public void Restore(WindowId id) => runtime.Apply(new RestoreWindowCommand(id));

    /// <inheritdoc />
    public void Close(WindowId id) => runtime.Apply(new ForceWindowCloseCommand(id));
}

/// <summary>Owns the sample launcher's open/closed state.</summary>
public sealed class SampleLauncher : ITaskbarLauncher
{
    /// <inheritdoc />
    public bool IsOpen { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Toggle() => SetOpen(!IsOpen);

    /// <summary>Closes the launcher.</summary>
    public void Close() => SetOpen(false);

    private void SetOpen(bool value)
    {
        if (IsOpen == value)
        {
            return;
        }

        IsOpen = value;
        Changed?.Invoke();
    }
}

/// <summary>Ticks the taskbar clock once per real second using a plain timer -- no simulated clock here.</summary>
public sealed class SampleStatusSource : ITaskbarStatusSource, IDisposable
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1));
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>Starts the clock tick loop.</summary>
    public SampleStatusSource() => _ = RunAsync();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public event Action? Changed;

    private async Task RunAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cancellation.Token))
            {
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _timer.Dispose();
    }
}
