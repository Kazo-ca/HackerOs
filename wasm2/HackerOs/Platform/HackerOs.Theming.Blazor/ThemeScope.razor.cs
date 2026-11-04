using HackerOs.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HackerOs.Theming.Blazor;

/// <summary>
/// Applies one validated theme to an arbitrary subtree and contributes the RCL's static theme
/// stylesheet to the document head. A host should render one scope around its shell root.
/// A user theme (ADR 0103) renders its built-in base as <see cref="ThemeId"/> and passes its
/// verified tokens as <see cref="Tokens"/>; they are set on the scope's root element.
/// </summary>
public partial class ThemeScope : ComponentBase, IAsyncDisposable
{
    private ElementReference _root;
    private ThemeTokenApplier? _applier;
    private IReadOnlyDictionary<string, string> _tokenProperties = ThemeTokenApplier.Build(null, animationsEnabled: true);

    private ThemeRenderState _renderState = ThemeRenderState.Create(
        WellKnownThemeIds.HackerOs,
        ThemePlatform.Desktop,
        WellKnownAccentIds.Green,
        animationsEnabled: true);

    /// <summary>Gets or sets the exact built-in theme identifier to apply.</summary>
    [Parameter]
    public string ThemeId { get; set; } = WellKnownThemeIds.HackerOs;

    /// <summary>Gets or sets the shell form factor the selected theme must support.</summary>
    [Parameter]
    public ThemePlatform Platform { get; set; } = ThemePlatform.Desktop;

    /// <summary>Gets or sets the validated accent identifier; only KazoOS consumes its color.</summary>
    [Parameter]
    public string AccentId { get; set; } = WellKnownAccentIds.Green;

    /// <summary>Gets or sets whether non-essential theme motion remains enabled.</summary>
    [Parameter]
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the high-contrast layer (ADR 0105 section 2) covers the theme: it redefines
    /// the color, border and focus tokens of every theme, user themes included.
    /// </summary>
    [Parameter]
    public bool HighContrast { get; set; }

    /// <summary>
    /// Gets or sets the text size factor of ADR 0105: <c>1.0</c>, <c>1.25</c>, <c>1.5</c> or <c>2.0</c>.
    /// Platform text sizes are in <c>rem</c>, so the scope scales the document's root font size.
    /// </summary>
    [Parameter]
    public double TextScale { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the tokens of a user theme to apply over <see cref="ThemeId"/>, or
    /// <see langword="null"/> for the built-in theme alone. Unknown tokens and invalid values are
    /// ignored; animation tokens are ignored while <see cref="AnimationsEnabled"/> is false.
    /// </summary>
    [Parameter]
    public IReadOnlyList<KeyValuePair<string, string>>? Tokens { get; set; }

    /// <summary>Gets or sets the JS runtime used to set user theme tokens.</summary>
    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    /// <summary>Gets or sets the shell or reusable chrome subtree receiving the theme tokens.</summary>
    [Parameter, EditorRequired]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Gets the validated, attribute-safe render state for the current parameters.</summary>
    private ThemeRenderState RenderState => _renderState;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!Enum.IsDefined(Platform))
        {
            throw new ArgumentOutOfRangeException(nameof(Platform));
        }

        _renderState = ThemeRenderState.Create(ThemeId, Platform, AccentId, AnimationsEnabled, HighContrast, TextScale);
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
