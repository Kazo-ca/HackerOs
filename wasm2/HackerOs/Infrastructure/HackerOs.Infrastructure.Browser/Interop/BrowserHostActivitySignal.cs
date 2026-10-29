using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.JSInterop;

namespace HackerOs.Infrastructure.Browser.Interop;

/// <summary>
/// Browser half of <see cref="IHostActivitySignal"/> (ADR 0185 §15): the page's visibility and its
/// return (shown again or focused), from <c>watchActivity</c> of <c>hostFolder.js</c>. The listeners are
/// installed on first use; until then, and when the module cannot load, the page counts as visible.
/// </summary>
public sealed class BrowserHostActivitySignal : IHostActivitySignal, IAsyncDisposable
{
    private readonly IJSRuntime _runtime;
    private DotNetObjectReference<BrowserHostActivitySignal>? _self;
    private Action? _resumed;
    private int _started;
    private volatile bool _visible = true;

    /// <summary>Initializes the signal.</summary>
    public BrowserHostActivitySignal(IJSRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <inheritdoc />
    public bool IsVisible
    {
        get
        {
            EnsureStarted();
            return _visible;
        }
    }

    /// <inheritdoc />
    public event Action? Resumed
    {
        add
        {
            EnsureStarted();
            _resumed += value;
        }
        remove => _resumed -= value;
    }

    /// <summary>Called by the page when its visibility changes or it gets the focus.</summary>
    /// <param name="visible">Whether the page is visible now.</param>
    /// <param name="resumed">Whether the page came back (shown again, or focused).</param>
    [JSInvokable]
    public void OnActivity(bool visible, bool resumed)
    {
        _visible = visible;
        if (resumed)
        {
            _resumed?.Invoke();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _self?.Dispose();
        _self = null;
        return ValueTask.CompletedTask;
    }

    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = StartAsync();
        }
    }

    private async Task StartAsync()
    {
        try
        {
            IJSObjectReference module = await _runtime.InvokeAsync<IJSObjectReference>("import", BrowserHostFolderAccess.ModulePath).ConfigureAwait(false);
            _self = DotNetObjectReference.Create(this);
            _visible = await module.InvokeAsync<bool>("watchActivity", _self).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException or ObjectDisposedException)
        {
            // No page (prerendering, tests) or no module: the page counts as visible.
        }
    }
}
