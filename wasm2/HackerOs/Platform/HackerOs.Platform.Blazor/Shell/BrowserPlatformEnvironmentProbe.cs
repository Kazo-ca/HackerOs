using System.Text.Json.Serialization;
using HackerOs.Platform.Core.Shell;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// Browser-backed <see cref="IPlatformEnvironmentProbe"/>. The JS module
/// (<c>wwwroot/js/platformEnvironmentProbe.js</c>) only reports raw signals per
/// doc/wasm/dev/adr/0015-browser-storage-and-indexeddb-adapter.md's JS-isolation precedent — the
/// suggested-platform decision is made in <see cref="PlatformEnvironmentPolicy"/>, in C#.
/// </summary>
public sealed class BrowserPlatformEnvironmentProbe : IPlatformEnvironmentProbe, IAsyncDisposable
{
    private const string ModulePath = "./_content/HackerOs.Platform.Blazor/platformEnvironmentProbe.js";

    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserPlatformEnvironmentProbe>? _dotNetHelper;

    /// <summary>Creates the probe over the given JS runtime.</summary>
    public BrowserPlatformEnvironmentProbe(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
    }

    /// <inheritdoc />
    public PlatformEnvironmentSnapshot Current { get; private set; } = PlatformEnvironmentPolicy.Decide(
        new PlatformEnvironmentSignals(1280, 800, PointerIsCoarse: false, HasHover: true, MaxTouchPoints: 0, IsStandalone: false));

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", cancellationToken, ModulePath);

            RawSignals initial = await _module.InvokeAsync<RawSignals>(
                "readCurrentSignals", cancellationToken);
            Current = PlatformEnvironmentPolicy.Decide(initial.ToSignals());

            _dotNetHelper = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("observeEnvironmentChanges", cancellationToken, _dotNetHelper);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException)
        {
            // Headless/non-browser hosts (component tests, prerendering) have no JS runtime that
            // implements matchMedia/ResizeObserver — keep the conservative Desktop default set above.
            Console.Error.WriteLine($"[PlatformEnvironmentProbe] Failed to initialize: {exception.Message}");
        }
    }

    /// <summary>Invoked from JS whenever a watched signal (resize, pointer, hover, standalone) changes.</summary>
    [JSInvokable]
    public void OnEnvironmentChanged(RawSignals signals)
    {
        PlatformEnvironmentSnapshot next = PlatformEnvironmentPolicy.Decide(signals.ToSignals());
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
        _dotNetHelper?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    /// <summary>JSON-serializable shape matching the JS module's raw signal payload.</summary>
    public sealed record RawSignals(
        [property: JsonPropertyName("logicalWidth")] int LogicalWidth,
        [property: JsonPropertyName("logicalHeight")] int LogicalHeight,
        [property: JsonPropertyName("pointerIsCoarse")] bool PointerIsCoarse,
        [property: JsonPropertyName("hasHover")] bool HasHover,
        [property: JsonPropertyName("maxTouchPoints")] int MaxTouchPoints,
        [property: JsonPropertyName("isStandalone")] bool IsStandalone)
    {
        internal PlatformEnvironmentSignals ToSignals() => new(
            LogicalWidth, LogicalHeight, PointerIsCoarse, HasHover, MaxTouchPoints, IsStandalone);
    }
}
