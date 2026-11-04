using System.Globalization;

namespace Kazo.Os.Apps.SystemMonitor.Processes;

/// <summary>Sortable columns of the Processes tab (ADR 0058, section 2).</summary>
public enum ProcessColumn
{
    /// <summary>Process id.</summary>
    Pid,

    /// <summary>Short process name.</summary>
    Name,

    /// <summary>Owner.</summary>
    User,

    /// <summary>Scheduler state.</summary>
    State,

    /// <summary>CPU usage.</summary>
    Cpu,

    /// <summary>Resident memory.</summary>
    Memory,

    /// <summary>Disk bytes read per second.</summary>
    DiskRead,

    /// <summary>Disk bytes written per second.</summary>
    DiskWrite,

    /// <summary>Niceness (ADR 0127), hidden until <see cref="ProcessTable.ShowPriority"/> is set.</summary>
    Priority
}

/// <summary>One line of the process table, as displayed.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="ParentPid">Parent's id, or <see langword="null"/>.</param>
/// <param name="Name">Short process name.</param>
/// <param name="AppId">App id.</param>
/// <param name="User">Owner's login name (or user id).</param>
/// <param name="State">State as text (<c>Running</c>).</param>
/// <param name="StateCode">State letter (<c>R</c>).</param>
/// <param name="CpuPercent">CPU usage, in percent of one core.</param>
/// <param name="MemoryBytes">Resident memory, in bytes.</param>
/// <param name="ReadPerSecond">Disk bytes read per second.</param>
/// <param name="WritePerSecond">Disk bytes written per second.</param>
/// <param name="CpuEstimated">
/// Whether <paramref name="CpuPercent"/> is simulated rather than measured (<c>CpuSource:</c> of
/// <c>/proc/&lt;pid&gt;/status</c> other than <c>measured</c>, ADR 0066).
/// </param>
/// <param name="MemoryEstimated">Whether <paramref name="MemoryBytes"/> is simulated (<c>MemSource:</c>).</param>
/// <param name="Responding">
/// <see langword="false"/> when the OS reports the process "Not responding" (<c>Responding: 0</c> of
/// <c>/proc/&lt;pid&gt;/status</c>, ADR 0067, section 4). Display only: nothing is stopped.
/// </param>
public sealed record ProcessRow(
    long Pid,
    long? ParentPid,
    string Name,
    string AppId,
    string User,
    string State,
    char StateCode,
    double CpuPercent,
    long MemoryBytes,
    double ReadPerSecond,
    double WritePerSecond,
    bool CpuEstimated = false,
    bool MemoryEstimated = false,
    bool Responding = true)
{
    /// <summary>Text shown in the state column, which the OS's "Not responding" verdict overrides.</summary>
    public const string NotRespondingText = "Not responding";

    /// <summary>Gets the state column's text: <see cref="NotRespondingText"/> or <see cref="State"/>.</summary>
    public string DisplayState => Responding ? State : NotRespondingText;

    /// <summary>Gets the niceness (<c>NI</c>, field 19 of <c>/proc/&lt;pid&gt;/stat</c>, ADR 0127): <c>-20</c> to <c>19</c>.</summary>
    public int Nice { get; init; }
}

/// <summary>
/// The state behind the Processes tab, without any UI: filter, sort and multiple selection, with
/// the rules of a desktop file manager (click, Ctrl+click, Shift+click, Ctrl+A).
/// </summary>
/// <remarks>
/// The rows are replaced every second; the selection is kept by PID and loses the processes that
/// exited, so a refresh never selects a different process than the one the user clicked.
/// </remarks>
public sealed class ProcessTable
{
    private readonly HashSet<long> _selected = [];
    private IReadOnlyList<ProcessRow> _rows = [];
    private IReadOnlyList<ProcessRow>? _visible;
    private string _filter = string.Empty;
    private long? _anchor;

    /// <summary>Gets every row of the last refresh, filtered or not.</summary>
    public IReadOnlyList<ProcessRow> Rows => _rows;

    /// <summary>Gets the column the rows are sorted on. Defaults to CPU, busiest first, like <c>top</c>.</summary>
    public ProcessColumn SortColumn { get; private set; } = ProcessColumn.Cpu;

    /// <summary>Gets whether the sort is descending.</summary>
    public bool SortDescending { get; private set; } = true;

    /// <summary>
    /// Gets or sets whether the "Priority" column (niceness, ADR 0127) is shown. Hidden by default,
    /// like the optional columns of a desktop task manager; hiding it while the rows are sorted on
    /// it goes back to the CPU sort.
    /// </summary>
    public bool ShowPriority
    {
        get => _showPriority;
        set
        {
            _showPriority = value;
            if (!value && SortColumn == ProcessColumn.Priority)
            {
                SortColumn = ProcessColumn.Cpu;
                SortDescending = true;
                _visible = null;
            }
        }
    }

    private bool _showPriority;

    /// <summary>
    /// Gets or sets the filter text, matched case-insensitively against the name, the app id and
    /// the PID. Changing it drops the selected rows it hides, so "End task" never reaches a row
    /// the user cannot see.
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value?.Trim() ?? string.Empty;
            _visible = null;
            _selected.IntersectWith(Visible.Select(static row => row.Pid));
            if (_anchor is { } anchor && !_selected.Contains(anchor))
            {
                _anchor = null;
            }
        }
    }

    /// <summary>Gets the rows that pass the filter, in sort order.</summary>
    public IReadOnlyList<ProcessRow> Visible => _visible ??= [.. Sort(_rows.Where(Matches))];

    /// <summary>Gets the PIDs of the selected rows.</summary>
    public IReadOnlySet<long> Selected => _selected;

    /// <summary>
    /// Gets or sets the PID shown in the detail panel, or <see langword="null"/> when the panel is
    /// closed. It lives here so the panel survives a tab switch; it closes when the process exits.
    /// </summary>
    public long? DetailPid { get; set; }

    /// <summary>Gets the selected rows, in display order.</summary>
    public IReadOnlyList<ProcessRow> SelectedRows => [.. Visible.Where(row => _selected.Contains(row.Pid))];

    /// <summary>
    /// Gets whether <paramref name="column"/> shows at least one simulated value, so the header can
    /// carry the discreet "estimated" mention of ADR 0066. Only the CPU and memory columns can be.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <returns><see langword="true"/> when a row of the column is estimated.</returns>
    public bool IsEstimated(ProcessColumn column) => column switch
    {
        ProcessColumn.Cpu => _rows.Any(static row => row.CpuEstimated),
        ProcessColumn.Memory => _rows.Any(static row => row.MemoryEstimated),
        _ => false
    };

    /// <summary>Replaces the rows with a fresh reading, keeping the sort, the filter and the selection of surviving PIDs.</summary>
    /// <param name="rows">The processes of the last refresh.</param>
    public void SetRows(IEnumerable<ProcessRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = [.. rows];
        _visible = null;
        HashSet<long> alive = [.. _rows.Select(static row => row.Pid)];
        _selected.IntersectWith(alive);
        if (_anchor is { } anchor && !alive.Contains(anchor))
        {
            _anchor = null;
        }

        if (DetailPid is { } detail && !alive.Contains(detail))
        {
            DetailPid = null;
        }
    }

    /// <summary>Gets whether a PID is selected.</summary>
    /// <param name="pid">The PID.</param>
    public bool IsSelected(long pid) => _selected.Contains(pid);

    /// <summary>
    /// Sorts on <paramref name="column"/>: the same column again flips the direction; a new column
    /// starts busiest-first for the measures (CPU, memory, disk) and A to Z for the others.
    /// </summary>
    /// <param name="column">The column header clicked.</param>
    public void ToggleSort(ProcessColumn column)
    {
        if (column == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column is ProcessColumn.Cpu or ProcessColumn.Memory or ProcessColumn.DiskRead or ProcessColumn.DiskWrite;
        }

        _visible = null;
    }

    /// <summary>
    /// Applies a click on a row: alone, it selects only that row; with Ctrl it toggles the row;
    /// with Shift it selects the range from the last row clicked (adding to the selection with
    /// Ctrl+Shift).
    /// </summary>
    /// <param name="pid">The row clicked.</param>
    /// <param name="control">Whether Ctrl (or Cmd) was held.</param>
    /// <param name="shift">Whether Shift was held.</param>
    public void Click(long pid, bool control, bool shift)
    {
        if (shift && _anchor is { } anchor && IndexOf(anchor) is { } from && IndexOf(pid) is { } to)
        {
            if (!control)
            {
                _selected.Clear();
            }

            for (int index = Math.Min(from, to); index <= Math.Max(from, to); index++)
            {
                _selected.Add(Visible[index].Pid);
            }

            return;
        }

        if (control)
        {
            if (!_selected.Remove(pid))
            {
                _selected.Add(pid);
            }
        }
        else
        {
            _selected.Clear();
            _selected.Add(pid);
        }

        _anchor = pid;
    }

    /// <summary>Selects every visible row (Ctrl+A).</summary>
    public void SelectAll()
    {
        foreach (ProcessRow row in Visible)
        {
            _selected.Add(row.Pid);
        }
    }

    /// <summary>Clears the selection (Escape).</summary>
    public void ClearSelection()
    {
        _selected.Clear();
        _anchor = null;
    }

    private bool Matches(ProcessRow row) =>
        _filter.Length == 0
        || row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || row.AppId.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || row.Pid.ToString(CultureInfo.InvariantCulture).Contains(_filter, StringComparison.Ordinal);

    private IEnumerable<ProcessRow> Sort(IEnumerable<ProcessRow> rows)
    {
        IOrderedEnumerable<ProcessRow> ordered = SortColumn switch
        {
            ProcessColumn.Name => Order(rows, static row => row.Name, StringComparer.OrdinalIgnoreCase),
            ProcessColumn.User => Order(rows, static row => row.User, StringComparer.OrdinalIgnoreCase),
            ProcessColumn.State => Order(rows, static row => row.DisplayState, StringComparer.Ordinal),
            ProcessColumn.Cpu => Order(rows, static row => row.CpuPercent, Comparer<double>.Default),
            ProcessColumn.Memory => Order(rows, static row => row.MemoryBytes, Comparer<long>.Default),
            ProcessColumn.DiskRead => Order(rows, static row => row.ReadPerSecond, Comparer<double>.Default),
            ProcessColumn.DiskWrite => Order(rows, static row => row.WritePerSecond, Comparer<double>.Default),
            ProcessColumn.Priority => Order(rows, static row => row.Nice, Comparer<int>.Default),
            _ => Order(rows, static row => row.Pid, Comparer<long>.Default)
        };

        // Equal keys keep a stable order by PID, so rows do not jump at every refresh.
        return ordered.ThenBy(static row => row.Pid);
    }

    private IOrderedEnumerable<ProcessRow> Order<TKey>(IEnumerable<ProcessRow> rows, Func<ProcessRow, TKey> key, IComparer<TKey> comparer) =>
        SortDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    private int? IndexOf(long pid)
    {
        for (int index = 0; index < Visible.Count; index++)
        {
            if (Visible[index].Pid == pid)
            {
                return index;
            }
        }

        return null;
    }
}
