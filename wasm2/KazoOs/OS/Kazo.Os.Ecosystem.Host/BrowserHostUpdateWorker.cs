using Kazo.Os.Platform.Blazor.LazyLoading;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The page's service worker for <see cref="HostUpdateInstaller"/> (ADR 0223), through the host's
/// <c>hostUpdate.js</c> module: <c>registration.update()</c>, <c>SKIP_WAITING</c>, and the
/// <c>controllerchange</c> raised by another tab.
/// </summary>
internal sealed class BrowserHostUpdateWorker(IJSRuntime runtime) : IHostUpdateWorker, IDisposable
{
    private const string ModulePath = "./_content/Kazo.Os.Ecosystem.Host/hostUpdate.js";

    private Lazy<Task<IJSObjectReference>>? _module;
    private DotNetObjectReference<Callbacks>? _callbacks;

    /// <inheritdoc />
    public async ValueTask<HostUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken)
    {
        IJSObjectReference module = await GetModuleAsync().ConfigureAwait(false);
        // The download fills a whole release cache: no JS interop timeout, the module has its own.
        string result = await module.InvokeAsync<string>("download", cancellationToken).ConfigureAwait(false);
        return result switch
        {
            "no-worker" => HostUpdateDownloadResult.NoWorker,
            "up-to-date" => HostUpdateDownloadResult.UpToDate,
            "ready" => HostUpdateDownloadResult.Ready,
            _ => HostUpdateDownloadResult.Unavailable,
        };
    }

    /// <inheritdoc />
    public async ValueTask<bool> ActivateAsync(CancellationToken cancellationToken)
    {
        IJSObjectReference module = await GetModuleAsync().ConfigureAwait(false);
        return await module.InvokeAsync<bool>("activate", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ListenAsync(Action activatedElsewhere, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activatedElsewhere);
        IJSObjectReference module = await GetModuleAsync().ConfigureAwait(false);
        _callbacks?.Dispose();
        _callbacks = DotNetObjectReference.Create(new Callbacks(activatedElsewhere));
        await module.InvokeVoidAsync("listen", cancellationToken, _callbacks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _callbacks?.Dispose();

    private Task<IJSObjectReference> GetModuleAsync()
    {
        _module ??= new Lazy<Task<IJSObjectReference>>(() =>
            runtime.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask());
        return _module.Value;
    }

    /// <summary>The methods <c>hostUpdate.js</c> calls back.</summary>
    internal sealed class Callbacks(Action activatedElsewhere)
    {
        /// <summary>Another tab activated the new worker: this page restarts.</summary>
        [JSInvokable]
        public void OnActivatedElsewhere() => activatedElsewhere();
    }
}
