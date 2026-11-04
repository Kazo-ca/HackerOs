using System.Globalization;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Top;

/// <summary>
/// <c>top [-b] [-n N] [-d SECONDS] [-u USER] [-o FIELD]</c> (ADR 0064), procps style: a summary
/// (uptime, load, tasks, CPU from two <c>/proc/stat</c> readings, memory from
/// <c>/proc/meminfo</c>) and the processes sorted by CPU. In a terminal it runs full screen,
/// refreshing every 3 seconds; <c>q</c> or Ctrl+C quits, <c>M</c>/<c>P</c>/<c>N</c>/<c>T</c>
/// sort by memory, CPU, PID or time. With <c>-b</c> (or when its output is not a terminal) it
/// prints <c>-n</c> iterations as text, which pipes cleanly.
/// </summary>
public sealed class TopCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Only the messages are translated: the summary and the table stay English for scripts (ADR 0104, section 5).
        IStringLocalizer<Strings> messages = context.App.GetLocalizer<Strings>();
        bool batch = false;
        int? iterations = null;
        double delay = 3;
        string? user = null;
        string sort = "cpu";
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (arg)
            {
                case "-b": batch = true; break;
                case "-n" when int.TryParse(Next(), out int n): iterations = n; break;
                case "-d" when double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d): delay = Math.Max(0.1, d); break;
                case "-u" or "-U": user = Next(); break;
                case "-o": sort = (Next() ?? "cpu").TrimStart('%', '+', '-').ToLowerInvariant(); break;
                default:
                    if (arg.StartsWith("-n", StringComparison.Ordinal) && int.TryParse(arg[2..], out int inline))
                    {
                        iterations = inline;
                        break;
                    }

                    await context.StandardError.WriteLineAsync(messages["UnknownOption", arg]);
                    await context.StandardError.WriteLineAsync(messages["Usage"]);
                    return 1;
            }
        }

        if (batch || context.FullScreen is null)
        {
            int count = iterations ?? 1;
            SystemSnapshot? previous = null;
            for (int iteration = 0; iteration < count; iteration++)
            {
                if (iteration > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                    await context.StandardOutput.WriteLineAsync();
                }

                SystemSnapshot snapshot = await SystemSnapshot.ReadAsync(cancellationToken);
                foreach (string line in Render(context, snapshot, previous, user, sort, rows: int.MaxValue, columns: 120))
                {
                    await context.StandardOutput.WriteLineAsync(line);
                }

                previous = snapshot;
            }

            return 0;
        }

        IFullScreenTerminalSession screen = context.FullScreen;
        await screen.EnterAlternateScreenAsync(cancellationToken);
        try
        {
            SystemSnapshot? previous = null;
            int shown = 0;
            while (iterations is null || shown < iterations)
            {
                SystemSnapshot snapshot = await SystemSnapshot.ReadAsync(cancellationToken);
                TerminalViewport viewport = screen.Viewport;
                List<string> lines = Render(context, snapshot, previous, user, sort, viewport.Rows, viewport.Columns);
                await screen.RenderAsync(new TerminalScreenFrame(lines, new TerminalCursor(0, 0, Visible: false), "top"), cancellationToken);
                previous = snapshot;
                shown++;

                // Wait for the next refresh, reacting to keys meanwhile.
                using CancellationTokenSource tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tick.CancelAfter(TimeSpan.FromSeconds(delay));
                while (!tick.IsCancellationRequested)
                {
                    TerminalKeyEvent key;
                    try
                    {
                        key = await screen.ReadKeyAsync(tick.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (key.Key == TerminalKey.Character && (key.Text is "q" or "Q" || (key.Control && key.Text is "c" or "C")))
                    {
                        return 0;
                    }

                    if (key.Key == TerminalKey.Resize || key.Text is " ")
                    {
                        break;
                    }

                    if (key.Key == TerminalKey.Character && key.Text is { } text)
                    {
                        sort = text switch { "M" => "mem", "P" => "cpu", "N" => "pid", "T" => "time", _ => sort };
                        break;
                    }
                }
            }

            return 0;
        }
        finally
        {
            await screen.LeaveAlternateScreenAsync(CancellationToken.None);
        }
    }

    /// <summary>The summary and the process table, as top prints them.</summary>
    internal static List<string> Render(
        TerminalExecutionContext context, SystemSnapshot snapshot, SystemSnapshot? previous, string? user, string sort, int rows, int columns)
    {
        DateTimeOffset now = context.App.Clock.UtcNow;
        IReadOnlyList<KazoProcess> processes = snapshot.Processes;
        int running = processes.Count(p => p.StateCode == 'R');
        int stopped = processes.Count(p => p.StateCode == 'T');
        int zombie = processes.Count(p => p.StateCode == 'Z');
        double busy = previous is null
            ? Math.Min(100, processes.Sum(p => p.CpuUsage) / snapshot.Cores)
            : SystemSnapshot.BusyPercent(previous.CpuTicks, snapshot.CpuTicks);
        double totalMb = snapshot.MemoryTotalKb / 1024.0;
        double freeMb = snapshot.MemoryFreeKb / 1024.0;
        double availableMb = snapshot.MemoryAvailableKb / 1024.0;
        double usedMb = Math.Max(0, totalMb - availableMb);
        int users = processes.Select(p => p.UserId).Distinct().Count();

        List<string> lines =
        [
            $"top - {now:HH:mm:ss} up {SystemSnapshot.FormatUptime(snapshot.Uptime)},  {users} user{(users == 1 ? string.Empty : "s")},  load average: {snapshot.LoadAverage}",
            $"Tasks: {processes.Count,3} total, {running,3} running, {processes.Count - running - stopped - zombie,3} sleeping, {stopped,3} stopped, {zombie,3} zombie",
            string.Create(CultureInfo.InvariantCulture, $"%Cpu(s): {busy * 0.8,5:0.0} us, {busy * 0.2,5:0.0} sy,   0.0 ni, {100 - busy,5:0.0} id,   0.0 wa,   0.0 hi,   0.0 si,   0.0 st"),
            string.Create(CultureInfo.InvariantCulture, $"MiB Mem : {totalMb,8:0.0} total, {freeMb,8:0.0} free, {usedMb,8:0.0} used, {Math.Max(0, availableMb - freeMb),8:0.0} buff/cache"),
            string.Create(CultureInfo.InvariantCulture, $"MiB Swap: {0.0,8:0.0} total, {0.0,8:0.0} free, {0.0,8:0.0} used. {availableMb,8:0.0} avail Mem"),
            string.Empty,
            "    PID USER      PR  NI    VIRT    RES    SHR S  %CPU  %MEM     TIME+ COMMAND"
        ];

        IEnumerable<KazoProcess> shown = processes.Where(p => user is null || p.UserName == user || p.UserId == user);
        shown = sort switch
        {
            "mem" or "res" => shown.OrderByDescending(p => p.WorkingSet64),
            "pid" => shown.OrderBy(p => p.Id),
            "time" => shown.OrderByDescending(p => (now - p.StartTime).TotalSeconds * p.CpuUsage),
            _ => shown.OrderByDescending(p => p.CpuUsage).ThenBy(p => p.Id)
        };

        foreach (KazoProcess process in shown)
        {
            if (lines.Count >= rows)
            {
                break;
            }

            long resKb = process.WorkingSet64 / 1024;
            double memPercent = snapshot.MemoryTotalKb == 0 ? 0 : 100.0 * resKb / snapshot.MemoryTotalKb;
            string name = process.ProcessName.Length > 0 ? process.ProcessName : process.AppId;
            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{process.Id,7} {Truncate(process.UserName.Length > 0 ? process.UserName : process.UserId, 8),-8} {process.BasePriority,3} {process.Nice,3} {resKb * 3,7} {resKb,6} {resKb / 4,6} {process.StateCode} {process.CpuUsage,5:0.0} {memPercent,5:0.0} {SystemSnapshot.FormatCpuTime(process, now),9} {name}");
            lines.Add(line.Length > columns ? line[..columns] : line);
        }

        return lines;
    }

    private static string Truncate(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "+";
}
