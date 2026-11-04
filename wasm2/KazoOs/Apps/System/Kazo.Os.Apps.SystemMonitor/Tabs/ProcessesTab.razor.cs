using System.Globalization;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Apps.SystemMonitor.Processes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Kazo.Os.Apps.SystemMonitor.Tabs;

/// <summary>
/// The Processes tab (ADR 0058, section 2): sortable columns, a text filter, multiple selection
/// (click, Ctrl, Shift, Ctrl+A), "End task" on the selection and a detail panel (double click or
/// Enter). The state lives in <see cref="ProcessTable"/> so it survives a refresh and a tab switch.
/// A row's context menu (right click) offers Details, "End task" (<c>SIGTERM</c>) and "Force kill"
/// (<c>SIGKILL</c>, also Shift+Delete), per ADR 0069, section 4.
/// </summary>
public partial class ProcessesTab
{
    private MudMenu? _rowMenu;

    /// <summary>The columns, with the resource key of their header.</summary>
    private static readonly (ProcessColumn Column, string LabelKey, bool Numeric)[] Columns =
    [
        (ProcessColumn.Pid, "Column.Pid", true),
        (ProcessColumn.Name, "Column.Name", false),
        (ProcessColumn.User, "Column.User", false),
        (ProcessColumn.State, "Column.State", false),
        (ProcessColumn.Cpu, "Column.Cpu", true),
        (ProcessColumn.Memory, "Column.Memory", true),
        (ProcessColumn.DiskRead, "Column.DiskRead", true),
        (ProcessColumn.DiskWrite, "Column.DiskWrite", true),
        (ProcessColumn.Priority, "Column.Priority", true)
    ];

    /// <summary>The columns shown: "Priority" only once the user asked for it (ADR 0127).</summary>
    private IEnumerable<(ProcessColumn Column, string LabelKey, bool Numeric)> ShownColumns =>
        Columns.Where(column => column.Column != ProcessColumn.Priority || Table.ShowPriority);

    private void TogglePriorityColumn() => Table.ShowPriority = !Table.ShowPriority;

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The tab's texts in the language of the System Monitor process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the numbers shown (the process's locale).</summary>
    private CultureInfo Formats => AppContext?.FormatCulture ?? CultureInfo.InvariantCulture;

    /// <summary>Gets or sets the table model owned by the window.</summary>
    [Parameter, EditorRequired]
    public ProcessTable Table { get; set; } = default!;

    /// <summary>Gets or sets the processes of the last refresh, by PID, for the detail panel.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyDictionary<long, KazoProcess> Processes { get; set; } = new Dictionary<long, KazoProcess>();

    /// <summary>Gets or sets the outcome of the last "End task", or <see langword="null"/>.</summary>
    [Parameter]
    public EndTaskReport? EndTaskReport { get; set; }

    /// <summary>Gets or sets the handler that confirms and ends the given processes ("End task", <c>SIGTERM</c>).</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<ProcessRow>> OnEndTask { get; set; }

    /// <summary>Gets or sets the handler that confirms and kills the given processes ("Force kill", <c>SIGKILL</c>).</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<ProcessRow>> OnForceKill { get; set; }

    /// <summary>Gets or sets the handler that hides the "End task" report.</summary>
    [Parameter]
    public EventCallback OnDismissReport { get; set; }

    private KazoProcess? DetailProcess =>
        Table.DetailPid is { } pid && Processes.TryGetValue(pid, out KazoProcess? process) ? process : null;

    private void OnFilterInput(ChangeEventArgs e) => Table.Filter = e.Value?.ToString() ?? string.Empty;

    private Task EndSelectedAsync() => OnEndTask.InvokeAsync(Table.SelectedRows);

    private Task ForceKillSelectedAsync() => OnForceKill.InvokeAsync(Table.SelectedRows);

    /// <summary>
    /// Opens the row menu at the cursor. A right click on a row outside the selection selects that
    /// row alone first, like a file manager, so the menu always acts on what is highlighted.
    /// </summary>
    private async Task OpenRowMenuAsync(MouseEventArgs e, long pid)
    {
        if (!Table.IsSelected(pid))
        {
            Table.Click(pid, control: false, shift: false);
        }

        if (_rowMenu is not null)
        {
            await _rowMenu.OpenMenuAsync(e);
        }
    }

    private void ShowSelectedDetails()
    {
        if (Table.SelectedRows is [ProcessRow only])
        {
            OpenDetail(only.Pid);
        }
    }

    private void OpenDetail(long pid)
    {
        Table.DetailPid = pid;
        if (!Table.IsSelected(pid))
        {
            Table.Click(pid, control: false, shift: false);
        }
    }

    private void CloseDetail() => Table.DetailPid = null;

    /// <summary>Keyboard of the table: Ctrl+A, Delete (End task), Shift+Delete (Force kill), Enter, Escape.</summary>
    private async Task OnKeyDownAsync(KeyboardEventArgs e)
    {
        bool control = e.CtrlKey || e.MetaKey;
        switch (e.Key)
        {
            case "a" or "A" when control:
                Table.SelectAll();
                break;
            case "Delete" when e.ShiftKey:
                await ForceKillSelectedAsync();
                break;
            case "Delete":
                await EndSelectedAsync();
                break;
            case "Enter":
                ShowSelectedDetails();
                break;
            case "Escape":
                Table.ClearSelection();
                break;
        }
    }

    private string? AriaSort(ProcessColumn column) =>
        Table.SortColumn != column ? null : Table.SortDescending ? "descending" : "ascending";

    private string SortGlyph(ProcessColumn column) =>
        Table.SortColumn != column ? string.Empty : Table.SortDescending ? " ▼" : " ▲";

    /// <summary>
    /// Shows a login name as is, and an opaque 32-hex user id (a user the OS cannot name for this
    /// app) shortened like a short git hash.
    /// </summary>
    internal static string DisplayUser(string user) =>
        user.Length == 32 && user.All(char.IsAsciiHexDigit) ? $"uid:{user[..8]}" : user;

    private string ReportSummary(EndTaskReport report) => (report.Ended.Count, report.Failures.Count) switch
    {
        (_, 0) => report.Ended.Count == 1 ? L["Report.EndedOne"] : L["Report.EndedMany", report.Ended.Count],
        (0, _) => L["Report.Failed", report.Failures.Count],
        _ => L["Report.Mixed", report.Ended.Count, report.Failures.Count]
    };

    /// <summary>A process state (<c>Running</c>, <c>Sleeping</c>…) in the process's language; an unknown one as is.</summary>
    private string StateLabel(string state) => ProcessStateLabel(L, state);

    /// <summary>Translates a <see cref="KazoProcessState"/> name, shared with the detail panel.</summary>
    internal static string ProcessStateLabel(IStringLocalizer<Strings> text, string state)
    {
        LocalizedString label = text[$"ProcessState.{state}"];
        return label.ResourceNotFound ? state : label.Value;
    }
}
