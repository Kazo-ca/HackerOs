using System.Globalization;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Htop;

/// <summary>
/// <c>htop [-d TENTHS] [-u USER] [-t]</c> (ADR 0064): the interactive process viewer, full screen
/// and in colour: one bar per core (from successive <c>/proc/stat</c> readings), memory and swap
/// bars, tasks, load and uptime, then a scrollable process list with a highlighted selection.
/// Keys: arrows, PgUp/PgDn, Home/End move; <c>P</c>/<c>M</c>/<c>T</c>/<c>N</c> sort by CPU,
/// memory, time, PID; <c>t</c> toggles the tree view; <c>u</c> cycles users; <c>k</c> (or F9)
/// kills the selected process (<c>process.manage</c>); <c>/</c> searches; <c>q</c> (or F10, Ctrl+C) quits.
/// Without a terminal it prints a single snapshot.
/// </summary>
public sealed class HtopCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    private const string Reset = "\u001b[0m";
    private const string Header = "\u001b[30;42m";
    private const string Selected = "\u001b[30;46m";
    private const string Bracket = "\u001b[1;37m";
    private const string Label = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Red = "\u001b[31m";
    private const string Blue = "\u001b[34m";
    private const string Yellow = "\u001b[33m";
    private const string Bold = "\u001b[1m";
    private const string Footer = "\u001b[30;46m";

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        State state = new();
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-d" when i + 1 < args.Count && int.TryParse(args[i + 1], out int tenths):
                    state.Delay = TimeSpan.FromMilliseconds(Math.Max(1, tenths) * 100);
                    i++;
                    break;
                case "-u" when i + 1 < args.Count:
                    state.User = args[++i];
                    break;
                case "-t" or "--tree":
                    state.Tree = true;
                    break;
                case "-C" or "--no-color":
                    state.Color = false;
                    break;
                default:
                    await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["UnrecognizedOption", args[i]]);
                    return 1;
            }
        }

        if (context.FullScreen is not { } screen)
        {
            state.Color = false;
            SystemSnapshot snapshot = await SystemSnapshot.ReadAsync(cancellationToken);
            foreach (string line in Render(context, state, snapshot, null, 100, 40))
            {
                await context.StandardOutput.WriteLineAsync(line);
            }

            return 0;
        }

        await screen.EnterAlternateScreenAsync(cancellationToken);
        try
        {
            SystemSnapshot? previous = null;
            SystemSnapshot snapshot = await SystemSnapshot.ReadAsync(cancellationToken);
            while (true)
            {
                TerminalViewport viewport = screen.Viewport;
                await screen.RenderAsync(
                    new TerminalScreenFrame(Render(context, state, snapshot, previous, viewport.Columns, viewport.Rows), new TerminalCursor(0, 0, Visible: false), "htop"),
                    cancellationToken);

                using CancellationTokenSource tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tick.CancelAfter(state.Delay);
                TerminalKeyEvent? key = null;
                try
                {
                    key = await screen.ReadKeyAsync(tick.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Refresh time.
                }

                if (key is null)
                {
                    previous = snapshot;
                    snapshot = await SystemSnapshot.ReadAsync(cancellationToken);
                    continue;
                }

                if (await HandleKeyAsync(context, screen, state, key, cancellationToken))
                {
                    return 0;
                }

                if (state.Refresh)
                {
                    state.Refresh = false;
                    previous = snapshot;
                    snapshot = await SystemSnapshot.ReadAsync(cancellationToken);
                }
            }
        }
        finally
        {
            await screen.LeaveAlternateScreenAsync(CancellationToken.None);
        }
    }

    /// <summary>Applies one key; returns true to quit.</summary>
    private static async Task<bool> HandleKeyAsync(
        TerminalExecutionContext context, IFullScreenTerminalSession screen, State state, TerminalKeyEvent key, CancellationToken cancellationToken)
    {
        int page = Math.Max(1, screen.Viewport.Rows - 8);
        switch (key.Key)
        {
            case TerminalKey.ArrowUp: state.Selection = Math.Max(0, state.Selection - 1); return false;
            case TerminalKey.ArrowDown: state.Selection++; return false;
            case TerminalKey.PageUp: state.Selection = Math.Max(0, state.Selection - page); return false;
            case TerminalKey.PageDown: state.Selection += page; return false;
            case TerminalKey.Home: state.Selection = 0; return false;
            case TerminalKey.End: state.Selection = int.MaxValue / 2; return false;
            case TerminalKey.Escape: state.Message = null; state.Filter = null; return false;
            case TerminalKey.Resize: return false;
        }

        if (key.Key != TerminalKey.Character || key.Text is not { } text)
        {
            return false;
        }

        if (key.Control && text is "c" or "C")
        {
            return true;
        }

        switch (text)
        {
            case "q" or "Q":
                return true;
            case "P": state.Sort = "cpu"; break;
            case "M": state.Sort = "mem"; break;
            case "T": state.Sort = "time"; break;
            case "N": state.Sort = "pid"; break;
            case "t": state.Tree = !state.Tree; break;
            case "H" or "K": break;
            case "u":
                state.User = state.User is null ? context.App.UserLoginName : null;
                break;
            case "/":
                state.Filter = await PromptAsync(screen, context.App.GetLocalizer<Strings>()["SearchPrompt"], cancellationToken);
                state.Selection = 0;
                break;
            case "k" or "\u001b[20~":
                if (state.SelectedProcess is { } target)
                {
                    try
                    {
                        await target.KillAsync(cancellationToken);
                        state.Message = context.App.GetLocalizer<Strings>()["Killed", target.Id, target.ProcessName];
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or IOException)
                    {
                        state.Message = $"kill {target.Id}: {CommandText.Describe(context, exception)}";
                    }

                    state.Refresh = true;
                }

                break;
            case " ":
                state.Refresh = true;
                break;
        }

        return false;
    }

    /// <summary>Reads a short line on the bottom row (search).</summary>
    private static async Task<string?> PromptAsync(IFullScreenTerminalSession screen, string label, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        while (true)
        {
            TerminalViewport viewport = screen.Viewport;
            List<string> lines = [.. Enumerable.Repeat(string.Empty, Math.Max(0, viewport.Rows - 1)), Footer + label + text + Reset];
            await screen.RenderAsync(new TerminalScreenFrame(lines, new TerminalCursor(label.Length + text.Length, viewport.Rows - 1), "search"), cancellationToken);
            TerminalKeyEvent key = await screen.ReadKeyAsync(cancellationToken);
            switch (key.Key)
            {
                case TerminalKey.Enter:
                    return text.Length == 0 ? null : text.ToString();
                case TerminalKey.Escape:
                    return null;
                case TerminalKey.Backspace when text.Length > 0:
                    text.Length--;
                    break;
                case TerminalKey.Character when !key.Control && key.Text is { } typed:
                    text.Append(typed);
                    break;
            }
        }
    }

    /// <summary>Draws the meters, the process list and the function bar.</summary>
    private static List<string> Render(TerminalExecutionContext context, State state, SystemSnapshot snapshot, SystemSnapshot? previous, int columns, int rows)
    {
        List<string> lines = [];
        int half = Math.Max(20, columns / 2);
        int barWidth = Math.Max(10, half - 8);
        List<string> left = [];
        for (int core = 0; core < snapshot.Cores; core++)
        {
            double percent = previous is not null && core < snapshot.CoreTicks.Count && core < previous.CoreTicks.Count
                ? SystemSnapshot.BusyPercent(previous.CoreTicks[core], snapshot.CoreTicks[core])
                : Math.Min(100, snapshot.Processes.Sum(p => p.CpuUsage) / snapshot.Cores);
            left.Add(Meter(state, core.ToString(CultureInfo.InvariantCulture).PadLeft(3), percent / 100, $"{percent:0.0}%", barWidth, Green));
        }

        long usedKb = Math.Max(0, snapshot.MemoryTotalKb - snapshot.MemoryAvailableKb);
        double memoryShare = snapshot.MemoryTotalKb == 0 ? 0 : (double)usedKb / snapshot.MemoryTotalKb;
        left.Add(Meter(state, "Mem", memoryShare, $"{CommandIO.HumanSize(usedKb * 1024)}/{CommandIO.HumanSize(snapshot.MemoryTotalKb * 1024)}", barWidth, Green));
        left.Add(Meter(state, "Swp", 0, "0K/0K", barWidth, Red));

        int running = snapshot.Processes.Count(p => p.StateCode == 'R');
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        List<string> right =
        [
            $"{C(state, Label)}{text["Meter.Tasks"]} {C(state, Bold)}{snapshot.Processes.Count}{C(state, Reset)}{C(state, Label)}, {C(state, Green)}{running}{C(state, Label)} {text["Meter.Running"]}{C(state, Reset)}",
            $"{C(state, Label)}{text["Meter.LoadAverage"]} {C(state, Bold)}{snapshot.LoadAverage.Replace(",", string.Empty, StringComparison.Ordinal)}{C(state, Reset)}",
            $"{C(state, Label)}{text["Meter.Uptime"]} {C(state, Bold)}{snapshot.Uptime:hh\\:mm\\:ss}{C(state, Reset)}"
        ];

        for (int row = 0; row < Math.Max(left.Count, right.Count); row++)
        {
            string l = row < left.Count ? left[row] : string.Empty;
            string r = row < right.Count ? right[row] : string.Empty;
            lines.Add(l + new string(' ', Math.Max(1, half - VisibleLength(l))) + r);
        }

        lines.Add(string.Empty);
        lines.Add(C(state, Header) + "    PID USER      PRI  NI  VIRT   RES   SHR S CPU% MEM%   TIME+  Command".PadRight(columns) + C(state, Reset));

        DateTimeOffset now = context.App.Clock.UtcNow;
        List<(KazoProcess Process, int Depth)> ordered = Order(state, snapshot.Processes, now);
        int listRows = Math.Max(1, rows - lines.Count - (state.Message is null ? 1 : 2));
        state.Selection = Math.Clamp(state.Selection, 0, Math.Max(0, ordered.Count - 1));
        int top = Math.Max(0, state.Selection - listRows + 1);
        state.SelectedProcess = ordered.Count > 0 ? ordered[state.Selection].Process : null;
        for (int index = top; index < Math.Min(ordered.Count, top + listRows); index++)
        {
            (KazoProcess process, int depth) = ordered[index];
            long resKb = process.WorkingSet64 / 1024;
            double memPercent = snapshot.MemoryTotalKb == 0 ? 0 : 100.0 * resKb / snapshot.MemoryTotalKb;
            string command = (state.Tree && depth > 0 ? new string(' ', (depth - 1) * 3) + "`- " : string.Empty)
                + (process.CommandLine.Length > 0 ? process.CommandLine : process.AppId);
            string user = process.UserName.Length > 0 ? process.UserName : process.UserId;
            string row = string.Create(
                CultureInfo.InvariantCulture,
                $"{process.Id,7} {(user.Length > 8 ? user[..8] : user),-8} {process.BasePriority,4} {process.Nice,3} {CommandIO.HumanSize(resKb * 3 * 1024),5} {CommandIO.HumanSize(resKb * 1024),5} {CommandIO.HumanSize(resKb * 256),5} {process.StateCode} {process.CpuUsage,4:0.0} {memPercent,4:0.0} {SystemSnapshot.FormatCpuTime(process, now),8}  {command}");
            lines.Add(index == state.Selection
                ? C(state, Selected) + row.PadRight(columns) + C(state, Reset)
                : (process.StateCode == 'R' ? C(state, Green) : string.Empty) + row + C(state, Reset));
        }

        while (lines.Count < rows - (state.Message is null ? 1 : 2))
        {
            lines.Add(string.Empty);
        }

        if (state.Message is { } message)
        {
            lines.Add(C(state, Yellow) + message + C(state, Reset));
        }

        string[] keys =
        [
            "F1/?", text["Key.Help"], "/", text["Key.Search"], "t", text["Key.Tree"], "P/M/T", text["Key.SortBy"],
            "u", text["Key.User"], "k", text["Key.Kill"], "q", text["Key.Quit"]
        ];
        StringBuilder bar = new();
        for (int i = 0; i < keys.Length; i += 2)
        {
            bar.Append(C(state, Bold)).Append(keys[i]).Append(C(state, Reset)).Append(C(state, Footer)).Append(keys[i + 1].PadRight(7)).Append(C(state, Reset));
        }

        lines.Add(bar.ToString());
        return lines;
    }

    /// <summary>Sorted list, or a parent-first tree with depths when the tree view is on.</summary>
    private static List<(KazoProcess Process, int Depth)> Order(State state, IReadOnlyList<KazoProcess> processes, DateTimeOffset now)
    {
        IEnumerable<KazoProcess> visible = processes.Where(p =>
            (state.User is null || p.UserName == state.User || p.UserId == state.User)
            && (state.Filter is null || p.CommandLine.Contains(state.Filter, StringComparison.OrdinalIgnoreCase) || p.AppId.Contains(state.Filter, StringComparison.OrdinalIgnoreCase)));
        Func<IEnumerable<KazoProcess>, IEnumerable<KazoProcess>> sort = state.Sort switch
        {
            "mem" => items => items.OrderByDescending(p => p.WorkingSet64),
            "time" => items => items.OrderByDescending(p => (now - p.StartTime).TotalSeconds * p.CpuUsage),
            "pid" => items => items.OrderBy(p => p.Id),
            _ => items => items.OrderByDescending(p => p.CpuUsage).ThenBy(p => p.Id)
        };

        List<KazoProcess> list = [.. visible];
        if (!state.Tree)
        {
            return [.. sort(list).Select(p => (p, 0))];
        }

        HashSet<long> ids = [.. list.Select(p => p.Id)];
        ILookup<long?, KazoProcess> children = list.ToLookup(p => p.ParentId is { } parent && ids.Contains(parent) ? (long?)parent : null);
        List<(KazoProcess, int)> ordered = [];
        void Walk(long? parent, int depth)
        {
            foreach (KazoProcess process in children[parent].OrderBy(p => p.Id))
            {
                ordered.Add((process, depth));
                if (depth < 32)
                {
                    Walk(process.Id, depth + 1);
                }
            }
        }

        Walk(null, 0);
        return ordered;
    }

    /// <summary>A meter like <c>  0[|||||       12.5%]</c>, the bar coloured and the value at its right end.</summary>
    private static string Meter(State state, string label, double share, string value, int width, string colour)
    {
        int inner = Math.Max(4, width - 2);
        int filled = (int)Math.Round(Math.Clamp(share, 0, 1) * inner);
        string bars = new string('|', filled).PadRight(inner);
        if (value.Length < inner)
        {
            bars = bars[..(inner - value.Length)] + value;
        }

        string coloured = C(state, colour) + bars[..Math.Min(filled, inner - Math.Min(value.Length, inner))] + C(state, Reset)
            + bars[Math.Min(filled, inner - Math.Min(value.Length, inner))..];
        return $"{C(state, Label)}{label}{C(state, Reset)}{C(state, Bracket)}[{C(state, Reset)}{coloured}{C(state, Bracket)}]{C(state, Reset)}";
    }

    private static string C(State state, string code) => state.Color ? code : string.Empty;

    private static int VisibleLength(string text)
    {
        int length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u001b')
            {
                while (i < text.Length && text[i] != 'm')
                {
                    i++;
                }

                continue;
            }

            length++;
        }

        return length;
    }

    private sealed class State
    {
        public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(1.5);

        public string? User { get; set; }

        public bool Tree { get; set; }

        public bool Color { get; set; } = true;

        public string Sort { get; set; } = "cpu";

        public int Selection { get; set; }

        public string? Filter { get; set; }

        public string? Message { get; set; }

        public bool Refresh { get; set; }

        public KazoProcess? SelectedProcess { get; set; }
    }
}
