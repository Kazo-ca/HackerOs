using HackerOs.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using HackerOs.Theming.Blazor.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace HackerOs.Theming.Blazor;

/// <summary>Renders a compact, non-interactive sample using the exact tokens used by shell chrome.</summary>
public partial class ThemePreview : ComponentBase, IAsyncDisposable
{
    private ElementReference _root;
    private ThemeTokenApplier? _applier;
    private IReadOnlyDictionary<string, string> _tokenProperties = ThemeTokenApplier.Build(null, animationsEnabled: true);

    private ThemeRenderState _renderState = ThemeRenderState.Create(
        WellKnownThemeIds.HackerOs,
        ThemePlatform.Desktop,
        WellKnownAccentIds.Green,
        animationsEnabled: true);

    /// <summary>Gets or sets the exact built-in theme identifier to preview.</summary>
    [Parameter]
    public string ThemeId { get; set; } = WellKnownThemeIds.HackerOs;

    /// <summary>Gets or sets the validated accent identifier; only KazoOS consumes its color.</summary>
    [Parameter]
    public string AccentId { get; set; } = WellKnownAccentIds.Green;

    /// <summary>Gets or sets whether the preview may use non-essential theme motion.</summary>
    [Parameter]
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the tokens of a user theme to preview over <see cref="ThemeId"/> (its base), or
    /// <see langword="null"/> for the built-in theme alone (ADR 0103 §2.3).
    /// </summary>
    [Parameter]
    public IReadOnlyList<KeyValuePair<string, string>>? Tokens { get; set; }

    /// <summary>Gets or sets the title shown in the sample window; the theme's name by default.</summary>
    [Parameter]
    public string? Title { get; set; }

    /// <summary>Gets or sets the JS runtime used to set user theme tokens.</summary>
    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    /// <summary>Gets or sets the services the optional localizer is resolved from (ADR 0104).</summary>
    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>Gets the package's texts: the host's language when it localizes, English otherwise.</summary>
    private IStringLocalizer L => _localizer ??= PackageStringLocalizer.Resolve<Strings>(Services);

    private IStringLocalizer? _localizer;

    /// <summary>Gets the validated, attribute-safe render state for the current parameters.</summary>
    private ThemeRenderState RenderState => _renderState;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _renderState = ThemeRenderState.Create(ThemeId, requiredPlatform: null, AccentId, AnimationsEnabled);
        _tokenProperties = ThemeTokenApplier.Build(Tokens, AnimationsEnabled);
    }

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (_applier is null && _tokenProperties.Count == 0)
        {
            return Task.CompletedTask;
        }

        _applier ??= new ThemeTokenApplier(JavaScript);
        return _applier.ApplyAsync(_root, _tokenProperties);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _applier?.DisposeAsync() ?? ValueTask.CompletedTask;
}
