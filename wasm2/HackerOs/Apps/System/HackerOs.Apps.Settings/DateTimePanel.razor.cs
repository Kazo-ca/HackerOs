using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Date and time (ADR 0193 section 7): the current time and the user's time zone.
/// </summary>
/// <remarks>
/// <para>
/// The choice is the <c>timeZone</c> key of <c>~/.config/hackeros/datetime.config</c>, written through
/// <see cref="HackSettings.OpenSystemAsync(IAppExecutionContext, string, CancellationToken)"/>:
/// <c>auto</c> (the device's zone, as the browser reports it) or an IANA identifier from
/// <see cref="HackTimeZone.Available"/>. The shell re-reads the file and the taskbar clock follows
/// at once. The machine's default is <c>/etc/hackeros/datetime.config</c>, an administrator's file.
/// </para>
/// <para>
/// The readout shows the zone the choice resolves to, refreshed every second from the OS clock.
/// Uses only the public SDK.
/// </para>
/// </remarks>
public sealed partial class DateTimePanel
{
    /// <summary>OS document of the date and time settings.</summary>
    internal const string DocumentId = "datetime";

    /// <summary>Key of the time zone.</summary>
    internal const string TimeZoneKey = "timeZone";

    private const string DocumentFileName = DocumentId + ".config";

    private HackSettingsDocument? _document;
    private IDisposable? _tick;
    private string _setting = HackTimeZone.Automatic;
    private string _filter = string.Empty;
    private bool _listOpen;
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the <c>timeZone</c> value of the file (all layers merged): <c>auto</c> or an IANA identifier.</summary>
    internal string Setting => _setting;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>Gets whether <see cref="Message"/> reports a failure.</summary>
    internal bool MessageIsError => _messageIsError;

    /// <summary>Gets the zone the setting resolves to (the device's zone for <c>auto</c> or an unknown name).</summary>
    internal TimeZoneInfo EffectiveZone => HackTimeZone.Resolve(_setting);

    /// <summary>Gets the zones of the list that match the search text (identifier or display name).</summary>
    internal IEnumerable<TimeZoneInfo> FilteredZones => HackTimeZone.Available.Where(Matches);

    private bool IsAutomatic => !HackTimeZone.TryFind(_setting, out _);

    private DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(Context.Clock.UtcNow, EffectiveZone);

    private SettingLayer Source => _document?.GetSource(TimeZoneKey) ?? SettingLayer.Default;

    private bool IsLocked => _document?.IsLocked(TimeZoneKey) == true;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        ScheduleTick();
    }

    /// <summary>Opens the document and reads the user's choice.</summary>
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
            ShowError(L["DateTime.Error.CannotRead", DocumentFileName, exception.Message]);
        }
    }

    /// <summary>Writes the time zone to the user layer and reports the outcome.</summary>
    /// <param name="value"><c>auto</c> or an IANA identifier of <see cref="HackTimeZone.Available"/>.</param>
    internal async Task SetAsync(string value)
    {
        if (_isBusy || _document is null)
        {
            return;
        }

        if (value != HackTimeZone.Automatic && !HackTimeZone.TryFind(value, out _))
        {
            ShowError(L["DateTime.Error.Unknown", value]);
            StateHasChanged();
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            await _document.SetAsync(TimeZoneKey, value, SettingLayer.User, CancellationToken.None);
            ReadDocument();
            _listOpen = false;
            ShowSaved(value == HackTimeZone.Automatic
                ? L["DateTime.Saved.Automatic", Describe(HackTimeZone.Device)]
                : L["DateTime.Saved.Zone", Describe(EffectiveZone)]);
        }
        catch (HackSettingLockedException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowError(L["DateTime.Error.CannotSave", "~/.config/hackeros/" + DocumentFileName, exception.Message]);
        }
        finally
        {
            _isBusy = false;
        }

        // A click re-renders by itself; a direct call (a test) must ask for it.
        StateHasChanged();
    }

    /// <summary>Sets the search text of the list.</summary>
    /// <param name="filter">Part of an identifier (<c>toronto</c>, <c>Europe/</c>) or of a display name.</param>
    internal void SetFilter(string? filter) => _filter = filter?.Trim() ?? string.Empty;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _tick?.Dispose();
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            await _document.DisposeAsync();
        }
    }

    // "Choose from the list" only opens the list: nothing is written until a zone is picked.
    private Task ChooseListAsync()
    {
        _listOpen = true;
        return Task.CompletedTask;
    }

    private void OnFilterInput(ChangeEventArgs args) => SetFilter(args.Value as string);

    private Task OnZoneChanged(ChangeEventArgs args) =>
        args.Value is string { Length: > 0 } id ? SetAsync(id) : Task.CompletedTask;

    private bool Matches(TimeZoneInfo zone)
    {
        if (_filter.Length == 0)
        {
            return true;
        }

        string id = HackTimeZone.GetId(zone);
        return id.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || id.Replace('_', ' ').Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || zone.DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ReadDocument()
    {
        if (_document is null)
        {
            return;
        }

        string value = _document.Get<string>(TimeZoneKey);
        _setting = string.IsNullOrWhiteSpace(value) ? HackTimeZone.Automatic : value.Trim();
    }

    // The readout's clock: one render per second, from the OS clock (never a browser timer).
    private void ScheduleTick()
    {
        _tick?.Dispose();
        _tick = Context.Clock.Schedule(TimeSpan.FromSeconds(1), () =>
            _ = InvokeAsync(() =>
            {
                StateHasChanged();
                ScheduleTick();
            }));
    }

    /// <summary>An external edit (<c>nano ~/.config/hackeros/datetime.config</c>) changed the file.</summary>
    private void OnDocumentChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(() =>
        {
            if (!_isBusy)
            {
                ReadDocument();
            }

            StateHasChanged();
        });

    /// <summary>A zone as one line: "America/Toronto (UTC-04:00)".</summary>
    internal string Describe(TimeZoneInfo zone) =>
        $"{HackTimeZone.GetId(zone)} ({HackTimeZone.FormatOffset(zone, Context.Clock.UtcNow)})";

    private static string OptionClass(bool selected) => selected ? "datetime-option is-selected" : "datetime-option";

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
