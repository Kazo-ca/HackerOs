using System.Globalization;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Ps;

/// <summary>
/// Implements the <c>ps</c> terminal command (`P4-W5-CMD-004`).
/// Lists running processes by reading <c>/proc</c>, like procps on Linux (ADR 0046): one
/// <c>/proc/&lt;pid&gt;/status</c> file per process, and the <c>/proc/&lt;pid&gt;</c> directory's
/// creation time as the process start time. Which processes appear is decided by <c>/proc</c>
/// itself (own process always, others with <c>process.list</c>); <c>kill</c> keeps using the
/// process gateway for actions. <c>-l</c> and <c>-o pid,ni,…</c> (ADR 0127) add the niceness,
/// read from field 19 of <c>/proc/&lt;pid&gt;/stat</c>.
/// </summary>
public sealed class PsCommand : TerminalAppBase
{
    private static readonly VirtualPath ProcRoot = VirtualPath.Parse("/proc");

    /// <summary>Initializes the command with its manifest.</summary>
    public PsCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        bool allUsers = false;
        bool userFormat = false;
        bool longFormat = false;
        List<string> fields = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is "-a" or "-e" or "-A") allUsers = true;
            else if (arg == "-u") userFormat = true;
            else if (arg == "-l") longFormat = true;
            else if (arg is "-o" or "--format" && i + 1 < args.Count) fields.AddRange(SplitFields(args[++i]));
            else if (arg.StartsWith("-o", StringComparison.Ordinal) && arg.Length > 2) fields.AddRange(SplitFields(arg[2..]));
            else if (arg.StartsWith("--format=", StringComparison.Ordinal)) fields.AddRange(SplitFields(arg["--format=".Length..]));
        }

        IStringLocalizer<Strings> messages = context.App.GetLocalizer<Strings>();
        string? unknown = fields.FirstOrDefault(field => !Columns.ContainsKey(field));
        if (unknown is not null)
        {
            context.StandardError.WriteLine(messages["UnknownField", unknown]);
            return 1;
        }

        FileSystemResult<FileSystemDirectorySnapshot> listing =
            await context.App.FileSystem.EnumerateAsync(new FileSystemEnumerateRequest(ProcRoot), cancellationToken);
        if (!listing.Succeeded || listing.Value is null)
        {
            // The table below stays English for scripts (ADR 0104, section 5); only this message is translated.
            context.StandardError.WriteLine(messages["CannotReadProc", listing.Error?.Code.ToString() ?? string.Empty]);
            return 1;
        }

        // NI and PRI live in /proc/<pid>/stat (ADR 0127): read it only for the formats that show them.
        bool needsStat = longFormat || fields.Any(static field => field is "ni" or "nice" or "pri");
        List<ProcEntry> processes = [];
        foreach (FileSystemDirectoryItem item in listing.Value.Entries)
        {
            // Only the numeric directories are processes; self, meminfo, uptime... are skipped.
            if (item.Metadata.Kind != FileSystemEntryKind.Directory
                || !long.TryParse(item.Name.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long pid))
            {
                continue;
            }

            // A process can exit between the listing and this read; procps skips it the same way.
            IReadOnlyDictionary<string, string>? status = await ReadStatusAsync(context, pid, cancellationToken);
            if (status is not null)
            {
                string? stat = needsStat ? await ReadTextAsync(context, pid, "stat", cancellationToken) : null;
                int nice = stat is null ? 0 : HackerOs.AppSdk.Diagnostics.HackProcess.ParseStatNice(stat);
                processes.Add(new ProcEntry(pid, item.Metadata.Timestamps.CreatedAtUtc, status, nice));
            }
        }

        DateTimeOffset now = context.App.Clock.UtcNow;
        string currentSession = context.App.SessionId.ToString();
        IEnumerable<ProcEntry> visible = allUsers
            ? processes
            : processes.Where(p => p.Field("Session") == currentSession);

        if (fields.Count == 0)
        {
            fields = longFormat
                ? ["f", "s", "uid", "pid", "ppid", "c", "pri", "ni", "time", "cmd"]
                : userFormat ? ["user", "pid", "stat", "time", "cmd"] : ["pid", "stat", "time", "cmd"];
        }

        List<Column> columns = [.. fields.Select(field => Columns[field])];
        context.StandardOutput.WriteLine(string.Join(' ', columns.Select(static column => column.Pad(column.Header))).TrimEnd());
        foreach (ProcEntry proc in visible.OrderBy(p => p.Pid))
        {
            context.StandardOutput.WriteLine(string.Join(' ', columns.Select(column => column.Pad(column.Value(proc, now)))).TrimEnd());
        }

        return 0;
    }

    /// <summary>A background task (ADR 0128) shows as <c>&lt;app command&gt;:&lt;task&gt;</c>, like a named thread of its app.</summary>
    private static string CommandOf(ProcEntry proc) =>
        proc.Status.TryGetValue("Job", out string? job) ? job : proc.Field("AppId");

    /// <summary>
    /// The <c>-o</c> fields procps knows that Kazo OS can fill, with their headers (ADR 0127 adds
    /// <c>ni</c> and <c>pri</c>). <c>pri</c> is procps's <c>PRI</c> of <c>ps -l</c>: <c>80 + nice</c>.
    /// </summary>
    private static readonly Dictionary<string, Column> Columns = new(StringComparer.Ordinal)
    {
        ["pid"] = new("PID", 5, Right: true, static (p, _) => p.Pid.ToString(CultureInfo.InvariantCulture)),
        ["ppid"] = new("PPID", 5, Right: true, static (p, _) => p.Field("PPid")),
        ["user"] = new("USER", 10, Right: false, static (p, _) => p.Field("Uid")),
        ["uid"] = new("UID", 10, Right: false, static (p, _) => p.Field("Uid")),
        ["ni"] = new("NI", 3, Right: true, static (p, _) => p.Nice.ToString(CultureInfo.InvariantCulture)),
        ["nice"] = new("NI", 3, Right: true, static (p, _) => p.Nice.ToString(CultureInfo.InvariantCulture)),
        ["pri"] = new("PRI", 3, Right: true, static (p, _) => (80 + p.Nice).ToString(CultureInfo.InvariantCulture)),
        // "R (running)" -> "R", the single-letter STAT code procps prints.
        ["stat"] = new("STAT", 4, Right: false, static (p, _) => p.Field("State").Split(' ')[0]),
        ["s"] = new("S", 1, Right: false, static (p, _) => p.Field("State").Split(' ')[0]),
        ["f"] = new("F", 1, Right: true, static (_, _) => "0"),
        ["c"] = new("C", 2, Right: true, static (p, _) =>
            double.TryParse(p.Field("Cpu"), NumberStyles.Float, CultureInfo.InvariantCulture, out double cpu)
                ? ((int)cpu).ToString(CultureInfo.InvariantCulture)
                : "0"),
        ["time"] = new("TIME", 8, Right: true, static (p, now) =>
            (now > p.StartedAtUtc ? now - p.StartedAtUtc : TimeSpan.Zero).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)),
        ["comm"] = new("COMMAND", 0, Right: false, static (p, _) => p.Field("Name")),
        ["cmd"] = new("CMD", 0, Right: false, static (p, _) => CommandOf(p)),
        ["args"] = new("COMMAND", 0, Right: false, static (p, _) => CommandOf(p)),
        ["command"] = new("COMMAND", 0, Right: false, static (p, _) => CommandOf(p)),
    };

    private static IEnumerable<string> SplitFields(string list) =>
        list.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static field => field.ToLowerInvariant() is "%cpu" or "pcpu" ? "c" : field.ToLowerInvariant());

    /// <summary>Reads one file of a process directory, or returns null if it vanished.</summary>
    private static async ValueTask<string?> ReadTextAsync(TerminalExecutionContext context, long pid, string entry, CancellationToken cancellationToken)
    {
        VirtualPath path = VirtualPath.Parse($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/{entry}");
        FileSystemResult<FileSystemContentReadHandle> read =
            await context.App.FileSystem.ReadAsync(new FileSystemReadRequest(path), cancellationToken);
        if (!read.Succeeded || read.Value is null)
        {
            return null;
        }

        await using FileSystemContentReadHandle handle = read.Value;
        using StreamReader reader = new(handle.Content, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>Reads and parses <c>/proc/&lt;pid&gt;/status</c>, or returns null if it vanished.</summary>
    private static async ValueTask<IReadOnlyDictionary<string, string>?> ReadStatusAsync(
        TerminalExecutionContext context,
        long pid,
        CancellationToken cancellationToken) =>
        await ReadTextAsync(context, pid, "status", cancellationToken) is { } text ? ParseStatus(text) : null;

    /// <summary>Parses the <c>Key:\tvalue</c> lines of a <c>/proc/&lt;pid&gt;/status</c> file.</summary>
    internal static IReadOnlyDictionary<string, string> ParseStatus(string text)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator > 0)
            {
                fields[line[..separator]] = line[(separator + 1)..].Trim();
            }
        }

        return fields;
    }

    private sealed record ProcEntry(long Pid, DateTimeOffset StartedAtUtc, IReadOnlyDictionary<string, string> Status, int Nice)
    {
        public string Field(string key) => Status.TryGetValue(key, out string? value) ? value : "?";
    }

    /// <summary>One output column: its header, its width (0 for the last, unpadded one) and its value.</summary>
    private sealed record Column(string Header, int Width, bool Right, Func<ProcEntry, DateTimeOffset, string> Value)
    {
        public string Pad(string text) => Width == 0 ? text : Right ? text.PadLeft(Width) : text.PadRight(Width);
    }
}
