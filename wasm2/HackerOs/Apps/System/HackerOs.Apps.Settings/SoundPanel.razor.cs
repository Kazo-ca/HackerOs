using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Sounds;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Sound (ADR 0204 section 6): turns the OS event sounds on or off, sets their volume and
/// picks the sound theme.
/// </summary>
/// <remarks>
/// <para>
/// The choices are the keys of <c>~/.config/hackeros/sound.config</c>, written through
/// <see cref="HackSettings.OpenSystemAsync(IAppExecutionContext, string, CancellationToken)"/>. The shell's
/// sound service re-reads the file at once and plays the notification sound of the new choice, so the
/// user hears it without a "test" button.
/// </para>
/// <para>
/// The themes are the folders of <c>/usr/share/sounds</c> (built-in and administrator themes) and of
/// <c>~/.local/share/sounds</c> (the user's own) that hold an <c>index.theme</c>; a user folder with
/// the name of a system theme is not listed, as the system one wins. Uses only the public SDK.
/// </para>
/// </remarks>
public sealed partial class SoundPanel
{
    /// <summary>OS document of the sound settings.</summary>
    internal const string DocumentId = "sound";

    /// <summary>Key that turns the sounds on or off.</summary>
    internal const string EnabledKey = "enabled";

    /// <summary>Key of the sound theme.</summary>
    internal const string ThemeKey = "theme";

    /// <summary>Key of the volume (0 to 100).</summary>
    internal const string VolumeKey = "volume";

    private const string DocumentFileName = DocumentId + ".config";

    private HackSettingsDocument? _document;
    private IReadOnlyList<SoundThemeChoice> _themes = [];
    private bool _enabled = true;
    private string _theme = OsSoundEvents.DefaultThemeId;
    private int _volume = 60;
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets whether the sounds are on (all layers merged).</summary>
    internal bool Enabled => _enabled;

    /// <summary>Gets the chosen theme identifier.</summary>
    internal string Theme => _theme;

    /// <summary>Gets the volume, 0 to 100.</summary>
    internal int Volume => _volume;

    /// <summary>Gets the themes found on disk, system themes first.</summary>
    internal IReadOnlyList<SoundThemeChoice> Themes => _themes;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>Gets whether <see cref="Message"/> reports a failure.</summary>
    internal bool MessageIsError => _messageIsError;

    private string UserThemesDirectory => OsSoundEvents.GetUserThemesDirectory(Context.HomeDirectory);

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    /// <summary>Opens the document, reads the choices and lists the themes.</summary>
    internal async Task LoadAsync()
    {
        try
        {
            _document = await HackSettings.OpenSystemAsync(Context, DocumentId, Context.CancellationToken);
            _document.Changed += OnDocumentChanged;
            ReadDocument();
            _themes = await ListThemesAsync(Context.CancellationToken);
            _loaded = true;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ShowError(L["Sound.Error.CannotRead", DocumentFileName, exception.Message]);
        }
    }

    /// <summary>Turns the sounds on or off.</summary>
    /// <param name="enabled">The new state.</param>
    internal Task SetEnabledAsync(bool enabled) =>
        WriteAsync(EnabledKey, enabled, enabled ? L["Sound.Saved.On"] : L["Sound.Saved.Off"]);

    /// <summary>Sets the volume.</summary>
    /// <param name="volume">0 to 100; clamped.</param>
    internal Task SetVolumeAsync(int volume)
    {
        int value = Math.Clamp(volume, 0, 100);
        return WriteAsync(VolumeKey, (long)value, L["Sound.Saved.Volume", value]);
    }

    /// <summary>Chooses a sound theme listed in <see cref="Themes"/>.</summary>
    /// <param name="themeId">The theme identifier.</param>
    internal Task SetThemeAsync(string themeId)
    {
        SoundThemeChoice? choice = _themes.FirstOrDefault(theme => theme.Id == themeId);
        if (choice is null)
        {
            ShowError(L["Sound.Error.UnknownTheme", themeId]);
            StateHasChanged();
            return Task.CompletedTask;
        }

        return WriteAsync(ThemeKey, themeId, L["Sound.Saved.Theme", choice.Name]);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            await _document.DisposeAsync();
        }
    }

    private async Task WriteAsync<T>(string key, T value, string savedMessage)
    {
        if (_isBusy || _document is null)
        {
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            await _document.SetAsync(key, value, SettingLayer.User, CancellationToken.None);
            ReadDocument();
            ShowSaved(savedMessage);
        }
        catch (HackSettingLockedException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowError(L["Sound.Error.CannotSave", "~/.config/hackeros/" + DocumentFileName, exception.Message]);
        }
        finally
        {
            _isBusy = false;
        }

        // A click re-renders by itself; a direct call (a test) must ask for it.
        StateHasChanged();
    }

    private async Task<IReadOnlyList<SoundThemeChoice>> ListThemesAsync(CancellationToken cancellationToken)
    {
        List<SoundThemeChoice> themes = [];
        await AddThemesAsync(themes, OsSoundEvents.SystemThemesDirectory, isUserTheme: false, cancellationToken);
        await AddThemesAsync(themes, UserThemesDirectory, isUserTheme: true, cancellationToken);
        return themes;
    }

    private async Task AddThemesAsync(
        List<SoundThemeChoice> themes, string root, bool isUserTheme, CancellationToken cancellationToken)
    {
        if (!await HackDirectory.ExistsAsync(root, cancellationToken))
        {
            return;
        }

        foreach (string directory in (await HackDirectory.GetDirectoriesAsync(root, cancellationToken: cancellationToken)).Order(StringComparer.Ordinal))
        {
            string id = directory[(directory.LastIndexOf('/') + 1)..];
            string index = $"{directory}/{OsSoundEvents.IndexFileName}";
            if (!OsSoundEvents.IsValidThemeId(id)
                || themes.Any(theme => theme.Id == id)
                || !await HackFile.ExistsAsync(index, cancellationToken))
            {
                continue;
            }

            try
            {
                SoundThemeIndex theme = SoundThemeIndex.Parse(id, await HackFile.ReadAllTextAsync(index, cancellationToken));
                themes.Add(new SoundThemeChoice(
                    id, theme.GetName(Context.Culture), theme.GetComment(Context.Culture), directory, isUserTheme));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable theme is not offered; the others still are.
            }
        }
    }

    private Task OnEnabledChanged(ChangeEventArgs args) =>
        SetEnabledAsync(args.Value is true || (args.Value is string text && bool.TryParse(text, out bool value) && value));

    private Task OnVolumeChanged(ChangeEventArgs args) =>
        int.TryParse(args.Value as string, out int value) ? SetVolumeAsync(value) : Task.CompletedTask;

    private void ReadDocument()
    {
        if (_document is null)
        {
            return;
        }

        _enabled = _document.Get<bool>(EnabledKey);
        string theme = _document.Get<string>(ThemeKey)?.Trim() ?? string.Empty;
        _theme = OsSoundEvents.IsValidThemeId(theme) ? theme : OsSoundEvents.DefaultThemeId;
        _volume = (int)Math.Clamp(_document.Get<long>(VolumeKey), 0, 100);
    }

    /// <summary>An external edit (<c>nano ~/.config/hackeros/sound.config</c>) changed the file.</summary>
    private void OnDocumentChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(() =>
        {
            if (!_isBusy)
            {
                ReadDocument();
            }

            StateHasChanged();
        });

    private SettingLayer SourceOf(string key) => _document?.GetSource(key) ?? SettingLayer.Default;

    private bool IsLocked(string key) => _document?.IsLocked(key) == true;

    private static string OptionClass(bool selected) => selected ? "sound-option is-selected" : "sound-option";

    private void ShowSaved(string message)
    {
        _message = message;
        _messageIsError = false;
    }

    private void ShowError(string message)
    {
        _message = message;
        _messageIsError = true;
    }
}

/// <summary>One sound theme offered by Settings &gt; Sound.</summary>
/// <param name="Id">Folder name, written to <c>sound.config</c>.</param>
/// <param name="Name">Name in the reader's language.</param>
/// <param name="Comment">Description, or <see langword="null"/>.</param>
/// <param name="Directory">Theme folder.</param>
/// <param name="IsUserTheme">Whether the theme is in the user's <c>~/.local/share/sounds</c>.</param>
internal sealed record SoundThemeChoice(string Id, string Name, string? Comment, string Directory, bool IsUserTheme);
