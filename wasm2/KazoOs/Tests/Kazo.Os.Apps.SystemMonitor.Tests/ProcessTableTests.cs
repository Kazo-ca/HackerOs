using Kazo.Os.Apps.SystemMonitor.Processes;
using Kazo.Os.AppSdk.IO;
using Xunit;

namespace Kazo.Os.Apps.SystemMonitor.Tests;

/// <summary>Sort, filter and multiple selection of the Processes tab (ADR 0058, section 2).</summary>
public sealed class ProcessTableTests
{
    private static readonly ProcessRow[] Rows =
    [
        Row(1, "kernel", "org.hackeros.kernel", cpu: 1, memory: 64),
        Row(10, "terminal", "org.hackeros.terminal", cpu: 30, memory: 45),
        Row(11, "text-editor", "org.hackeros.text-editor", cpu: 5, memory: 52),
        Row(12, "calculator", "org.hackeros.calculator", cpu: 5, memory: 32)
    ];

    [Fact]
    public void Rows_start_sorted_by_cpu_busiest_first_with_ties_by_pid()
    {
        ProcessTable table = Table();

        Assert.Equal([10L, 11, 12, 1], Pids(table));
    }

    [Fact]
    public void Clicking_the_sorted_column_flips_it_and_a_new_column_starts_a_to_z()
    {
        ProcessTable table = Table();

        table.ToggleSort(ProcessColumn.Cpu);
        Assert.Equal([1L, 11, 12, 10], Pids(table));

        table.ToggleSort(ProcessColumn.Name);
        Assert.False(table.SortDescending);
        Assert.Equal([12L, 1, 10, 11], Pids(table));

        table.ToggleSort(ProcessColumn.Memory);
        Assert.True(table.SortDescending);
        Assert.Equal([1L, 11, 10, 12], Pids(table));
    }

    [Theory]
    [InlineData("TEXT", new long[] { 11 })]
    [InlineData("hackeros.ca", new long[] { 12 })]
    [InlineData("1", new long[] { 10, 11, 12, 1 })]
    [InlineData("nothing", new long[0])]
    public void The_filter_matches_name_app_id_and_pid(string filter, long[] expected)
    {
        ProcessTable table = Table();

        table.Filter = filter;

        Assert.Equal(expected, Pids(table));
    }

    [Fact]
    public void Click_ctrl_click_and_shift_click_select_like_a_file_manager()
    {
        ProcessTable table = Table(); // display order: 10, 11, 12, 1

        table.Click(10, control: false, shift: false);
        table.Click(12, control: false, shift: true);
        Assert.Equal([10L, 11, 12], table.SelectedRows.Select(static row => row.Pid));

        table.Click(11, control: true, shift: false);
        Assert.Equal([10L, 12], table.SelectedRows.Select(static row => row.Pid));

        table.Click(1, control: true, shift: true);
        Assert.Equal([10L, 11, 12, 1], table.SelectedRows.Select(static row => row.Pid));

        table.Click(1, control: false, shift: false);
        Assert.Equal([1L], table.SelectedRows.Select(static row => row.Pid));
    }

    [Fact]
    public void Select_all_takes_the_visible_rows_and_the_filter_drops_hidden_selected_rows()
    {
        ProcessTable table = Table();
        table.SelectAll();
        Assert.Equal(4, table.Selected.Count);

        table.Filter = "editor";

        Assert.Equal([11L], table.Selected);
    }

    [Fact]
    public void A_refresh_keeps_the_selection_of_surviving_processes_and_closes_a_vanished_detail()
    {
        ProcessTable table = Table();
        table.Click(10, control: false, shift: false);
        table.Click(11, control: true, shift: false);
        table.DetailPid = 11;

        table.SetRows([Rows[0], Rows[1], Rows[3]]);

        Assert.Equal([10L], table.Selected);
        Assert.Null(table.DetailPid);
    }

    [Fact]
    public async Task End_task_reports_each_refusal_and_still_ends_the_others()
    {
        List<long> killed = [];

        EndTaskReport report = await EndTaskRunner.EndAsync(
            [Rows[1], Rows[0], Rows[2]],
            row => row.Pid switch
            {
                1 => throw new KazoUnauthorizedAccessException("denied", requiredCapability: "process.manage"),
                11 => throw new InvalidOperationException("Process 11 has exited."),
                _ => Record(row.Pid)
            });

        Assert.Equal([10L], killed);
        Assert.Equal([10L, 11], report.Ended.Select(static row => row.Pid));
        EndTaskFailure failure = Assert.Single(report.Failures);
        Assert.Equal(1, failure.Pid);
        Assert.Contains("process.manage", failure.Reason, StringComparison.Ordinal);

        Task Record(long pid)
        {
            killed.Add(pid);
            return Task.CompletedTask;
        }
    }

    private static ProcessTable Table()
    {
        ProcessTable table = new();
        table.SetRows(Rows);
        return table;
    }

    [Fact]
    public void Only_columns_with_simulated_values_are_marked_estimated()
    {
        // ADR 0066: the discreet "estimated" mark follows CpuSource/MemSource of /proc/<pid>/status.
        ProcessTable table = new();
        table.SetRows(
        [
            Row(1, "a", "org.a", 1, 1) with { CpuEstimated = false, MemoryEstimated = true },
            Row(2, "b", "org.b", 1, 1) with { CpuEstimated = false, MemoryEstimated = false }
        ]);

        Assert.False(table.IsEstimated(ProcessColumn.Cpu));
        Assert.True(table.IsEstimated(ProcessColumn.Memory));
        Assert.False(table.IsEstimated(ProcessColumn.DiskRead));

        table.SetRows([Row(1, "a", "org.a", 1, 1) with { CpuEstimated = true }]);
        Assert.True(table.IsEstimated(ProcessColumn.Cpu));
        Assert.False(table.IsEstimated(ProcessColumn.Memory));
    }

    [Fact]
    public void A_process_that_does_not_respond_shows_it_in_the_state_column_and_sorts_by_it()
    {
        // ADR 0067, section 4: display only, from Responding: 0 of /proc/<pid>/status.
        ProcessTable table = new();
        table.SetRows(
        [
            Row(1, "a", "org.a", 1, 1) with { Responding = false },
            Row(2, "b", "org.b", 1, 1)
        ]);

        Assert.Equal("Not responding", table.Rows[0].DisplayState);
        Assert.Equal("Running", table.Rows[1].DisplayState);

        table.ToggleSort(ProcessColumn.State);
        Assert.Equal([1, 2], Pids(table));
    }

    [Fact]
    public void The_priority_column_is_hidden_by_default_and_sorts_lowest_niceness_first()
    {
        ProcessTable table = new();
        table.SetRows([Rows[0] with { Nice = 5 }, Rows[1] with { Nice = -3 }, Rows[2], Rows[3] with { Nice = 19 }]);
        Assert.False(table.ShowPriority);

        table.ShowPriority = true;
        table.ToggleSort(ProcessColumn.Priority);

        Assert.False(table.SortDescending);
        Assert.Equal([10L, 11, 1, 12], Pids(table));
    }

    [Fact]
    public void Hiding_the_priority_column_while_sorted_on_it_goes_back_to_the_cpu_order()
    {
        ProcessTable table = Table();
        table.ShowPriority = true;
        table.ToggleSort(ProcessColumn.Priority);

        table.ShowPriority = false;

        Assert.Equal(ProcessColumn.Cpu, table.SortColumn);
        Assert.True(table.SortDescending);
    }

    private static long[] Pids(ProcessTable table) => [.. table.Visible.Select(static row => row.Pid)];

    private static ProcessRow Row(long pid, string name, string appId, double cpu, long memory) =>
        new(pid, null, name, appId, "alice", "Running", 'R', cpu, memory, 0, 0);
}
