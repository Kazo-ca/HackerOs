using System.Text.Json.Serialization;
using Kazo.Os.Platform.Core.Appearance;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// Browser-backed <see cref="IHostAccessibilityProbe"/> (ADR 0105): the JS module
/// (<c>wwwroot/hostAccessibilityProbe.js</c>) reports <c>prefers-contrast</c>, <c>forced-colors</c>
/// and <c>prefers-reduced-motion</c>; the decision of what <c>auto</c> means is made in C#.
/// </summary>
public sealed class BrowserHostAccessibilityProbe : IHostAccessibilityProbe, IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Os.Platform.Blazor/hostAccessibilityProbe.js";

    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;
    private IJSObjectReference? _subscription;
    private DotNetObjectReference<BrowserHostAccessibilityProbe>? _dotNetHelper;

    /// <summary>Creates the probe over the given JS runtime.</summary>
    /// <param name="jsRuntime">The browser's JS runtime.</param>
    public BrowserHostAccessibilityProbe(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
    }

    /// <inheritdoc />
    public HostAccessibilitySignals Current { get; private set; } = HostAccessibilitySignals.None;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_module is not null)
        {
            // Every boot after a sign-out re-initializes the shell's services: one watch is enough.
            return;
        }

        try
        {
            _module = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
            Current = (await _module.InvokeAsync<RawSignals>("readSignals", cancellationToken)).ToSignals();
            _dotNetHelper = DotNetObjectReference.Create(this);
            _subscription = await _module.InvokeAsync<IJSObjectReference>("observeSignals", cancellationToken, _dotNetHelper);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // A host without a browser (component tests, prerendering) asks for nothing.
            Current = HostAccessibilitySignals.None;
        }
    }

    /// <summary>Invoked from JS when the host changed one of its accessibility settings.</summary>
    /// <param name="signals">The new media-query results.</param>
    [JSInvokable]
    public void OnHostAccessibilityChanged(RawSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        HostAccessibilitySignals next = signals.ToSignals();
        if (next == Current)
        {
            return;
        }

        Current = next;
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_subscription is not null)
            {
                await _subscription.InvokeVoidAsync("dispose");
                await _subscription.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException)
        {
            // The page is going away: nothing left to unsubscribe from.
        }

        _dotNetHelper?.Dispose();
    }

    /// <summary>JSON shape of the JS module's signals.</summary>
    /// <param name="PrefersMoreContrast"><c>prefers-contrast: more</c>.</param>
    /// <param name="ForcedColors"><c>forced-colors: active</c>.</param>
    /// <param name="PrefersReducedMotion"><c>prefers-reduced-motion: reduce</c>.</param>
    public sealed record RawSignals(
        [property: JsonPropertyName("prefersMoreContrast")] bool PrefersMoreContrast,
        [property: JsonPropertyName("forcedColors")] bool ForcedColors,
        [property: JsonPropertyName("prefersReducedMotion")] bool PrefersReducedMotion)
    {
        internal HostAccessibilitySignals ToSignals() => new(PrefersMoreContrast, ForcedColors, PrefersReducedMotion);
    }
}
