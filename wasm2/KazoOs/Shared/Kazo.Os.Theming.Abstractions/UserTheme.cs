namespace Kazo.Os.Theming.Abstractions;

/// <summary>Where a user theme file was found (ADR 0103 §2.2), like the theme folders of a Linux desktop.</summary>
public enum ThemeOrigin
{
    /// <summary><c>/etc/hackeros/themes/</c>: installed by an administrator for every user.</summary>
    System,

    /// <summary><c>~/.config/hackeros/themes/</c>: the session user's own themes.</summary>
    User
}

/// <summary>
/// A theme made of verified tokens over a built-in base theme (ADR 0103 §2.2): the content of one
/// <c>.theme</c> file. Tokens it does not set keep the base theme's value.
/// </summary>
public sealed record UserTheme
{
    /// <summary>Creates a theme; values must already be validated (see <see cref="ThemeFile"/>).</summary>
    /// <param name="id">Lowercase identifier written in <c>appearance.config</c>.</param>
    /// <param name="name">Display name.</param>
    /// <param name="baseThemeId">Built-in theme the tokens apply over.</param>
    /// <param name="platforms">Shell form factors the theme is offered for.</param>
    /// <param name="tokens">Token values by CSS custom property name, in file order.</param>
    /// <param name="origin">Folder the file came from.</param>
    /// <param name="sourcePath">Absolute virtual path of the file, when it was read from one.</param>
    public UserTheme(
        string id,
        string name,
        string baseThemeId,
        IReadOnlyList<ThemePlatform> platforms,
        IReadOnlyList<KeyValuePair<string, string>> tokens,
        ThemeOrigin origin = ThemeOrigin.User,
        string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseThemeId);
        ArgumentNullException.ThrowIfNull(platforms);
        ArgumentNullException.ThrowIfNull(tokens);
        Id = id;
        Name = name;
        BaseThemeId = baseThemeId;
        Platforms = platforms;
        Tokens = tokens;
        Origin = origin;
        SourcePath = sourcePath;
    }

    /// <summary>Gets the identifier written in <c>appearance.config</c>.</summary>
    public string Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the built-in base theme id.</summary>
    public string BaseThemeId { get; init; }

    /// <summary>Gets the form factors the theme is offered for.</summary>
    public IReadOnlyList<ThemePlatform> Platforms { get; init; }

    /// <summary>Gets the token values, in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Tokens { get; init; }

    /// <summary>Gets the folder the file came from.</summary>
    public ThemeOrigin Origin { get; init; }

    /// <summary>Gets the file path, when the theme was read from a file.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Gets whether the theme is offered on <paramref name="platform"/>.</summary>
    /// <param name="platform">Shell form factor.</param>
    public bool Supports(ThemePlatform platform) => Platforms.Contains(platform);

    /// <summary>
    /// Builds the CSS custom properties to set on the theme's root element: every token, plus the
    /// <c>-rgb</c> companion of each color that has one (<see cref="ThemeTokenDefinition.RgbCompanion"/>).
    /// Values are validated once more, so a hand-built instance cannot inject anything.
    /// </summary>
    /// <param name="animationsEnabled">
    /// When <see langword="false"/>, the animation tokens are left out so the reduced-motion values
    /// of the stylesheet stay in force.
    /// </param>
    public IReadOnlyDictionary<string, string> ToCssProperties(bool animationsEnabled = true) =>
        BuildCssProperties(Tokens, animationsEnabled);

    /// <summary>Builds CSS custom properties from raw token pairs, keeping only known tokens with valid values.</summary>
    /// <param name="tokens">Token pairs.</param>
    /// <param name="animationsEnabled">Whether animation tokens are kept.</param>
    public static IReadOnlyDictionary<string, string> BuildCssProperties(
        IEnumerable<KeyValuePair<string, string>> tokens, bool animationsEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        foreach ((string name, string value) in tokens)
        {
            if (!ThemeTokenCatalog.TryGet(name, out ThemeTokenDefinition? token)
                || !ThemeTokenValidator.TryValidate(token.Type, value, out string normalized, out _)
                || (!animationsEnabled && token.Group == ThemeTokenGroup.Animations))
            {
                continue;
            }

            properties[name] = normalized;
            if (token.RgbCompanion is { } companion && ThemeTokenValidator.TryGetRgbTriplet(normalized, out string triplet))
            {
                properties[companion] = triplet;
            }
        }

        return properties;
    }
}
