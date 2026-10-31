using HackerOs.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HackerOs.Theming.Blazor;

/// <summary>
/// Draws a cursor theme (ADR 0205 §4) on the whole document: every cursor the OS surface and its
/// apps ask for by name (<c>cursor: pointer</c>, <c>cursor: ew-resize</c>…) shows the theme's PNG
/// image for that name, as a real OS swaps the bitmaps behind the cursor names an app requests.
/// Renders nothing; a host renders one, inside its shell root.
/// </summary>
/// <remarks>
/// The work is done by the library's <c>cursorTheme.js</c> module (the CSS object model is out of
/// C#'s reach): it rewrites the same-origin style rules that name a themed cursor and restores
/// them when the theme changes. Nothing is imported while the theme draws no image, so the
/// <c>system</c> theme costs nothing and works without JavaScript (prerendering, tests).
/// </remarks>
public sealed class CursorThemeSurface : ComponentBase, IAsyncDisposable
{
    private const string ModulePath = "./_content/HackerOs.Theming.Blazor/cursorTheme.js";
    private IJSObjectReference? _module;
    private IReadOnlyDictionary<string, CursorImage> _applied = new Dictionary<string, CursorImage>(StringComparer.Ordinal);

    /// <summary>Gets or sets the theme to draw; <see langword="null"/> or <c>system</c> keeps the host's cursors.</summary>
    [Parameter]
    public CursorTheme? Theme { get; set; }

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    /// <summary>Gets the images to apply for a theme, by cursor name, fallbacks resolved.</summary>
    /// <param name="theme">Theme, or <see langword="null"/> for none.</param>
    internal static IReadOnlyDictionary<string, CursorImage> Build(CursorTheme? theme) =>
        theme?.ResolveAll() ?? new Dictionary<string, CursorImage>(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        IReadOnlyDictionary<string, CursorImage> desired = Build(Theme);
        if (Same(desired, _applied))
        {
            return;
        }

        try
        {
            _module ??= await JavaScript.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await _module.InvokeVoidAsync("applyCursorTheme", desired.ToDictionary(
                static pair => pair.Key,
                static pair => new CursorImageDto(pair.Value.Url, pair.Value.HotspotX, pair.Value.HotspotY),
                StringComparer.Ordinal));
            _applied = desired;
        }
        catch (JSDisconnectedException)
        {
            // The page is gone: nothing left to draw on.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("applyCursorTheme", new Dictionary<string, CursorImageDto>());
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Already gone.
        }
    }

    private static bool Same(IReadOnlyDictionary<string, CursorImage> left, IReadOnlyDictionary<string, CursorImage> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out CursorImage? other) && other == pair.Value);

    /// <summary>What the module receives for one cursor name.</summary>
    private sealed record CursorImageDto(string Url, int X, int Y);
}
