using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.FileSystem;

/// <summary>
/// Receives the protected writes <see cref="FileSystemService"/> sees (ADR 0100 section 3), like the
/// kernel's audit subsystem hands records to <c>auditd</c>.
/// </summary>
public interface IFileSystemAuditSink
{
    /// <summary>Records one mutation of an audited path, allowed or refused.</summary>
    /// <param name="operation"><c>create</c>, <c>write</c>, <c>delete</c>, <c>rename</c>, <c>copy</c> or <c>chmod</c>.</param>
    /// <param name="path">Path the operation targeted.</param>
    /// <param name="newPath">Destination of a rename or copy.</param>
    /// <param name="context">Who asked.</param>
    /// <param name="result">What happened.</param>
    void Record(string operation, VirtualPath path, VirtualPath? newPath, AppOperationContext context, FileSystemMutationResult result);
}

/// <summary>Which paths the audit watches (ADR 0100 section 3): <c>/etc</c>, <c>/bin</c> and <c>/var</c>, except <c>/var/log</c>.</summary>
public static class FileSystemAuditPolicy
{
    /// <summary>Gets whether a mutation of <paramref name="path"/> is written to the audit log.</summary>
    /// <param name="path">Absolute path.</param>
    public static bool IsAudited(VirtualPath path)
    {
        string value = path.Value;
        // /var/log is excluded so the log writer never audits itself (nor its rotation).
        return !IsWithin(value, "/var/log")
            && (IsWithin(value, "/etc") || IsWithin(value, "/bin") || IsWithin(value, "/var"));
    }

    private static bool IsWithin(string path, string root) =>
        path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}

/// <summary>
/// <see cref="IFileSystemAuditSink"/> writing <c>/var/log/audit.log</c> (mode <c>0600</c>, root) through
/// the platform's single log writer, with the same rotation as every log (ADR 0045, 1 MiB × 5).
/// </summary>
public sealed class FileSystemAuditLog : IFileSystemAuditSink
{
    private readonly LogFileWriter _writer;
    private readonly ISimulationClock _clock;
    private long _serial;

    /// <summary>Initializes the audit log.</summary>
    /// <param name="writer">The platform log writer.</param>
    /// <param name="clock">Clock for the event timestamps.</param>
    public FileSystemAuditLog(LogFileWriter writer, ISimulationClock clock)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public void Record(string operation, VirtualPath path, VirtualPath? newPath, AppOperationContext context, FileSystemMutationResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        bool succeeded = result.Transaction.Status == FileSystemTransactionStatus.Committed;
        bool human = !context.IsSystemOperation && !string.IsNullOrWhiteSpace(context.UserName);
        string login = human ? context.UserName! : "root";
        string appId = context.AppId;
        _writer.Enqueue(LogFileTarget.AuditLog, AuditLineFormat.Format(new AuditRecord(
            _clock.UtcNow,
            Interlocked.Increment(ref _serial),
            operation,
            path.Value,
            newPath?.Value,
            context.EffectiveAuthority == AppAuthority.System ? "root" : login,
            human ? login : AuditLineFormat.UnsetAuid,
            context.ProcessId ?? 0,
            appId[(appId.LastIndexOf('.') + 1)..],
            appId,
            succeeded,
            succeeded ? null : result.Transaction.Error?.Code.ToString())));
    }
}
