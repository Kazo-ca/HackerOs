using HackerOs.Platform.Core.Sounds;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Sounds;

/// <summary>
/// The browser's sound card (ADR 0204 section 5): plays a clip with the Web Audio API through
/// <c>osSounds.js</c>. A clip is decoded once and kept by its key (path and revision), so the bytes
/// of a file cross to JavaScript only the first time it plays.
/// </summary>
public sealed class BrowserSoundOutput : IOsSoundOutput, IAsyncDisposable
{
    private const string ModulePath = "./_content/HackerOs.Platform.Blazor/osSounds.js";

    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;

    /// <summary>Creates the output.</summary>
    /// <param name="jsRuntime">The browser runtime.</param>
    public BrowserSoundOutput(IJSRuntime jsRuntime) =>
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));

    /// <inheritdoc />
    public async ValueTask<bool> PlayAsync(OsSoundClip clip, double volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        try
        {
            IJSObjectReference module = _module ??= await _jsRuntime
                .InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
            string cached = await module.InvokeAsync<string>("playCached", cancellationToken, clip.Key, volume);
            if (cached != "missing")
            {
                return cached == "played";
            }

            byte[] bytes = await clip.LoadAsync(cancellationToken);
            return await module.InvokeAsync<bool>("play", cancellationToken, clip.Key, bytes, volume);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // No audio in this host (prerendering, a closed tab): the event goes on silently.
            return false;
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
                // The page is gone with its module.
            }
        }
    }
}
