using System.Globalization;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Screens (ADR 0179 §7, ADR 0180): chooses what the taskbar of screen 1 lists
/// (<c>primary-taskbar</c>) and shows the secondary screens <c>screens.config</c> remembers, with a
/// way to forget the place of a closed one.
/// </summary>
/// <remarks>
/// <para>
/// ADR 0184: on a host with screens, <see cref="HackScreens"/> lists the open screens and opens,
/// restores, closes, identifies them, puts the screen of this window in full screen and asks for the
/// <c>window-management</c> permission (capability <c>screens.manage</c>). The browser accepts a new
/// window, a full screen or its prompt only during the click, so each button calls it straight away.
/// </para>
/// <para>
/// Both choices go to the user layer, <c>~/.config/hackeros/screens.config</c>, through
/// <see cref="HackSettings.OpenSystemAsync(IAppExecutionContext, string, CancellationToken)"/>: the
/// screen service follows <c>primary-taskbar</c> at once. The panel uses the public SDK only; moving a
/// window to another screen stays in the window menus.
/// </para>
/// <para>
/// A screen listed in <c>open</c> is open, or offered at the next sign-in; the screen service owns that
/// line, so only the place of a closed screen can be forgotten.
/// </para>
/// </remarks>
public sealed partial class ScreensPanel
{
    /// <summary>OS document of the screens settings.</summary>
    internal const string DocumentId = "screens";

    /// <summary>Qualified key of what the taskbar of screen 1 lists.</summary>
    internal const string PrimaryTaskbarKey = "taskbar.primary-taskbar";

    /// <summary>Qualified key of the screens open in the last session.</summary>
    internal const string OpenKey = "screens.open";

    /// <summary><c>primary-taskbar</c> value: every window of every screen (the default).</summary>
    internal const string PrimaryTaskbarAll = "all";

    /// <summary><c>primary-taskbar</c> value: only the windows of screen 1.</summary>
    internal const string PrimaryTaskbarOwn = "own";

    /// <summary>Highest secondary screen number the file remembers.</summary>
    internal const int MaxScreenNumber = 9;

    /// <summary>Where the user layer lives, as shown to the user.</summary>
    internal const string UserFilePath = "~/.config/hackeros/screens.config";

    private static readonly string[] TaskbarModes = [PrimaryTaskbarAll, PrimaryTaskbarOwn];

    private HackSettingsDocument? _document;
    private HackScreensState? _live;
    private IAsyncDisposable? _liveWatch;
    private string _primaryTaskbar = PrimaryTaskbarAll;
    private IReadOnlyList<RememberedScreen> _screens = [];
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the <c>primary-taskbar</c> value in effect: <c>all</c> or <c>own</c>.</summary>
    internal string PrimaryTaskbar => _primaryTaskbar;

    /// <summary>Gets the secondary screens the file remembers, by number.</summary>
    internal IReadOnlyList<RememberedScreen> Screens => _screens;

    /// <summary>Gets the open screens, or <see langword="null"/> on a host without screens.</summary>
    internal HackScreensState? Live => _live;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>Gets whether the last outcome is an error.</summary>
    internal bool MessageIsError => _messageIsError;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync() => await LoadAsync();

    /// <summary>Opens <c>screens.config</c>.</summary>
    internal async Task LoadAsync()
    {
        try
        {
            _document = await HackSettings.OpenSystemAsync(Context, DocumentId, Context.CancellationToken);
            _document.Changed += OnDocumentChanged;
            ReadDocument();
            _loaded = true;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(L["Screens.Error.CannotRead", exception.Message]);
        }

        await StartLiveAsync();
    }

    /// <summary>Opens a secondary screen. Called by the button's click: the browser needs that gesture.</summary>
    internal async Task OpenScreenAsync()
    {
        HackScreenOpenStatus status = await RunAsync(() => HackScreens.OpenScreenAsync(Context, CancellationToken.None), HackScreenOpenStatus.Unavailable);
        switch (status)
        {
            case HackScreenOpenStatus.Blocked:
                ShowError(L["Screens.Open.Blocked"]);
                break;
            case HackScreenOpenStatus.LimitReached:
                ShowError(L["Screens.Open.LimitReached"]);
                break;
            case HackScreenOpenStatus.Unavailable:
                ShowError(L["Screens.Open.Unavailable"]);
                break;
        }
    }

    /// <summary>Reopens the screens of the last session. Called by the button's click.</summary>
    internal async Task RestoreScreensAsync()
    {
        int opened = await RunAsync(() => HackScreens.RestoreScreensAsync(Context, CancellationToken.None), 0);
        if (opened == 0 && _message is null)
        {
            ShowError(L["Screens.Open.Blocked"]);
        }
    }

    /// <summary>Closes a secondary screen; its windows come back to screen 1.</summary>
    /// <param name="number">The screen, 2 to 9.</param>
    internal Task CloseScreenAsync(int number) =>
        RunAsync(async () =>
        {
            await HackScreens.CloseScreenAsync(Context, number, CancellationToken.None);
            return true;
        }, false);

    /// <summary>Shows each screen's number for a few seconds.</summary>
    internal Task IdentifyAsync() =>
        RunAsync(async () =>
        {
            await HackScreens.IdentifyAsync(Context, CancellationToken.None);
            return true;
        }, false);

    /// <summary>Puts the screen of this window in full screen, or takes it out. Called by the button's click.</summary>
    internal async Task ToggleFullScreenAsync()
    {
        if (!await RunAsync(() => HackScreens.ToggleFullScreenAsync(Context, CancellationToken.None), true))
        {
            ShowError(L["Screens.Open.FullScreenRefused"]);
        }
    }

    /// <summary>Asks the browser for the window management permission. Called by the button's click.</summary>
    internal Task RequestPermissionAsync() =>
        RunAsync(() => HackScreens.RequestPermissionAsync(Context, CancellationToken.None), HackScreenPermission.Unknown);

    /// <summary>Reads the open screens and follows them, on a host that has some.</summary>
    private async Task StartLiveAsync()
    {
        if (!HackScreens.IsSupportedFor(Context))
        {
            return;
        }

        try
        {
            _live = HackScreens.GetState(Context);
            _liveWatch = await HackScreens.WatchAsync(Context, OnLiveChangedAsync, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is HackUnauthorizedAccessException or NotSupportedException)
        {
            // Without screens.manage, or once the screens went away, the page keeps the file part.
            _live = null;
        }
    }

    /// <summary>Runs one screen request, shows a refusal, then reads the screens again.</summary>
    private async Task<T> RunAsync<T>(Func<Task<T>> request, T fallback)
    {
        if (_isBusy || _live is null)
        {
            return fallback;
        }

        _isBusy = true;
        _message = null;
        T result = fallback;
        try
        {
            result = await request();
        }
        catch (Exception exception) when (exception is HackUnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            ShowError(L["Screens.Error.Request", exception.Message]);
        }
        finally
        {
            _isBusy = false;
        }

        RefreshLive();
        StateHasChanged();
        return result;
    }

    private ValueTask OnLiveChangedAsync() =>
        new(InvokeAsync(() =>
        {
            RefreshLive();
            StateHasChanged();
        }));

    private void RefreshLive()
    {
        try
        {
            _live = _live is null ? null : HackScreens.GetState(Context);
        }
        catch (Exception exception) when (exception is HackUnauthorizedAccessException or NotSupportedException)
        {
            _live = null;
        }
    }

    /// <summary>Writes <c>primary-taskbar</c> to the user layer.</summary>
    /// <param name="mode"><c>all</c> or <c>own</c>.</param>
    internal Task SetPrimaryTaskbarAsync(string mode)
    {
        if (!TaskbarModes.Contains(mode) || mode == _primaryTaskbar)
        {
            return Task.CompletedTask;
        }

        return EditAsync(document => document.SetAsync(PrimaryTaskbarKey, mode, SettingLayer.User, CancellationToken.None));
    }

    /// <summary>Removes the remembered place of a closed screen from the user layer.</summary>
    /// <param name="number">Screen number, 2 to <see cref="MaxScreenNumber"/>.</param>
    internal Task ForgetAsync(int number)
    {
        RememberedScreen? screen = _screens.FirstOrDefault(candidate => candidate.Number == number);
        if (screen is not { CanForget: true })
        {
            return Task.CompletedTask;
        }

        return EditAsync(document => document.ResetAsync(GeometryKey(number), SettingLayer.User, CancellationToken.None));
    }

    /// <summary>Reads the remembered screens of a <c>[screens]</c> section: every number listed in <c>open</c> or with a valid place.</summary>
    /// <param name="open">The value of <c>open</c>.</param>
    /// <param name="geometry">The value of the line of a screen number, or <see langword="null"/>.</param>
    internal static IReadOnlyList<RememberedScreen> ReadScreens(string? open, Func<int, string?> geometry)
    {
        HashSet<int> openNumbers = [];
        foreach (string part in (open ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number is >= 2 and <= MaxScreenNumber)
            {
                openNumbers.Add(number);
            }
        }

        List<RememberedScreen> screens = [];
        for (int number = 2; number <= MaxScreenNumber; number++)
        {
            RememberedPlace? place = RememberedPlace.Parse(geometry(number));
            if (place is not null || openNumbers.Contains(number))
            {
                screens.Add(new RememberedScreen(number, openNumbers.Contains(number), place));
            }
        }

        return screens;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_liveWatch is not null)
        {
            await _liveWatch.DisposeAsync();
        }

        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            await _document.DisposeAsync();
        }
    }

    private async Task EditAsync(Func<HackSettingsDocument, ValueTask> edit)
    {
        if (_isBusy || _document is null)
        {
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            await edit(_document);
            ReadDocument();
            ShowSaved(L["Screens.Saved"]);
        }
        catch (HackSettingLockedException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowError(L["Screens.Error.CannotSave", UserFilePath, exception.Message]);
        }
        finally
        {
            _isBusy = false;
        }

        StateHasChanged();
    }

    private void ReadDocument()
    {
        if (_document is null)
        {
            return;
        }

        string mode = _document.Get<string>(PrimaryTaskbarKey)?.Trim() ?? string.Empty;
        _primaryTaskbar = string.Equals(mode, PrimaryTaskbarOwn, StringComparison.OrdinalIgnoreCase) ? PrimaryTaskbarOwn : PrimaryTaskbarAll;
        HackSettingsDocument document = _document;
        _screens = ReadScreens(document.Get<string>(OpenKey), number => document.Get<string>(GeometryKey(number)));
    }

    /// <summary>The screen service, another Settings window or an editor changed the file.</summary>
    private void OnDocumentChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(() =>
        {
            if (!_isBusy)
            {
                ReadDocument();
            }

            StateHasChanged();
        });

    private static string GeometryKey(int number) => string.Create(CultureInfo.InvariantCulture, $"screens.{number}");

    private SettingLayer SourceOf(string key) => _document?.GetSource(key) ?? SettingLayer.Default;

    private bool IsLocked(string key) => _document?.IsLocked(key) ?? false;

    private string PlaceText(RememberedScreen screen) => screen.Place is { } place
        ? L["Screens.List.Place", place.Width, place.Height, place.Left, place.Top]
        : L["Screens.List.NoPlace"];

    private string MonitorText(HackScreenInfo screen) => screen.Number == 1
        ? L["Screens.List.Primary"]
        : screen.MonitorName ?? L["Screens.Open.NoMonitorName"];

    private static bool CallerIsFullScreen(HackScreensState live) =>
        live.Screens.FirstOrDefault(screen => screen.Number == live.CallerScreen)?.IsFullScreen == true;

    private string PermissionText(HackScreenPermission permission) => permission switch
    {
        HackScreenPermission.Granted => L["Screens.Permission.Granted"],
        HackScreenPermission.Denied => L["Screens.Permission.Denied"],
        HackScreenPermission.Prompt => L["Screens.Permission.Prompt"],
        HackScreenPermission.Unsupported => L["Screens.Permission.Unsupported"],
        _ => L["Screens.Permission.Unknown"],
    };

    private static string OptionClass(bool selected) => selected ? "scr-option is-selected" : "scr-option";

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

/// <summary>A secondary screen <c>screens.config</c> remembers.</summary>
/// <param name="Number">Screen number, 2 to 9.</param>
/// <param name="IsOpen">Whether <c>open</c> lists it: open now, or offered at the next sign-in.</param>
/// <param name="Place">Where its window was, or <see langword="null"/> when not remembered yet.</param>
internal sealed record RememberedScreen(int Number, bool IsOpen, RememberedPlace? Place)
{
    /// <summary>Gets whether the user may forget its place: the screen is closed and has one.</summary>
    public bool CanForget => !IsOpen && Place is not null;
}

/// <summary>A remembered window place, <c>left,top,width,height</c> in CSS pixels (ADR 0180 §3).</summary>
internal sealed record RememberedPlace(int Left, int Top, int Width, int Height)
{
    /// <summary>Reads <c>left,top,width,height</c>; <see langword="null"/> for anything else or a window smaller than 200 × 150, as the OS does.</summary>
    /// <param name="text">The value of a numbered <c>[screens]</c> line.</param>
    public static RememberedPlace? Parse(string? text)
    {
        string[] parts = (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        int[] values = new int[4];
        for (int index = 0; index < 4; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[index]))
            {
                return null;
            }
        }

        return values[2] >= 200 && values[3] >= 150 ? new RememberedPlace(values[0], values[1], values[2], values[3]) : null;
    }
}
