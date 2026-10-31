using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Notifications (ADR 0216 §2): turns "Do not disturb" on or off. While it is on, a new
/// notification shows no bubble and plays no sound, but still goes to the clock panel.
/// </summary>
/// <remarks>
/// The switch is the <c>doNotDisturb</c> key of <c>~/.config/hackeros/notifications.config</c>,
/// written through <see cref="HackSettings.OpenSystemAsync(IAppExecutionContext, string, CancellationToken)"/>.
/// The shell's <c>DoNotDisturbService</c> watches the file, so the change applies at once, and the
/// clock panel's switch shows it. Uses only the public SDK.
/// </remarks>
public sealed partial class NotificationsPanel
{
    /// <summary>OS document of the notification settings.</summary>
    internal const string DocumentId = "notifications";

    /// <summary>Key of the "Do not disturb" mode.</summary>
    internal const string DoNotDisturbKey = "doNotDisturb";

    private const string DocumentFileName = DocumentId + ".config";

    private HackSettingsDocument? _document;
    private bool _doNotDisturb;
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets whether "Do not disturb" is on (all layers merged).</summary>
    internal bool DoNotDisturb => _doNotDisturb;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>Gets whether <see cref="Message"/> reports a failure.</summary>
    internal bool MessageIsError => _messageIsError;

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    /// <summary>Opens the document and reads the switch.</summary>
    internal async Task LoadAsync()
    {
        try
        {
            _document = await HackSettings.OpenSystemAsync(Context, DocumentId, Context.CancellationToken);
            _document.Changed += OnDocumentChanged;
            ReadDocument();
            _loaded = true;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ShowError(L["Notifications.Error.CannotRead", DocumentFileName, exception.Message]);
        }
    }

    /// <summary>Turns "Do not disturb" on or off for the user.</summary>
    /// <param name="enabled">The new state.</param>
    internal async Task SetDoNotDisturbAsync(bool enabled)
    {
        if (_isBusy || _document is null)
        {
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            await _document.SetAsync(DoNotDisturbKey, enabled, SettingLayer.User, CancellationToken.None);
            ReadDocument();
            _message = enabled ? L["Notifications.Saved.On"] : L["Notifications.Saved.Off"];
            _messageIsError = false;
        }
        catch (HackSettingLockedException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowError(L["Notifications.Error.CannotSave", "~/.config/hackeros/" + DocumentFileName, exception.Message]);
        }
        finally
        {
            _isBusy = false;
        }

        // A click re-renders by itself; a direct call (a test) must ask for it.
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

    private Task OnDoNotDisturbChanged(ChangeEventArgs args) =>
        SetDoNotDisturbAsync(args.Value is true || (args.Value is string text && bool.TryParse(text, out bool value) && value));

    private void ReadDocument()
    {
        if (_document is not null)
        {
            _doNotDisturb = _document.Get<bool>(DoNotDisturbKey);
        }
    }

    /// <summary>The clock panel's switch or an external edit (<c>nano</c>) changed the file.</summary>
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

    private void ShowError(string message)
    {
        _message = message;
        _messageIsError = true;
    }
}
