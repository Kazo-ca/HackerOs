using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Appearance &gt; Cursors (ADR 0205 §5): lists the built-in cursor themes and the theme
/// folders of <c>/etc/hackeros/cursors/</c> and <c>~/.config/hackeros/cursors/</c>, and writes the
/// chosen id to the <c>theme</c> key of <c>~/.config/hackeros/cursors.config</c>.
/// </summary>
/// <remarks>
/// The panel reads the folders itself with <see cref="HackDirectory"/> and <see cref="HackFile"/>
/// (public SDK only, ADR 0055), so a theme copied into a folder shows the next time the page opens.
/// The shell re-reads <c>cursors.config</c> and draws the theme at once. The machine's default
/// (<c>/etc/hackeros/cursors.config</c>, also used by the sign-in screen) is an administrator's
/// file, edited with <c>sudo nano</c>.
/// </remarks>
public sealed partial class CursorThemePanel
{
    /// <summary>OS document of the cursor theme.</summary>
    internal const string DocumentId = "cursors";

    /// <summary>Key of the theme id.</summary>
    internal const string ThemeKey = "theme";

    private const int MaxDiagnosticsShown = 5;

    private HackSettingsDocument? _document;
    private CursorThemeLibraryResult _library = CursorThemeLibraryResult.Empty;
    private string _selectedId = CursorThemeCatalog.SystemId;
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the themes offered: the built-in ones, then <c>/etc</c>, then <c>~</c>.</summary>
    internal IEnumerable<CursorTheme> Themes => _library.All;

    /// <summary>Gets the id of the theme the file chooses (all layers merged).</summary>
    internal string SelectedId => _selectedId;

    /// <summary>Gets the warnings of the theme folders.</summary>
    internal CursorThemeLibraryResult Library => _library;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>An administrator locked the theme in <c>/etc/hackeros/cursors.config</c>.</summary>
    private bool IsLocked => _document?.IsLocked(ThemeKey) == true;

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    /// <summary>Opens the document and reads the theme folders.</summary>
    internal async Task LoadAsync()
    {
        try
        {
            _document = await HackSettings.OpenSystemAsync(Context, DocumentId, Context.CancellationToken);
            _document.Changed += OnDocumentChanged;
            _selectedId = _document.Get<string>(ThemeKey);
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ShowMessage(L["Cursors.Error.CannotRead", exception.Message], isError: true);
        }

        _library = await CursorThemeLibrary.LoadAsync(
            async (directory, token) => await HackDirectory.GetDirectoriesAsync(directory, cancellationToken: token),
            (path, token) => HackFile.ReadAllTextAsync(path, token),
            (path, token) => HackFile.ReadAllBytesAsync(path, token),
            HackUser.Describe(Context).HomeDirectory,
            Context.CancellationToken);
        _loaded = true;
    }

    /// <summary>Writes a theme id to the user layer and reports the outcome.</summary>
    /// <param name="themeId">Id of an offered theme.</param>
    internal async Task ChooseAsync(string themeId)
    {
        if (_isBusy || _document is null || themeId == _selectedId)
        {
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            await _document.SetAsync(ThemeKey, themeId, SettingLayer.User, CancellationToken.None);
            _selectedId = _document.Get<string>(ThemeKey);
            ShowMessage(L["Cursors.Saved"], isError: false);
        }
        catch (HackSettingLockedException exception)
        {
            ShowMessage(exception.Message, isError: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowMessage(L["Cursors.Error.CannotSave", "~/.config/hackeros/cursors.config", exception.Message], isError: true);
        }
        finally
        {
            _isBusy = false;
        }

        StateHasChanged();
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

    /// <summary>An external edit (<c>nano ~/.config/hackeros/cursors.config</c>) changed the file.</summary>
    private void OnDocumentChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(() =>
        {
            if (!_isBusy && _document is not null)
            {
                _selectedId = _document.Get<string>(ThemeKey);
            }

            StateHasChanged();
        });

    private SettingLayer SourceOf() => _document?.GetSource(ThemeKey) ?? SettingLayer.Default;

    // Built-in names are translated; a theme of the files shows the name its author wrote.
    private string DisplayName(CursorTheme theme) => theme.Origin is null ? L[$"Cursors.Theme.{theme.Id}"] : theme.Name;

    private void ShowMessage(string message, bool isError)
    {
        _message = message;
        _messageIsError = isError;
    }

    private static string OptionClass(bool selected) => selected ? "cursor-option is-selected" : "cursor-option";
}
