using System.Text;
using HackerOs.AppSdk.Globalization;
using HackerOs.Theming.Abstractions;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.ThemeEditor;

/// <summary>
/// The theme being edited (ADR 0103 §2): identity, base theme, form factors and the token values
/// the user typed, with the validation result of each. A value is kept as typed even when it is
/// invalid, so the user can fix it; only valid values reach the preview and the file.
/// </summary>
public sealed class ThemeEditorDocument
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private bool _idEdited;
    private string _savedSnapshot;

    // The editor's texts, for the default name and the problems that prevent saving (ADR 0104).
    private readonly IStringLocalizer _text;

    /// <summary>Creates a new, empty theme over the KazoOS theme.</summary>
    /// <param name="text">
    /// The editor's texts in the window's language; <see langword="null"/> reads them in the language of
    /// the process running the code (English outside any app, as in unit tests).
    /// </param>
    public ThemeEditorDocument(IStringLocalizer? text = null)
    {
        _text = text ?? HackLocalization.GetLocalizer<Strings>();
        Reset(WellKnownThemeIds.HackerOs);
        _savedSnapshot = Snapshot();
    }

    /// <summary>Gets or sets the display name. Until the id is edited, the id follows the name.</summary>
    public string Name
    {
        get;
        set
        {
            field = value ?? string.Empty;
            if (!_idEdited)
            {
                Id = Slugify(field);
            }
        }
    } = string.Empty;

    /// <summary>Gets the theme id (file name and value written in <c>appearance.config</c>).</summary>
    public string Id { get; private set; } = string.Empty;

    /// <summary>Gets or sets the built-in base theme id.</summary>
    public string BaseThemeId { get; set; } = WellKnownThemeIds.HackerOs;

    /// <summary>Gets or sets whether the theme is offered on the desktop.</summary>
    public bool ForDesktop { get; set; } = true;

    /// <summary>Gets or sets whether the theme is offered on mobile.</summary>
    public bool ForMobile { get; set; }

    /// <summary>Gets the file the theme was opened from or last saved to, if any.</summary>
    public string? Path { get; private set; }

    /// <summary>Gets whether the document differs from the last opened or saved state.</summary>
    public bool IsDirty => Snapshot() != _savedSnapshot;

    /// <summary>Gets the number of tokens with a value.</summary>
    public int TokenCount => _values.Count;

    /// <summary>Gets the platforms selected, in canonical order (desktop first).</summary>
    public IReadOnlyList<ThemePlatform> Platforms =>
        [.. new[] { (ForDesktop, ThemePlatform.Desktop), (ForMobile, ThemePlatform.Mobile) }.Where(static p => p.Item1).Select(static p => p.Item2)];

    /// <summary>Gets the valid token values, in catalog order, for the preview and the file.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ValidTokens =>
        [.. ThemeTokenCatalog.All
            .Where(token => _values.ContainsKey(token.Name) && !_errors.ContainsKey(token.Name))
            .Select(token => new KeyValuePair<string, string>(token.Name, _values[token.Name].Trim()))];

    /// <summary>Sets the id by hand; from then on it no longer follows the name.</summary>
    /// <param name="id">New id, lowercased.</param>
    public void SetId(string? id)
    {
        _idEdited = true;
        Id = (id ?? string.Empty).Trim().ToLowerInvariant();
    }

    /// <summary>Gets the value typed for a token, or <see langword="null"/> when the base value applies.</summary>
    /// <param name="tokenName">Token name.</param>
    public string? GetValue(string tokenName) => _values.TryGetValue(tokenName, out string? value) ? value : null;

    /// <summary>Gets the validation error of a token, or <see langword="null"/>.</summary>
    /// <param name="tokenName">Token name.</param>
    public string? GetError(string tokenName) => _errors.TryGetValue(tokenName, out string? error) ? error : null;

    /// <summary>Sets (or, with an empty value, clears) a token and validates it.</summary>
    /// <param name="tokenName">Catalog token name.</param>
    /// <param name="value">Typed value.</param>
    public void SetValue(string tokenName, string? value)
    {
        if (!ThemeTokenCatalog.TryGet(tokenName, out ThemeTokenDefinition? token))
        {
            throw new ArgumentException($"Unknown token '{tokenName}'.", nameof(tokenName));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            Clear(tokenName);
            return;
        }

        _values[tokenName] = value;
        if (ThemeTokenValidator.TryValidate(token.Type, value, Strings.CultureOf(_text), out _, out string? error))
        {
            _errors.Remove(tokenName);
        }
        else
        {
            _errors[tokenName] = error!;
        }
    }

    /// <summary>Removes a token so the base theme's value applies again.</summary>
    /// <param name="tokenName">Token name.</param>
    public void Clear(string tokenName)
    {
        _values.Remove(tokenName);
        _errors.Remove(tokenName);
    }

    /// <summary>Starts a new theme over a base theme.</summary>
    /// <param name="baseThemeId">Built-in base theme id.</param>
    public void Reset(string baseThemeId)
    {
        ThemeDefinition baseTheme = ThemeCatalog.Get(baseThemeId);
        _values.Clear();
        _errors.Clear();
        _idEdited = false;
        BaseThemeId = baseTheme.Id;
        ForDesktop = baseTheme.Platform == ThemePlatform.Desktop;
        ForMobile = baseTheme.Platform == ThemePlatform.Mobile;
        Name = _text["Document.DefaultName", baseTheme.DisplayName];
        Path = null;
        _savedSnapshot = Snapshot();
    }

    /// <summary>Loads a theme read from a file.</summary>
    /// <param name="theme">Parsed theme.</param>
    /// <param name="path">File it came from.</param>
    public void Load(UserTheme theme, string? path)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _values.Clear();
        _errors.Clear();
        _idEdited = true;
        Name = theme.Name;
        Id = theme.Id;
        BaseThemeId = theme.BaseThemeId;
        ForDesktop = theme.Supports(ThemePlatform.Desktop);
        ForMobile = theme.Supports(ThemePlatform.Mobile);
        foreach ((string name, string value) in theme.Tokens)
        {
            SetValue(name, value);
        }

        Path = path;
        _savedSnapshot = Snapshot();
    }

    /// <summary>Records the document as saved to <paramref name="path"/>.</summary>
    /// <param name="path">File written.</param>
    public void MarkSaved(string path)
    {
        Path = path;
        _savedSnapshot = Snapshot();
    }

    /// <summary>
    /// Lists what prevents saving: an invalid identity, no platform, or invalid token values.
    /// Empty when the theme can be saved.
    /// </summary>
    public IReadOnlyList<string> GetProblems()
    {
        List<string> problems = [];
        if (!ThemeFile.IsValidId(Id))
        {
            problems.Add(_text["Problem.Id"]);
        }
        else if (ThemeCatalog.TryGet(Id, out _))
        {
            problems.Add(_text["Problem.BuiltInId", Id]);
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > ThemeFile.MaxNameLength || Name.Any(char.IsControl))
        {
            problems.Add(_text["Problem.Name", ThemeFile.MaxNameLength]);
        }

        if (!ForDesktop && !ForMobile)
        {
            problems.Add(_text["Problem.Platforms"]);
        }

        if (_errors.Count > 0)
        {
            problems.Add(_text["Problem.Tokens", _errors.Count, string.Join(", ", _errors.Keys)]);
        }

        return problems;
    }

    /// <summary>Builds the theme to save; call only when <see cref="GetProblems"/> is empty.</summary>
    public UserTheme ToUserTheme() => new(Id, Name.Trim(), BaseThemeId, Platforms, ValidTokens, ThemeOrigin.User, Path);

    /// <summary>Turns a display name into a theme id: lowercase ASCII letters and digits joined by dashes.</summary>
    /// <param name="name">Display name.</param>
    public static string Slugify(string name)
    {
        StringBuilder builder = new();
        foreach (char character in name.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
            }
            else if (char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark
                && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        string slug = builder.ToString().Trim('-');
        return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
    }

    private string Snapshot() =>
        string.Join('\n',
            [Name, Id, BaseThemeId, ForDesktop.ToString(), ForMobile.ToString(),
             .. _values.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}")]);
}
