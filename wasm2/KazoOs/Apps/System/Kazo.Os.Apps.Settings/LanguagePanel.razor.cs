using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Language (ADR 0104): shows the language and locale of this session, lets the user
/// choose one of <see cref="HackLocale.AvailableLocales"/> or follow the system language, and shows
/// the system locale.
/// </summary>
/// <remarks>
/// <para>
/// Like GNOME's region settings on Linux, the choice is a file, not a live switch: picking a locale
/// writes <c>LANG=&lt;locale&gt;</c> to <c>~/.config/locale.conf</c> (systemd format, through
/// <see cref="HackLocale.FormatLocaleConf"/>); "follow the system language" removes <c>LANG</c> from
/// it, deleting the file when nothing else is left. The login environment reads that file at the
/// next sign-in (<c>/etc/locale.conf</c> first, then the user's file), so the desktop and the apps
/// started afterwards speak the new language; this session keeps its own until then.
/// </para>
/// <para>
/// The session's language is read from the Settings process itself (its <c>LANG</c>,
/// <c>LC_MESSAGES</c>, <c>LC_ALL</c>, ADR 0050), which inherited the login environment. The system
/// locale (<c>/etc/locale.conf</c>) is shown read-only: changing it is an administrator's job
/// (<c>sudo localectl set-locale</c>). Uses only the public SDK: <see cref="HackFile"/>,
/// <see cref="HackDirectory"/> and the home directory of <see cref="HackUser"/>.
/// </para>
/// </remarks>
public sealed partial class LanguagePanel
{
    /// <summary>
    /// The user's locale file, relative to the home directory. Mirrors the login environment's
    /// constant (Platform Core), which apps do not reference: they only see the public SDK.
    /// </summary>
    internal const string UserLocaleFileRelativePath = ".config/locale.conf";

    /// <summary>The system locale file, written by <c>localectl set-locale</c>.</summary>
    internal const string SystemLocaleFilePath = "/etc/locale.conf";

    /// <summary>The comment written at the top of <c>~/.config/locale.conf</c> (file content, never translated).</summary>
    internal const string FileHeader = "Written by Settings > Language. Read at the next sign-in.";

    private IReadOnlyDictionary<string, string> _userVariables = new Dictionary<string, string>(StringComparer.Ordinal);
    private bool _userFileExists;
    private string? _systemLocale;
    private string? _systemError;
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the absolute path of the user's locale file.</summary>
    internal string UserLocaleFilePath => $"{HackUser.Describe(Context).HomeDirectory.TrimEnd('/')}/{UserLocaleFileRelativePath}";

    /// <summary>Gets the <c>LANG</c> of the user's file: their choice, or <see langword="null"/> when they follow the system.</summary>
    internal string? ChosenLocale => _userVariables.TryGetValue(HackLocale.LangVariable, out string? locale) ? locale : null;

    /// <summary>Gets the text of the last outcome (saved or failed), or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>Gets whether <see cref="Message"/> reports a failure.</summary>
    internal bool MessageIsError => _messageIsError;

    /// <summary>Gets the <c>LANG</c> of this process (the session's, unless the process was started with another).</summary>
    private string? SessionLang => Context.Environment.GetVariable(HackLocale.LangVariable) is { Length: > 0 } lang ? lang : null;

    /// <summary>Gets the variables that override <c>LANG</c> for the texts of this process, when set.</summary>
    private IEnumerable<(string Name, string Value)> SessionOverrides =>
        new[] { HackLocale.MessagesVariable, HackLocale.AllVariable }
            .Select(name => (Name: name, Value: Context.Environment.GetVariable(name) ?? string.Empty))
            .Where(static variable => variable.Value.Length > 0);

    /// <summary>Gets the system locale, or the built-in default when <c>/etc/locale.conf</c> sets none.</summary>
    private string SystemLocaleOrDefault => _systemLocale ?? HackLocale.DefaultLocale;

    /// <summary>Gets the <c>LANG</c> the next sign-in will start with (the user's file wins over the system's).</summary>
    private string? NextSignInLocale => _loaded ? ChosenLocale ?? SystemLocaleOrDefault : null;

    /// <summary>A date and a number in the formats of this process (<c>LC_TIME</c>, <c>LC_NUMERIC</c>).</summary>
    private string FormatSample
    {
        get
        {
            CultureInfo formats = Context.FormatCulture;
            return $"{DateTimeOffset.Now.ToString("D", formats)} · {1234567.89.ToString("N2", formats)}";
        }
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    /// <summary>Reads <c>/etc/locale.conf</c> and <c>~/.config/locale.conf</c>.</summary>
    internal async Task LoadAsync()
    {
        _systemError = null;
        try
        {
            string content = await HackFile.ReadAllTextAsync(SystemLocaleFilePath, Context.CancellationToken);
            _systemLocale = HackLocale.ParseLocaleConf(content).GetValueOrDefault(HackLocale.LangVariable);
        }
        catch (HackFileNotFoundException)
        {
            _systemLocale = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _systemLocale = null;
            _systemError = L["Language.Error.CannotRead", SystemLocaleFilePath, exception.Message];
        }

        try
        {
            string content = await HackFile.ReadAllTextAsync(UserLocaleFilePath, Context.CancellationToken);
            _userVariables = HackLocale.ParseLocaleConf(content);
            _userFileExists = true;
        }
        catch (Exception exception) when (exception is HackFileNotFoundException or HackDirectoryNotFoundException)
        {
            _userVariables = new Dictionary<string, string>(StringComparer.Ordinal);
            _userFileExists = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _userVariables = new Dictionary<string, string>(StringComparer.Ordinal);
            _userFileExists = false;
            ShowError(L["Language.Error.CannotRead", "~/" + UserLocaleFileRelativePath, exception.Message]);
        }

        _loaded = true;
    }

    /// <summary>
    /// Saves the user's choice: <paramref name="locale"/> as <c>LANG</c> in <c>~/.config/locale.conf</c>,
    /// or, when <see langword="null"/>, removes <c>LANG</c> so the system language applies again (the
    /// file is deleted when it holds nothing else). Other variables of the file (<c>LC_TIME</c>…) are kept.
    /// </summary>
    /// <param name="locale">One of <see cref="HackLocale.AvailableLocales"/>, or <see langword="null"/> to follow the system.</param>
    internal async Task ChooseAsync(string? locale)
    {
        if (_isBusy)
        {
            return;
        }

        if (locale is not null && !HackLocale.IsAvailable(locale))
        {
            ShowError(L["Language.Error.Unknown", locale]);
            StateHasChanged();
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            Dictionary<string, string> variables = new(_userVariables, StringComparer.Ordinal);
            if (locale is null)
            {
                variables.Remove(HackLocale.LangVariable);
                if (variables.Count == 0)
                {
                    if (_userFileExists)
                    {
                        await HackFile.DeleteAsync(UserLocaleFilePath, Context.CancellationToken);
                    }
                }
                else
                {
                    await WriteAsync(variables);
                }

                ShowSaved(L["Language.Saved.FollowSystem", Describe(SystemLocaleOrDefault)]);
            }
            else
            {
                variables[HackLocale.LangVariable] = locale;
                await WriteAsync(variables);
                ShowSaved(L["Language.Saved.Locale", Describe(locale)]);
            }

            _userVariables = variables;
            _userFileExists = variables.Count > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(locale is null
                ? L["Language.Error.CannotRemove", "~/" + UserLocaleFileRelativePath, exception.Message]
                : L["Language.Error.CannotSave", "~/" + UserLocaleFileRelativePath, exception.Message]);
        }
        finally
        {
            _isBusy = false;
        }

        // A click re-renders by itself; a direct call (another panel, a test) must ask for it.
        StateHasChanged();
    }

    /// <summary>Writes the user's file, creating <c>~/.config</c> first when the account has none yet.</summary>
    private async Task WriteAsync(IReadOnlyDictionary<string, string> variables)
    {
        string directory = UserLocaleFilePath[..UserLocaleFilePath.LastIndexOf('/')];
        await HackDirectory.CreateDirectoryAsync(directory, Context.CancellationToken);
        await HackFile.WriteAllTextAsync(UserLocaleFilePath, HackLocale.FormatLocaleConf(variables, FileHeader), Context.CancellationToken);
    }

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

    private bool IsChosen(string locale) => ChosenLocale is { } chosen && SameLocale(chosen, locale);

    private string OptionClass(string? locale) =>
        (locale is null ? ChosenLocale is null : IsChosen(locale)) ? "locale-option is-selected" : "locale-option";

    /// <summary>Compares two POSIX locales the way glibc does: the charset spelling does not matter (<c>fr_CA.utf8</c> = <c>fr_CA.UTF-8</c>).</summary>
    private static bool SameLocale(string? left, string? right) =>
        left is not null && right is not null
        && string.Equals(StripCharset(left), StripCharset(right), StringComparison.OrdinalIgnoreCase);

    private static string StripCharset(string locale) => locale.Split('.', '@')[0];

    /// <summary>The short tag of a locale card: <c>FR-CA</c>, <c>EN-US</c>, <c>C</c>.</summary>
    private static string LocaleTag(string locale) => StripCharset(locale).Replace('_', '-').ToUpperInvariant();

    /// <summary>
    /// The human name of a locale, in its own language like GNOME's list ("Français (Canada)",
    /// "English (United States)"); the <c>C</c> locale has a translated description.
    /// </summary>
    internal string LocaleName(string locale) => HackLocale.ToCulture(locale) switch
    {
        null => locale,
        { Name.Length: 0 } => L["Language.Locale.Posix"],
        CultureInfo culture => Capitalize(culture.NativeName, culture)
    };

    /// <summary>A locale as one line: "Français (Canada) — fr_CA.UTF-8".</summary>
    internal string Describe(string locale) => $"{LocaleName(locale)} — {locale}";

    /// <summary>The name of a language in itself: "Français", "English".</summary>
    private static string LanguageName(CultureInfo language) =>
        language.Name.Length == 0 ? "English" : Capitalize(language.NativeName, language);

    private static string Capitalize(string text, CultureInfo culture) =>
        text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
}
