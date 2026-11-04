using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Accessibility;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Accessibility (ADR 0105): high contrast, text size and reduced motion.
/// </summary>
/// <remarks>
/// <para>
/// Each choice is one key of <c>~/.config/hackeros/accessibility.config</c>, written through
/// <see cref="HackSettings.OpenSystemAsync(IAppExecutionContext, string, CancellationToken)"/>; the
/// shell re-reads the file and applies the change at once, like it does for a theme. The machine's
/// default (<c>/etc/hackeros/accessibility.config</c>, also used by the sign-in screen) is an
/// administrator's file, edited with <c>sudo nano</c>.
/// </para>
/// <para>
/// "Reduce animations" is the only motion setting left (it replaces the Appearance switch):
/// <c>animationsEnabled = false</c> in <c>appearance.config</c> still counts as <c>reducedMotion = on</c>,
/// so choosing "auto" or "off" here also turns the user's own <c>animationsEnabled</c> back on.
/// The readout shows the effective state from <see cref="HackAccessibility"/>.
/// </para>
/// </remarks>
public sealed partial class AccessibilityPanel
{
    /// <summary>OS document of the accessibility preferences.</summary>
    internal const string DocumentId = "accessibility";

    /// <summary>Key of the high-contrast switch.</summary>
    internal const string HighContrastKey = "highContrast";

    /// <summary>Key of the text size factor.</summary>
    internal const string TextScaleKey = "textScale";

    /// <summary>Key of the reduced-motion switch.</summary>
    internal const string ReducedMotionKey = "reducedMotion";

    private const string AppearanceDocumentId = "appearance";
    private const string AnimationsKey = "animationsEnabled";
    private const string DocumentFileName = DocumentId + ".config";

    /// <summary>The three values of a switch, in the order shown.</summary>
    internal static readonly string[] Modes = ["auto", "on", "off"];

    /// <summary>The text sizes, as written in the file.</summary>
    internal static readonly string[] TextScaleValues = ["1.0", "1.25", "1.5", "2.0"];

    private HackSettingsDocument? _document;
    private HackSettingsDocument? _appearance;
    private HackAccessibilityPreferences _effective = HackAccessibilityPreferences.Default;
    private string _highContrast = "auto";
    private string _textScale = "1.0";
    private string _reducedMotion = "auto";
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the effective preferences shown in the readout.</summary>
    internal HackAccessibilityPreferences Effective => _effective;

    /// <summary>Gets the <c>highContrast</c> value of the file (all layers merged).</summary>
    internal string HighContrast => _highContrast;

    /// <summary>Gets the <c>textScale</c> value of the file (all layers merged).</summary>
    internal string TextScale => _textScale;

    /// <summary>Gets the <c>reducedMotion</c> value of the file (all layers merged).</summary>
    internal string ReducedMotion => _reducedMotion;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>An administrator locked <c>animationsEnabled = false</c>: animations stay off whatever is chosen here.</summary>
    private bool AnimationsLockedOff =>
        _appearance is not null && _appearance.IsLocked(AnimationsKey) && !_appearance.Get<bool>(AnimationsKey);

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        Context.Accessibility.Changed += OnEffectiveChanged;
        await LoadAsync();
    }

    /// <summary>Opens the two documents and reads the effective preferences.</summary>
    internal async Task LoadAsync()
    {
        try
        {
            _document = await HackSettings.OpenSystemAsync(Context, DocumentId, Context.CancellationToken);
            _document.Changed += OnDocumentChanged;
            ReadDocument();
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ShowError(L["Accessibility.Error.CannotRead", DocumentFileName, exception.Message]);
        }

        try
        {
            _appearance = await HackSettings.OpenSystemAsync(Context, AppearanceDocumentId, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Without appearance.config the motion choice is simply written; nothing to turn back on.
            _appearance = null;
        }

        _effective = await Context.Accessibility.GetPreferencesAsync(Context.CancellationToken);
        _loaded = _document is not null;
    }

    /// <summary>Writes one key to the user layer and reports the outcome.</summary>
    /// <param name="key">One of the three keys.</param>
    /// <param name="value">Its new value, as written in the file.</param>
    internal async Task SetAsync(string key, string value)
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
            if (key == ReducedMotionKey && value != "on")
            {
                await TurnAnimationsBackOnAsync();
            }

            ReadDocument();
            ShowSaved(L["Accessibility.Saved"]);
        }
        catch (HackSettingLockedException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowError(L["Accessibility.Error.CannotSave", "~/.config/hackeros/" + DocumentFileName, exception.Message]);
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
        if (AppContext is not null)
        {
            AppContext.Accessibility.Changed -= OnEffectiveChanged;
        }

        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            await _document.DisposeAsync();
        }

        if (_appearance is not null)
        {
            await _appearance.DisposeAsync();
        }
    }

    /// <summary>
    /// The old switch (<c>animationsEnabled = false</c>) would keep animations off after the user
    /// chose "auto" or "off": the user's own value is turned back on. An administrator's lock stays.
    /// </summary>
    private async Task TurnAnimationsBackOnAsync()
    {
        if (_appearance is null || _appearance.Get<bool>(AnimationsKey) || _appearance.IsLocked(AnimationsKey))
        {
            return;
        }

        await _appearance.SetAsync(AnimationsKey, true, SettingLayer.User, CancellationToken.None);
    }

    private void ReadDocument()
    {
        if (_document is null)
        {
            return;
        }

        _highContrast = _document.Get<string>(HighContrastKey);
        _textScale = _document.Get<string>(TextScaleKey);
        _reducedMotion = _document.Get<string>(ReducedMotionKey);
    }

    /// <summary>An external edit (<c>nano ~/.config/hackeros/accessibility.config</c>) changed the file.</summary>
    private void OnDocumentChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(() =>
        {
            if (!_isBusy)
            {
                ReadDocument();
            }

            StateHasChanged();
        });

    /// <summary>The shell resolved new effective preferences (a file, or the host's own settings).</summary>
    private void OnEffectiveChanged(object? sender, EventArgs args) =>
        _ = InvokeAsync(async () =>
        {
            _effective = await Context.Accessibility.GetPreferencesAsync(CancellationToken.None);
            StateHasChanged();
        });

    private SettingLayer SourceOf(string key) => _document?.GetSource(key) ?? SettingLayer.Default;

    private bool IsLocked(string key) => _document?.IsLocked(key) == true;

    private static string OptionClass(bool selected) => selected ? "a11y-option is-selected" : "a11y-option";

    private static string ModeTag(string mode) => mode.ToUpperInvariant();

    private string ModeLabel(string mode) => mode switch
    {
        "on" => L["Accessibility.Mode.On"],
        "off" => L["Accessibility.Mode.Off"],
        _ => L["Accessibility.Mode.Auto"]
    };

    private string OnOff(bool value) => value ? L["Accessibility.State.On"] : L["Accessibility.State.Off"];

    private string Percent(double scale) => (scale).ToString("P0", Context.FormatCulture);

    private static double ParseScale(string value) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double scale) ? scale : 1.0;

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
