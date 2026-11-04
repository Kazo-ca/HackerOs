using HackerOs.Simulation.Abstractions.Clipboard;
using Microsoft.JSInterop;

namespace HackerOs.Infrastructure.Browser.Interop;

/// <summary>
/// Browser half of the host clipboard bridge (ADR 0065): imports <c>hostClipboard.js</c> of this
/// library, whose page listeners report host pastes and native copies, and which writes Kazo OS text
/// to the real clipboard with <c>navigator.clipboard.writeText</c>.
/// </summary>
/// <remarks>
/// The module is an asset of this Razor class library (<c>_content/HackerOs.Infrastructure.Browser/hostClipboard.js</c>),
/// never referenced by a host page. The JavaScript callbacks run on the renderer's dispatcher and are
/// forwarded to the <see cref="IHostClipboardSink"/> as they come.
/// </remarks>
public sealed class BrowserHostClipboardChannel : IHostClipboardChannel
{
    internal const string ModulePath = "./_content/HackerOs.Infrastructure.Browser/hostClipboard.js";

    private readonly IJSRuntime _runtime;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;
    private DotNetObjectReference<Callbacks>? _callbacks;
    private bool _disposed;

    /// <summary>Creates the channel over the page's JavaScript runtime.</summary>
    /// <param name="runtime">Runtime the module is imported with.</param>
    public BrowserHostClipboardChannel(IJSRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc />
    public async ValueTask AttachAsync(IHostClipboardSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        _callbacks?.Dispose();
        _callbacks = DotNetObjectReference.Create(new Callbacks(sink));
        await module.InvokeVoidAsync("attach", cancellationToken, _callbacks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SetDirectionsAsync(bool guestToHost, bool hostToGuest, CancellationToken cancellationToken = default)
    {
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        await module.InvokeVoidAsync("setDirections", cancellationToken, guestToHost, hostToGuest).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SyncGuestTextAsync(string? text, bool writeToHost, CancellationToken cancellationToken = default)
    {
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        await module.InvokeVoidAsync("syncGuest", cancellationToken, text, writeToHost).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_module is not null)
        {
            try
            {
                if (_callbacks is not null)
                {
                    await _module.InvokeVoidAsync("detach", _callbacks).ConfigureAwait(false);
                }

                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The page is gone (server circuit closed): nothing left to detach.
            }
        }

        _callbacks?.Dispose();
        _moduleGate.Dispose();
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _module ??= await _runtime
                .InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath)
                .ConfigureAwait(false);
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    /// <summary>Target of the module's <c>invokeMethodAsync</c> calls.</summary>
    /// <param name="sink">Receives what the page reports.</param>
    internal sealed class Callbacks(IHostClipboardSink sink)
    {
        /// <summary>A paste brought new host content.</summary>
        /// <param name="text">Pasted text, or <see langword="null"/>.</param>
        /// <param name="fileNames">Names of the pasted host files.</param>
        [JSInvokable]
        public void OnHostPaste(string? text, string[]? fileNames) => sink.OnHostPaste(text, fileNames ?? []);

        /// <summary>A native copy or cut inside Kazo OS.</summary>
        /// <param name="text">Copied text.</param>
        [JSInvokable]
        public void OnNativeCopy(string text) => sink.OnNativeCopy(text);
    }
}
