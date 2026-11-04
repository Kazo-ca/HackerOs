namespace HackerOs.Theming.Abstractions;

/// <summary>Describes one selectable Kazo OS visual theme without depending on a UI framework.</summary>
public sealed record ThemeDefinition
{
    /// <summary>Creates a validated theme definition.</summary>
    /// <param name="id">Stable lowercase identifier persisted in settings and emitted to theme selectors.</param>
    /// <param name="displayName">Human-readable name shown by theme pickers.</param>
    /// <param name="platform">Shell form factor supported by the theme.</param>
    /// <param name="supportsAccentColor">Whether the theme intentionally consumes the selected accent color.</param>
    public ThemeDefinition(
        string id,
        string displayName,
        ThemePlatform platform,
        bool supportsAccentColor = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform));
        }

        Id = id;
        DisplayName = displayName;
        Platform = platform;
        SupportsAccentColor = supportsAccentColor;
    }

    /// <summary>Gets the stable persisted theme identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the localized-ready display label.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the shell form factor supported by the theme.</summary>
    public ThemePlatform Platform { get; }

    /// <summary>Gets whether the theme consumes the independently persisted accent choice.</summary>
    public bool SupportsAccentColor { get; }
}
