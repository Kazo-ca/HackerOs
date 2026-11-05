using Kazo.Os.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Theming.Blazor;

/// <summary>
/// Applies the tokens of a user theme to one theme root element (ADR 0103 §2.3) through the
/// library's <c>themeTokens.js</c> module, setting only what changed since the previous call.
/// Shared by <see cref="ThemeScope"/> and <see cref="ThemePreview"/>.
/// </summary>
/// <remarks>
/// Tokens are re-validated here (<see cref="UserTheme.BuildCssProperties"/>): only catalog tokens
/// with valid values, and their derived <c>-rgb</c> companions, reach the browser. Nothing is
/// imported while no token was ever applied, so the built-in themes keep working without
/// JavaScript (prerendering, tests).
/// </remarks>
internal sealed class ThemeTokenApplier : IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Os.Theming.Blazor/themeTokens.js";
    private readonly IJSRuntime _javaScript;
    private IReadOnlyDictionary<string, string> _applied = new Dictionary<string, string>(StringComparer.Ordinal);
    private IJSObjectReference? _module;

    /// <summary>Creates an applier over the component's JS runtime.</summary>
    /// <param name="javaScript">JS runtime.</param>
    public ThemeTokenApplier(IJSRuntime javaScript) => _javaScript = javaScript;

    /// <summary>Builds the properties to apply for a set of tokens, honoring the animation switch.</summary>
    /// <param name="tokens">Token pairs, or <see langword="null"/> for none.</param>
    /// <param name="animationsEnabled">Whether animation tokens apply.</param>
    public static IReadOnlyDictionary<string, string> Build(IEnumerable<KeyValuePair<string, string>>? tokens, bool animationsEnabled) =>
        tokens is null ? new Dictionary<string, string>(StringComparer.Ordinal) : UserTheme.BuildCssProperties(tokens, animationsEnabled);

    /// <summary>Applies <paramref name="desired"/> to <paramref name="element"/>, removing properties no longer wanted.</summary>
    /// <param name="element">Theme root element.</param>
    /// <param name="desired">Properties to have, from <see cref="Build"/>.</param>
    public async Task ApplyAsync(ElementReference element, IReadOnlyDictionary<string, string> desired)
    {
        string[] removed = [.. _applied.Keys.Where(name => !desired.ContainsKey(name))];
        Dictionary<string, string> changed = desired
            .Where(pair => !_applied.TryGetValue(pair.Key, out string? current) || current != pair.Value)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        if (removed.Length == 0 && changed.Count == 0)
        {
            return;
        }

        try
        {
            _module ??= await _javaScript.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await _module.InvokeVoidAsync("applyTokens", element, changed, removed);
            _applied = desired;
        }
        catch (JSDisconnectedException)
        {
            // The circuit or page is gone: nothing left to theme.
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
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Already gone.
        }
    }
}
