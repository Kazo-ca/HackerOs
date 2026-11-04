using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Windowing.Abstractions;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Browser-backed <see cref="IWindowClientAreaProbe"/>: reads the laid-out content box of a window
/// through <c>windowClientArea.js</c>. Without a JS runtime (prerendering, tests) it answers
/// <see langword="null"/> and the dispatcher falls back to its estimate.
/// </summary>
public sealed class BrowserWindowClientAreaProbe(IJSRuntime jsRuntime) : IWindowClientAreaProbe, IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Os.Platform.Blazor/windowClientArea.js";

    private readonly IJSRuntime _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
    private IJSObjectReference? _module;

    /// <inheritdoc />
    public async ValueTask<HackWindowSize?> MeasureAsync(WindowId windowId)
    {
        try
        {
            _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);
            ClientArea? area = await _module.InvokeAsync<ClientArea?>("measureWindowClient", windowId.ToString());
            return area is null ? null : new HackWindowSize(area.Width, area.Height);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
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

    /// <summary>Raw size returned by the JS module.</summary>
    private sealed record ClientArea(double Width, double Height);
}
