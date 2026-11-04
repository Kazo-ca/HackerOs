using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Browser.DevTools;

/// <summary>
/// The developer tools of a tab (ADR 0075 §2, questionnaire 13): a Network panel (method, status,
/// type, size and duration of every request) and a Console panel (the page's <c>console.*</c>
/// output, uncaught errors and the browser's own warnings).
/// </summary>
public sealed partial class DevToolsPanel : IDisposable
{
    private enum Panel
    {
        Network,
        Console,
    }

    private Panel _tab = Panel.Console;
    private DevToolsLog? _subscribed;
    private IReadOnlyList<NetworkLogEntry> _network = [];
    private IReadOnlyList<ConsoleLogEntry> _console = [];
    private int _errors;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's labels in the browser process's language (ADR 0104); console messages are not translated.</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The log of the tab.</summary>
    [Parameter, EditorRequired]
    public DevToolsLog Log { get; set; } = default!;

    /// <summary>Closes the panel.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_subscribed, Log))
        {
            if (_subscribed is not null)
            {
                _subscribed.Changed -= OnLogChanged;
            }

            _subscribed = Log;
            Log.Changed += OnLogChanged;
        }

        Snapshot();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnLogChanged;
        }
    }

    private void OnLogChanged() => _ = InvokeAsync(() =>
    {
        Snapshot();
        StateHasChanged();
    });

    private void Snapshot()
    {
        _network = Log.Network;
        _console = Log.Console;
        _errors = Log.ErrorCount;
    }

    /// <summary>The "Name" column: last path segment and query, as Chromium shows it.</summary>
    public static string ShortName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return url;
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        string name = path.Length == 0 ? uri.Host : path[(path.LastIndexOf('/') + 1)..];
        return Uri.UnescapeDataString(name) + uri.Query;
    }

    private static string KindLabel(NetworkLogEntry entry) => entry.Kind switch
    {
        NetworkRequestKind.Document => "document",
        NetworkRequestKind.Stylesheet => "stylesheet",
        NetworkRequestKind.Script => "script",
        NetworkRequestKind.Image => "img",
        NetworkRequestKind.Font => "font",
        NetworkRequestKind.Media => "media",
        NetworkRequestKind.Fetch => "fetch",
        NetworkRequestKind.Xhr => "xhr",
        _ => "other",
    };

    private static string FormatSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0 'kB'", CultureInfo.InvariantCulture),
        _ => (bytes / (1024.0 * 1024)).ToString("0.0 'MB'", CultureInfo.InvariantCulture),
    };

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMilliseconds < 1000
            ? $"{Math.Round(duration.TotalMilliseconds)} ms"
            : duration.TotalSeconds.ToString("0.00 's'", CultureInfo.InvariantCulture);
}
