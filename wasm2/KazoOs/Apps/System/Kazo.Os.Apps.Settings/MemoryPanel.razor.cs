using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Memory (ADR 0126): turns the OOM killer on or off and sets the share of
/// <c>MemTotal</c> it keeps free, in <c>/etc/hackeros/memory.config</c>.
/// </summary>
/// <remarks>
/// <para>
/// The document is system-only (<c>system:system</c>, mode <c>0644</c>): the write goes to the system
/// layer with this app's own rights, so the OS asks an administrator to approve it (ADR 0042), exactly
/// like <c>sudo nano /etc/hackeros/memory.config</c>. The OS re-reads the file at once.
/// </para>
/// <para>
/// The readout comes from <c>/proc/meminfo</c> and <c>/proc/vmstat</c> through <see cref="HackFile"/>,
/// the same files <c>free</c> and <c>vmstat</c> read: the panel uses the public SDK only. Memory counts as
/// measured when <c>/proc/meminfo</c> carries the measured lines (<c>AnonPages</c>, ADR 0068); the OOM
/// killer never acts on simulated figures.
/// </para>
/// </remarks>
public sealed partial class MemoryPanel
{
    /// <summary>OS document of the memory settings.</summary>
    internal const string DocumentId = "memory";

    /// <summary>Qualified key of the OOM killer's switch.</summary>
    internal const string EnabledKey = "oom-killer.enabled";

    /// <summary>Qualified key of the threshold.</summary>
    internal const string MinFreePercentKey = "oom-killer.min-free-percent";

    /// <summary>The file the readout's memory figures come from.</summary>
    internal const string MemInfoPath = "/proc/meminfo";

    /// <summary>The file the readout's kill counter comes from.</summary>
    internal const string VmStatPath = "/proc/vmstat";

    private const string DocumentFileName = DocumentId + ".config";

    /// <summary>The two positions of the switch, in the order shown.</summary>
    private static readonly bool[] Switch = [true, false];

    /// <summary>The thresholds offered; another value set by hand in the file is shown as well.</summary>
    internal static readonly int[] StandardThresholds = [1, 2, 5, 10, 15, 20];

    private HackSettingsDocument? _document;
    private MemoryReadout _readout = MemoryReadout.Empty;
    private bool _enabled;
    private int _minFreePercent = 5;
    private string? _message;
    private bool _messageIsError;
    private bool _isBusy;
    private bool _loaded;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets whether the file turns the OOM killer on.</summary>
    internal bool Enabled => _enabled;

    /// <summary>Gets the threshold of the file.</summary>
    internal int MinFreePercent => _minFreePercent;

    /// <summary>Gets the figures read from <c>/proc</c>.</summary>
    internal MemoryReadout Readout => _readout;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>Gets whether the last outcome is an error.</summary>
    internal bool MessageIsError => _messageIsError;

    /// <summary>The thresholds shown: the standard ones plus the file's own when it is another.</summary>
    private IEnumerable<int> ThresholdChoices => StandardThresholds.Contains(_minFreePercent)
        ? StandardThresholds
        : StandardThresholds.Append(_minFreePercent).Order();

    /// <inheritdoc />
    protected override async Task OnInitializedAsync() => await LoadAsync();

    /// <summary>Opens <c>memory.config</c> and reads <c>/proc</c>.</summary>
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
            ShowError(L["Memory.Error.CannotRead", DocumentFileName, exception.Message]);
        }

        await RefreshReadoutAsync();
    }

    /// <summary>
    /// Writes both keys to the system layer in one edit (one administrator approval) and reports the
    /// outcome.
    /// </summary>
    /// <param name="enabled">The new switch.</param>
    /// <param name="minFreePercent">The new threshold.</param>
    internal async Task SaveAsync(bool enabled, int minFreePercent)
    {
        if (_isBusy || _document is null || (enabled == _enabled && minFreePercent == _minFreePercent))
        {
            return;
        }

        _isBusy = true;
        _message = null;
        try
        {
            // /etc/hackeros belongs to root: a denial raises the administrator approval of ADR 0042.
            await _document.SetManyAsync(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [EnabledKey] = enabled,
                    [MinFreePercentKey] = minFreePercent
                },
                SettingLayer.System,
                CancellationToken.None);
            ReadDocument();
            ShowSaved(L["Memory.Saved"]);
        }
        catch (HackSettingLockedException exception)
        {
            ShowError(exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            ShowError(L["Memory.Error.AdminRequired"]);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            ShowError(L["Memory.Error.CannotSave", "/etc/hackeros/" + DocumentFileName, exception.Message]);
        }
        finally
        {
            _isBusy = false;
        }

        await RefreshReadoutAsync();
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

    /// <summary>Reads the figures of <c>/proc/meminfo</c> and <c>/proc/vmstat</c>; an unreadable file reads as zeros.</summary>
    private async Task RefreshReadoutAsync()
    {
        string meminfo = await ReadOrEmptyAsync(MemInfoPath);
        string vmstat = await ReadOrEmptyAsync(VmStatPath);
        _readout = MemoryReadout.Parse(meminfo, vmstat);
    }

    private async Task<string> ReadOrEmptyAsync(string path)
    {
        try
        {
            return await HackFile.ReadAllTextAsync(path, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private void ReadDocument()
    {
        if (_document is null)
        {
            return;
        }

        _enabled = _document.Get<bool>(EnabledKey);
        _minFreePercent = _document.Get<int>(MinFreePercentKey);
    }

    /// <summary>An external edit (<c>sudo nano /etc/hackeros/memory.config</c>) changed the file.</summary>
    private void OnDocumentChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(async () =>
        {
            if (!_isBusy)
            {
                ReadDocument();
                await RefreshReadoutAsync();
            }

            StateHasChanged();
        });

    private SettingLayer SourceOf(string key) => _document?.GetSource(key) ?? SettingLayer.Default;

    private static string OptionClass(bool selected) => selected ? "mem-option is-selected" : "mem-option";

    private string Mebibytes(long kilobytes) =>
        string.Format(Context.FormatCulture, "{0:N0} MiB", kilobytes / 1024);

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

/// <summary>The figures of Settings &gt; Memory, parsed from <c>/proc/meminfo</c> and <c>/proc/vmstat</c>.</summary>
/// <param name="TotalKb"><c>MemTotal</c>, in KiB.</param>
/// <param name="AvailableKb"><c>MemAvailable</c>, in KiB.</param>
/// <param name="Measured">Whether memory is measured (<c>AnonPages</c> is present).</param>
/// <param name="OomKills">The <c>oom_kill</c> counter.</param>
internal sealed record MemoryReadout(long TotalKb, long AvailableKb, bool Measured, long OomKills)
{
    /// <summary>The readout before the files are read.</summary>
    public static MemoryReadout Empty { get; } = new(0, 0, false, 0);

    /// <summary>Parses the two files; a missing line reads as <c>0</c>.</summary>
    /// <param name="meminfo">Content of <c>/proc/meminfo</c> (<c>Key:   value kB</c> lines).</param>
    /// <param name="vmstat">Content of <c>/proc/vmstat</c> (<c>key value</c> lines).</param>
    public static MemoryReadout Parse(string meminfo, string vmstat)
    {
        Dictionary<string, long> memory = ParseLines(meminfo, ':');
        Dictionary<string, long> counters = ParseLines(vmstat, ' ');
        return new MemoryReadout(
            memory.GetValueOrDefault("MemTotal"),
            memory.GetValueOrDefault("MemAvailable"),
            memory.ContainsKey("AnonPages"),
            counters.GetValueOrDefault("oom_kill"));
    }

    private static Dictionary<string, long> ParseLines(string content, char separator)
    {
        Dictionary<string, long> values = new(StringComparer.Ordinal);
        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int index = line.IndexOf(separator);
            if (index <= 0)
            {
                continue;
            }

            string number = line[(index + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                values[line[..index].Trim()] = value;
            }
        }

        return values;
    }
}
