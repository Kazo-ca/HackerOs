using Kazo.Os.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Drives <see cref="WindowResponsivenessMonitor"/> from the shell (ADR 0127 §2): samples it about
/// once a second and forwards the input latencies measured by
/// <c>WindowResponsivenessHost.razor.js</c>, the only part of the detection that needs the browser
/// (the moment an input happened, <c>event.timeStamp</c>, is not visible from C#).
/// </summary>
/// <remarks>
/// The sample loop runs on the renderer's context, so the monitor and <c>WindowRuntime</c> are only
/// ever touched from it. While an app holds the main thread the loop does not run either: the next
/// tick, right after the thread is given back, is what notices the frozen app (ADR 0067).
/// </remarks>
public sealed partial class WindowResponsivenessHost : IAsyncDisposable
{
    /// <summary>Interval between two samples of the monitor.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private const string ModulePath = "./_content/Kazo.Os.Platform.Blazor/Windows/WindowResponsivenessHost.razor.js";

    private readonly CancellationTokenSource _stop = new();
    private IJSObjectReference? _module;
    private DotNetObjectReference<WindowResponsivenessHost>? _self;
    private string[] _watched = [];

    /// <summary>Gets or sets the monitor this host drives.</summary>
    [Inject]
    public WindowResponsivenessMonitor Monitor { get; set; } = default!;

    /// <summary>Gets or sets the JavaScript runtime, for the input probe.</summary>
    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>
    /// Receives one input's wait from the probe: pointer or key events inside a window's content,
    /// reported when they waited at least <see cref="WindowResponsivenessMonitor.LongInputDelay"/>
    /// or when their window is currently marked as not responding.
    /// </summary>
    /// <param name="windowId">The <c>data-window-id</c> of the window the input targeted.</param>
    /// <param name="latencyMilliseconds"><c>performance.now() - event.timeStamp</c> at dispatch.</param>
    [JSInvokable]
    public Task ReportInputAsync(string windowId, double latencyMilliseconds)
    {
        if (Guid.TryParse(windowId, out Guid id) && double.IsFinite(latencyMilliseconds) && latencyMilliseconds >= 0)
        {
            Monitor.ReportInput(WindowId.FromGuid(id), TimeSpan.FromMilliseconds(latencyMilliseconds));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Monitor.Changed += OnMonitorChanged;
        _ = Monitor.StartAsync(_stop.Token);
        _ = SampleLoopAsync(_stop.Token);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            _self = DotNetObjectReference.Create(this);
            _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await _module.InvokeVoidAsync("attachInputProbe", _self, WindowResponsivenessMonitor.LongInputDelay.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // Prerendering or a host without the module: only cases 2 and 3 of ADR 0127 §2 are lost.
            Console.Error.WriteLine($"[WindowResponsivenessHost] Input probe unavailable: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Monitor.Changed -= OnMonitorChanged;
        await _stop.CancelAsync();
        _stop.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detachInputProbe");
                await _module.DisposeAsync();
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException)
            {
                // The page is going away.
            }
        }

        _self?.Dispose();
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await InvokeAsync(Monitor.Evaluate).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The shell went away.
        }
    }

    private void OnMonitorChanged()
    {
        // The probe also reports fast inputs of the marked windows: those end the state (ADR 0127 §2).
        string[] watched = [.. Monitor.UnresponsiveWindows.Select(id => id.Value.ToString())];
        if (_module is null || watched.SequenceEqual(_watched, StringComparer.Ordinal))
        {
            return;
        }

        _watched = watched;
        _ = InvokeAsync(async () =>
        {
            try
            {
                await _module.InvokeVoidAsync("setWatchedWindows", (object)watched);
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException)
            {
                // The page is going away.
            }
        });
    }
}
