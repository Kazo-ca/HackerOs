using HackerOs.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;

namespace HackerOs.Windowing.Blazor;

/// <summary>
/// The window layers of the screens that show one desktop (ADR 0178 §3): every
/// <see cref="DesktopArea"/> given the same instance registers the layer of its screen here, and
/// the area of screen 1 renders every window once, then moves each one into the layer of its
/// window's screen. Changing screen moves the window's element; it never recreates its components.
/// </summary>
/// <remarks>
/// A window whose screen has no layer (the screen is not shown yet, or its area is gone) stays in
/// the layer of screen 1. See doc/en/wasm/dev/window-portal.md.
/// </remarks>
public sealed class WindowLayers
{
    private readonly Dictionary<DisplayId, ElementReference> _layers = [];

    /// <summary>Raised when a screen's layer is added or removed.</summary>
    public event Action? Changed;

    /// <summary>Gets the screens that have a layer, screen 1 first, then by number.</summary>
    public IReadOnlyList<DisplayId> Displays => [.. _layers.Keys.OrderBy(display => display.Number)];

    /// <summary>Gets the window layer of a screen.</summary>
    /// <param name="displayId">The screen.</param>
    /// <param name="layer">The layer element, when the screen's area is shown.</param>
    /// <returns><see langword="true"/> when the screen has a layer.</returns>
    public bool TryGetLayer(DisplayId displayId, out ElementReference layer) =>
        _layers.TryGetValue(displayId, out layer);

    internal void Register(DisplayId displayId, ElementReference layer)
    {
        _layers[displayId] = layer;
        Changed?.Invoke();
    }

    internal void Unregister(DisplayId displayId, ElementReference layer)
    {
        // A newer area of the same screen may already have taken its place.
        if (_layers.TryGetValue(displayId, out ElementReference current) && current.Id == layer.Id)
        {
            _layers.Remove(displayId);
            Changed?.Invoke();
        }
    }
}
