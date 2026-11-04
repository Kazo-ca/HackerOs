using System.Globalization;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Apps.ErrorLogViewer;

/// <summary>One log file the viewer can show: an active log or one of its numbered archives.</summary>
/// <param name="Path">Canonical file path.</param>
/// <param name="Label">Short label shown in the source picker, e.g. <c>syslog</c> or <c>apps/org.x.log.1</c>.</param>
/// <param name="IsArchive">Whether the file is a rotated archive (<c>.1</c> to <c>.5</c>), never written again.</param>
public sealed record LogSource(VirtualPath Path, string Label, bool IsArchive)
{
    /// <summary>
    /// Gets whether only root may read the file (<c>audit.log</c>, ADR 0100 section 3): it is read
    /// only when picked on its own, never as part of "every log", so opening the viewer never asks
    /// for an administrator.
    /// </summary>
    public bool RequiresRoot { get; init; }
}

/// <summary>One parsed log line and the file it came from.</summary>
/// <param name="Line">Parsed line.</param>
/// <param name="Source">File the line was read from.</param>
/// <param name="LineNumber">One-based line number within that file, used as a stable tiebreaker.</param>
public sealed record LogRow(LogLine Line, LogSource Source, int LineNumber);

/// <summary>The parsed content of one log file.</summary>
/// <param name="Source">File that was read.</param>
/// <param name="Rows">Parsed lines, in file order.</param>
/// <param name="UnparsedLineCount">Non-blank lines that do not follow the ADR 0045 format.</param>
/// <param name="Error">Filesystem error when the file could not be read; rows are then empty.</param>
public sealed record LogFileContent(
    LogSource Source,
    IReadOnlyList<LogRow> Rows,
    int UnparsedLineCount,
    FileSystemError? Error);

/// <summary>
/// Reads the platform logs under <c>/var/log</c> (ADR 0045, section 6) with this app's own,
/// capability-checked filesystem gateway: <c>syslog</c>, every <c>apps/*.log</c>, and on demand their
/// archives. Lines are decoded with <see cref="LogLineFormat.TryParse"/>. The reader never touches
/// <c>auth.log</c> (mode <c>0600</c>): reading it is an administrator action, done with <c>sudo cat</c>.
/// It does list <c>audit.log</c> (ADR 0100 section 3, also <c>0600</c>, decoded with
/// <see cref="AuditLineFormat.TryParse"/>): opening it is denied for everybody but root, and the
/// window host then offers the administrator approval of ADR 0042 for that one read.
/// </summary>
/// <remarks>
/// This deliberately goes through <see cref="IAppFileSystemGateway"/>, like any other app: the viewer
/// gets no special path into the logs. It moves to the <c>HackFile</c> API of ADR 0043 once that API
/// lands on the rebuild branch.
/// </remarks>
public sealed class LogFileReader
{
    /// <summary>Canonical path of the platform log.</summary>
    public static readonly VirtualPath SyslogPath = VirtualPath.Parse("/var/log/syslog");

    /// <summary>Canonical directory of the per-app logs.</summary>
    public static readonly VirtualPath AppLogDirectory = VirtualPath.Parse("/var/log/apps");

    /// <summary>Canonical log directory.</summary>
    public static readonly VirtualPath LogDirectory = VirtualPath.Parse("/var/log");

    /// <summary>Highest archive number kept by rotation.</summary>
    public const int MaxArchives = 5;

    private readonly IAppFileSystemGateway _fileSystem;

    /// <summary>Initializes a reader over the calling app's filesystem gateway.</summary>
    public LogFileReader(IAppFileSystemGateway fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>
    /// Lists the log files that exist now: <c>syslog</c> then each app log in name order, each
    /// followed by its archives when <paramref name="includeArchives"/> is set.
    /// </summary>
    /// <param name="includeArchives">Also list the rotated archives.</param>
    /// <param name="includeAudit">Also list <c>audit.log</c> (for an administrator, ADR 0100 section 3).</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    public async Task<IReadOnlyList<LogSource>> ListSourcesAsync(
        bool includeArchives, bool includeAudit = false, CancellationToken cancellationToken = default)
    {
        List<LogSource> sources = [];

        FileSystemResult<FileSystemDirectorySnapshot> logDirectory = await _fileSystem.EnumerateAsync(
            new FileSystemEnumerateRequest(LogDirectory), cancellationToken).ConfigureAwait(false);
        if (logDirectory.Succeeded)
        {
            AddFamily(sources, logDirectory.Value!, "syslog", label => label, includeArchives);
            if (includeAudit)
            {
                // ADR 0100 section 3: 0600 root, so reading it asks for the administrator approval.
                int first = sources.Count;
                AddFamily(sources, logDirectory.Value!, "audit.log", label => label, includeArchives);
                for (int index = first; index < sources.Count; index++)
                {
                    sources[index] = sources[index] with { RequiresRoot = true };
                }
            }
        }

        FileSystemResult<FileSystemDirectorySnapshot> appDirectory = await _fileSystem.EnumerateAsync(
            new FileSystemEnumerateRequest(AppLogDirectory), cancellationToken).ConfigureAwait(false);
        if (appDirectory.Succeeded)
        {
            foreach (FileSystemDirectoryItem item in appDirectory.Value!.Entries)
            {
                if (item.Metadata.Kind == FileSystemEntryKind.File
                    && item.Name.Value.EndsWith(".log", StringComparison.Ordinal))
                {
                    AddFamily(sources, appDirectory.Value, item.Name.Value, label => $"apps/{label}", includeArchives);
                }
            }
        }

        return sources;
    }

    /// <summary>Reads and parses one log file. A missing or unreadable file returns an empty result with its error.</summary>
    public async Task<LogFileContent> ReadAsync(LogSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        FileSystemResult<FileSystemContentReadHandle> read = await _fileSystem.ReadAsync(
            new FileSystemReadRequest(source.Path), cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded || read.Value is null)
        {
            return new LogFileContent(source, [], 0, read.Error);
        }

        string text;
        await using (FileSystemContentReadHandle handle = read.Value)
        {
            using StreamReader reader = new(handle.Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        return Parse(source, text);
    }

    /// <summary>
    /// Truncates an active log to zero bytes (<c>truncate -s 0</c>). Logs belong to
    /// <c>system:system</c>, so for an ordinary user this is denied and the window host offers the
    /// administrator approval of ADR 0042; the caller retries after approval.
    /// </summary>
    public async Task<FileSystemMutationResult> TruncateAsync(LogSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsArchive)
        {
            throw new InvalidOperationException("Archives are never truncated; only active logs are.");
        }

        return await _fileSystem.WriteAsync(
            new FileSystemWriteRequest(source.Path), EmptyContent.Instance, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Splits <paramref name="text"/> into lines and parses each one.</summary>
    public static LogFileContent Parse(LogSource source, string text)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(text);

        List<LogRow> rows = [];
        int unparsed = 0;
        int lineNumber = 0;
        foreach (string line in text.Split(LogLineFormat.LineTerminator))
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            if (LogLineFormat.TryParse(line, out LogLine? parsed))
            {
                rows.Add(new LogRow(parsed, source, lineNumber));
            }
            else if (AuditLineFormat.TryParse(line, out AuditRecord? audit))
            {
                rows.Add(new LogRow(ToLogLine(audit), source, lineNumber));
            }
            else
            {
                unparsed++;
            }
        }

        return new LogFileContent(source, rows, unparsed, null);
    }

    /// <summary>
    /// Shows one <c>audit.log</c> record (ADR 0100 section 3) as a log row: a refused write is a
    /// warning, an allowed one information; the source is the app that wrote.
    /// </summary>
    private static LogLine ToLogLine(AuditRecord audit)
    {
        string target = audit.NewPath is null ? audit.Path : $"{audit.Path} -> {audit.NewPath}";
        string outcome = audit.Succeeded ? "success" : $"refused ({audit.Error})";
        Dictionary<string, string> properties = new(StringComparer.Ordinal)
        {
            ["op"] = audit.Operation,
            ["path"] = audit.Path,
            ["uid"] = audit.Uid,
            ["auid"] = audit.Auid
        };
        return new LogLine(
            audit.TimestampUtc,
            audit.Succeeded ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            audit.AppId,
            audit.ProcessId == 0 ? null : audit.ProcessId,
            $"audit #{audit.Serial}: {audit.Operation} {target} uid={audit.Uid} auid={audit.Auid}: {outcome}",
            properties);
    }

    /// <summary>Adds <paramref name="fileName"/> if present, then its existing archives in order.</summary>
    private static void AddFamily(
        List<LogSource> sources,
        FileSystemDirectorySnapshot directory,
        string fileName,
        Func<string, string> label,
        bool includeArchives)
    {
        HashSet<string> files = directory.Entries
            .Where(static item => item.Metadata.Kind == FileSystemEntryKind.File)
            .Select(static item => item.Name.Value)
            .ToHashSet(StringComparer.Ordinal);
        string prefix = directory.Path.Value;

        if (files.Contains(fileName))
        {
            sources.Add(new LogSource(VirtualPath.Parse($"{prefix}/{fileName}"), label(fileName), IsArchive: false));
        }

        if (!includeArchives)
        {
            return;
        }

        for (int index = 1; index <= MaxArchives; index++)
        {
            string archive = $"{fileName}.{index.ToString(CultureInfo.InvariantCulture)}";
            if (files.Contains(archive))
            {
                sources.Add(new LogSource(VirtualPath.Parse($"{prefix}/{archive}"), label(archive), IsArchive: true));
            }
        }
    }

    private sealed class EmptyContent : IFileSystemContentSource
    {
        public static readonly EmptyContent Instance = new();

        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

        public long? Length => 0;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream([], writable: false));
        }
    }
}
