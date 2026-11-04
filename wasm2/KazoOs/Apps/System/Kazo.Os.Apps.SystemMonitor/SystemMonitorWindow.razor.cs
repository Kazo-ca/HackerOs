using System.Globalization;
using System.Text;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Apps.SystemMonitor.Metrics;
using Kazo.Os.Apps.SystemMonitor.Processes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Os.Apps.SystemMonitor;

/// <summary>
/// The task manager (ADR 0058, section 2): processes, performance, disk, network and services, all
/// read from <c>/proc</c> through the public SDK (ADR 0055, 0056, 0057), like <c>top</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refresh.</b> One reading per second (<see cref="RefreshInterval"/>), suspended while the
/// window is minimized or the browser tab is hidden: the component's own JavaScript module watches
/// the window's visibility, the only thing C# cannot see. Rates are differences between two
/// readings (<see cref="SystemMetrics"/>).
/// </para>
/// <para>
/// <b>End task.</b> The selection is confirmed in a dialog that lists the processes, then each one
/// is killed through <see cref="HackProcess.KillAsync"/>. A refusal (<c>process.manage</c> missing
/// for one process) is reported per process and does not stop the others.
/// </para>
/// </remarks>
public partial class SystemMonitorWindow
{
    /// <summary>Time between two readings of <c>/proc</c>, like <c>top</c>'s default delay.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Readings between two reads of the drives' space. Computing the used space reads the
    /// IndexedDB metadata, which <c>/proc/diskstats</c> counts: polling it every second would make
    /// the task manager the main reader of the disk it is watching.
    /// </summary>
    public const int DriveRefreshEvery = 10;

    private readonly ProcessTable _table = new();
    private readonly SystemMetrics _metrics = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IReadOnlyDictionary<long, HackProcess> _processes = new Dictionary<long, HackProcess>();
    private IReadOnlyList<ProcCpuInfoEntry> _cpuInfo = [];
    private IReadOnlyList<HackDriveInfo> _drives = [];
    private EndTaskReport? _endTaskReport;
    private ElementReference _root;
    private IJSObjectReference? _module;
    private DotNetObjectReference<SystemMonitorWindow>? _selfReference;
    private Task? _refreshLoop;
    private bool _paused;
    private bool _windowVisible = true;
    private string? _error;

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    private bool IsLive => !_paused && _windowVisible;

    private string StatusClass => IsLive ? "live" : "paused";

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>System Monitor's texts in the language of its own process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    private string StatusText => _paused ? L["Status.Paused"] : _windowVisible ? L["Status.Live"] : L["Status.Idle"];

    private string StatusTitle => _paused
        ? L["Status.Title.Paused"]
        : _windowVisible ? L["Status.Title.Live"] : L["Status.Title.Hidden"];

    /// <summary>Called by the JavaScript module when the window is minimized, restored, or the tab is hidden or shown.</summary>
    /// <param name="visible">Whether the window can be seen.</param>
    [JSInvokable]
    public async Task SetWindowVisible(bool visible)
    {
        // A JavaScript callback runs outside the app's ambient scope (ADR 0043): enter it first, so
        // this work is the app's, not the compositor's (ADR 0067).
        using IDisposable scope = HackExecutionContext.Enter(AppContext);
        bool resumed = visible && !_windowVisible;
        _windowVisible = visible;
        if (!resumed)
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Refresh at once rather than showing a reading from before the window was minimized.
        await RefreshAndRenderAsync();
    }

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        // Runs inside the app's ambient scope (WindowAppBase): the loop inherits it.
        _refreshLoop = RunRefreshLoopAsync(_lifetime.Token);
        _cpuInfo = await ProcReader.ReadCpuInfoAsync(_lifetime.Token);
        await WatchVisibilityAsync();
    }

    private async Task WatchVisibilityAsync()
    {
        try
        {
            _module = await JavaScript.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Kazo.Os.Apps.SystemMonitor/SystemMonitorWindow.razor.js");
            _selfReference = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("watchVisibility", _root, _selfReference);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or NotSupportedException)
        {
            // No JavaScript (headless renderer): the refresh simply never pauses by itself.
        }
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, AppContext.CancellationToken);
            using PeriodicTimer timer = new(RefreshInterval);
            await RefreshAndRenderAsync();
            while (await timer.WaitForNextTickAsync(linked.Token))
            {
                if (IsLive)
                {
                    await RefreshAndRenderAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed or process stopped.
        }
    }

    private async Task RefreshAndRenderAsync()
    {
        await RefreshAsync();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Reads <c>/proc</c> once and updates every tab's model. Concurrent calls are serialized.</summary>
    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return; // A reading is already in flight; the next tick catches up.
        }

        try
        {
            ProcRefresh refresh = await ProcReader.ReadAsync(_lifetime.Token);
            _metrics.Apply(refresh.Metrics);
            _processes = refresh.Processes.ToDictionary(static process => process.Id);
            _table.SetRows(refresh.Processes.Select(process => ToRow(process, _metrics.RateOf(process.Id))));
            if (_drives.Count == 0 || _metrics.ReadingCount % DriveRefreshEvery == 1)
            {
                _drives = await ProcReader.ReadDrivesAsync(_lifetime.Token);
            }

            _error = null;
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception exception) when (exception is HackIOException or HackUnauthorizedAccessException or InvalidOperationException)
        {
            _error = L["Error.CannotReadProc", exception.Message];
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private Task RefreshNowAsync() => RefreshAsync();

    private void TogglePause() => _paused = !_paused;

    private void DismissEndTaskReport() => _endTaskReport = null;

    /// <summary>
    /// "End task" (ADR 0069, section 4): confirms, then asks every selected process to stop
    /// (<c>SIGTERM</c>, forced after 5 s); refusals are collected, not fatal.
    /// </summary>
    private Task EndTasksAsync(IReadOnlyList<ProcessRow> rows) => EndAsync(rows, EndTaskMode.Terminate);

    /// <summary>"Force kill" (ADR 0069, section 4): confirms, then kills every selected process (<c>SIGKILL</c>).</summary>
    private Task ForceKillAsync(IReadOnlyList<ProcessRow> rows) => EndAsync(rows, EndTaskMode.ForceKill);

    private async Task EndAsync(IReadOnlyList<ProcessRow> rows, EndTaskMode mode)
    {
        if (rows.Count == 0)
        {
            return;
        }

        bool force = mode == EndTaskMode.ForceKill;
        StringBuilder content = new();
        content.Append((rows.Count, force) switch
        {
            (1, false) => L["EndTask.Confirm.One"].Value,
            (_, false) => L["EndTask.Confirm.Many", rows.Count].Value,
            (1, true) => L["ForceKill.Confirm.One"].Value,
            _ => L["ForceKill.Confirm.Many", rows.Count].Value
        });
        content.Append("\n\n");
        foreach (ProcessRow row in rows)
        {
            content.Append(CultureInfo.InvariantCulture, $"  {row.Pid,6}  {row.Name}  ({row.AppId})\n");
        }

        MessageBoxDialogResult answer = await MessageBox(force ? L["ForceKill.Title"] : L["EndTask.Title"], content.ToString(), MessageBoxType.YesNo);
        if (answer.Result != MessageBoxResult.Yes)
        {
            return;
        }

        _endTaskReport = await EndTaskRunner.EndAsync(
            rows,
            row => _processes.TryGetValue(row.Pid, out HackProcess? process)
                ? EndTaskRunner.End(process, mode, _lifetime.Token)
                : Task.CompletedTask);
        _table.ClearSelection();
        await RefreshAsync();
    }

    private static ProcessRow ToRow(HackProcess process, ProcessIoRate rate) => new(
        process.Id,
        process.ParentId,
        process.ProcessName,
        process.AppId,
        process.UserName,
        process.State.ToString(),
        process.StateCode,
        process.CpuUsage,
        process.WorkingSet64,
        rate.ReadPerSecond,
        rate.WritePerSecond,
        CpuEstimated: !IsMeasured(process, "CpuSource"),
        MemoryEstimated: !IsMeasured(process, "MemSource"),
        Responding: process.Responding)
    {
        Nice = process.Nice,
    };

    /// <summary>
    /// Reads a <c>CpuSource:</c>/<c>MemSource:</c> line of <c>/proc/&lt;pid&gt;/status</c> (ADR 0066).
    /// A missing line (an OS older than phase 5) means simulated.
    /// </summary>
    private static bool IsMeasured(HackProcess process, string field) =>
        process.Status.TryGetValue(field, out string? source) && string.Equals(source, "measured", StringComparison.Ordinal);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_refreshLoop is not null)
        {
            await _refreshLoop;
        }

        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("unwatchVisibility", _root);
                await _module.DisposeAsync();
            }
            catch (Exception exception) when (exception is JSDisconnectedException or JSException or TaskCanceledException)
            {
                // The page is going away with the window.
            }
        }

        _selfReference?.Dispose();
        _lifetime.Dispose();
        _refreshGate.Dispose();
    }
}
