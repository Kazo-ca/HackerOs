using System.Globalization;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Platform.Core.Power;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Backup;

/// <summary>
/// The host side of a whole-machine backup (ADR 0099, section 1): the database export and import,
/// and the host computer's download and file picker. The browser host implements it over
/// <c>IndexedDbBackupService</c> and the SDK Blazor <c>download.js</c>/<c>pickFile.js</c> modules;
/// Platform Core never touches the database itself.
/// </summary>
public interface ISystemBackupDevice
{
    /// <summary>Exports the whole OS database as a compressed <c>.kosbak</c> archive.</summary>
    /// <param name="cancellationToken">Cancels the export.</param>
    Task<SystemBackupArchive> ExportAsync(CancellationToken cancellationToken = default);

    /// <summary>Hands the archive to the host computer (a browser download).</summary>
    /// <param name="fileName">Name proposed to the host.</param>
    /// <param name="archive">Archive bytes.</param>
    /// <param name="cancellationToken">Cancels the transfer before it reached the host.</param>
    Task SaveToHostAsync(string fileName, byte[] archive, CancellationToken cancellationToken = default);

    /// <summary>Opens the host's file picker; <see langword="null"/> when the user closed it.</summary>
    /// <param name="cancellationToken">Cancels the picker.</param>
    Task<SystemBackupFile?> PickFromHostAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates an archive completely without touching the database.</summary>
    /// <param name="archive">Archive bytes (gzip, or the plain JSON).</param>
    /// <exception cref="KazoBackupInvalidException">Not a backup this OS can restore.</exception>
    SystemBackupSummary Inspect(byte[] archive);

    /// <summary>Restores a validated archive in one database transaction.</summary>
    /// <param name="archive">Archive bytes.</param>
    /// <param name="mode">Merge or replace.</param>
    /// <param name="cancellationToken">Cancels before the transaction started.</param>
    Task RestoreAsync(byte[] archive, KazoBackupRestoreMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps the record of a restore outside the database, where the restore cannot overwrite it,
    /// until the next boot writes it to <c>/var/log/auth.log</c>.
    /// </summary>
    /// <param name="record">The record to keep.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SaveRestoreRecordAsync(SystemRestoreRecord record, CancellationToken cancellationToken = default);

    /// <summary>Reads and forgets the kept restore record; <see langword="null"/> when there is none.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<SystemRestoreRecord?> TakeRestoreRecordAsync(CancellationToken cancellationToken = default);
}

/// <summary>An exported archive and what it holds.</summary>
/// <param name="Bytes">The <c>.kosbak</c> bytes.</param>
/// <param name="Summary">What the archive holds.</param>
public sealed record SystemBackupArchive(byte[] Bytes, SystemBackupSummary Summary);

/// <summary>A file the user picked on the host.</summary>
/// <param name="Name">File name on the host.</param>
/// <param name="Bytes">File content.</param>
public sealed record SystemBackupFile(string Name, byte[] Bytes);

/// <summary>What a validated archive holds.</summary>
/// <param name="CreatedAtUtc">When it was exported.</param>
/// <param name="RecordCount">Database records it holds.</param>
public sealed record SystemBackupSummary(DateTimeOffset CreatedAtUtc, int RecordCount);

/// <summary>A picked and validated archive, ready to restore.</summary>
/// <param name="File">The picked file.</param>
/// <param name="Summary">What it holds.</param>
public sealed record SystemBackupCandidate(SystemBackupFile File, SystemBackupSummary Summary)
{
    /// <summary>Describes the candidate for the SDK.</summary>
    public KazoBackupInfo ToInfo() =>
        new(File.Name, File.Bytes.LongLength, Summary.RecordCount, Summary.CreatedAtUtc);
}

/// <summary>A restore, kept across the restart until <c>/var/log/auth.log</c> receives it.</summary>
/// <param name="AtUtc">When the restore ran.</param>
/// <param name="Administrator">Administrator who authorized it.</param>
/// <param name="RequestedBy">Login of the session user who asked.</param>
/// <param name="RequestedById">Id of that user, the audit actor.</param>
/// <param name="Mode">Merge or replace.</param>
/// <param name="FileName">Archive name on the host.</param>
/// <param name="RecordCount">Records in the archive.</param>
/// <param name="Outcome"><c>success</c>, or <c>failure:&lt;reason&gt;</c>.</param>
public sealed record SystemRestoreRecord(
    DateTimeOffset AtUtc,
    string Administrator,
    string RequestedBy,
    string RequestedById,
    KazoBackupRestoreMode Mode,
    string FileName,
    int RecordCount,
    string Outcome);

/// <summary>
/// Whole-machine backup and restore (ADR 0099, section 1), sequenced like a system backup tool.
/// </summary>
/// <remarks>
/// <para>
/// <b>Create.</b> Export the database, hand the archive to the host, write
/// <c>system.backup.create</c> to <c>/var/log/auth.log</c>.
/// </para>
/// <para>
/// <b>Restore.</b> Pick and validate the archive first (a bad file changes nothing and stops
/// nothing); once an administrator agreed, end the session through <see cref="SystemPowerService"/>
/// (logs flushed, every process cancelled), restore in one transaction, then restart. Merge and
/// replace both restart: the running OS keeps paths, settings and accounts in memory that the
/// restore just changed under it. The restore record is kept outside the database
/// (<see cref="ISystemBackupDevice.SaveRestoreRecordAsync"/>) because a replace overwrites
/// <c>/var/log/auth.log</c> itself; the next boot writes it (<see cref="WritePendingRestoreRecordAsync"/>).
/// </para>
/// <para>
/// The caller (<c>AppSystemBackupGateway</c>) has already checked <c>system.backup</c> and obtained
/// an administrator from the OS authentication agent.
/// </para>
/// </remarks>
public sealed class SystemBackupService
{
    /// <summary>Audit action of a backup.</summary>
    public const string CreateAction = "system.backup.create";

    /// <summary>Audit action of a restore.</summary>
    public const string RestoreAction = "system.backup.restore";

    private readonly ISystemBackupDevice _device;
    private readonly SystemPowerService _power;
    private readonly IAuditLog? _auditLog;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="device">Host side of the backup.</param>
    /// <param name="power">Ends the session and restarts the machine around a restore.</param>
    /// <param name="auditLog">Audit log receiving <see cref="CreateAction"/> and <see cref="RestoreAction"/>.</param>
    /// <param name="clock">Clock for the archive name and the audit timestamps.</param>
    public SystemBackupService(
        ISystemBackupDevice device,
        SystemPowerService power,
        IAuditLog? auditLog = null,
        Func<DateTimeOffset>? clock = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _auditLog = auditLog;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Gets the archive name for a backup made at <paramref name="atUtc"/>: <c>kazoos-20261017-071200Z.kosbak</c>.</summary>
    /// <param name="atUtc">Export time.</param>
    public static string FileNameFor(DateTimeOffset atUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"kazoos-{atUtc.ToUniversalTime():yyyyMMdd-HHmmss}Z.kosbak");

    /// <summary>Exports the machine, hands the archive to the host and audits it.</summary>
    /// <param name="administratorLogin">Administrator who authorized it.</param>
    /// <param name="requestedBy">Session user who asked.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    public async Task<KazoBackupInfo> CreateAsync(
        string administratorLogin, AuthenticatedPrincipal? requestedBy, CancellationToken cancellationToken = default)
    {
        SystemBackupArchive archive = await _device.ExportAsync(cancellationToken).ConfigureAwait(false);
        string fileName = FileNameFor(archive.Summary.CreatedAtUtc);
        await _device.SaveToHostAsync(fileName, archive.Bytes, cancellationToken).ConfigureAwait(false);
        Record(_clock(), CreateAction, requestedBy?.UserId.ToString() ?? "system", AuditOutcome.Success, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["admin"] = administratorLogin,
            ["requestedBy"] = requestedBy?.LoginName.Value ?? "-",
            ["file"] = fileName,
            ["bytes"] = archive.Bytes.LongLength.ToString(CultureInfo.InvariantCulture),
            ["records"] = archive.Summary.RecordCount.ToString(CultureInfo.InvariantCulture)
        });
        return new KazoBackupInfo(fileName, archive.Bytes.LongLength, archive.Summary.RecordCount, archive.Summary.CreatedAtUtc);
    }

    /// <summary>Lets the user pick an archive on the host and validates it; nothing is changed.</summary>
    /// <param name="cancellationToken">Cancels the picker.</param>
    /// <returns><see langword="null"/> when the user closed the picker.</returns>
    /// <exception cref="KazoBackupInvalidException">Not a backup this OS can restore.</exception>
    public async Task<SystemBackupCandidate?> PickAsync(CancellationToken cancellationToken = default)
    {
        SystemBackupFile? file = await _device.PickFromHostAsync(cancellationToken).ConfigureAwait(false);
        return file is null ? null : new SystemBackupCandidate(file, _device.Inspect(file.Bytes));
    }

    /// <summary>Ends the session, restores the candidate, then restarts the machine. May never return.</summary>
    /// <param name="candidate">A picked and validated archive.</param>
    /// <param name="mode">Merge or replace.</param>
    /// <param name="administratorLogin">Administrator who authorized it.</param>
    /// <param name="requestedBy">Session user who asked.</param>
    public async Task RestoreAsync(
        SystemBackupCandidate candidate,
        KazoBackupRestoreMode mode,
        string administratorLogin,
        AuthenticatedPrincipal? requestedBy)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        SystemRestoreRecord record = new(
            _clock(),
            administratorLogin,
            requestedBy?.LoginName.Value ?? "-",
            requestedBy?.UserId.ToString() ?? "system",
            mode,
            candidate.File.Name,
            candidate.Summary.RecordCount,
            "started");

        // Once the session ends, the caller's token is cancelled: nothing below observes it.
        await _power.RestartAfterAsync(async () =>
        {
            await _device.SaveRestoreRecordAsync(record, CancellationToken.None).ConfigureAwait(false);
            string outcome;
            try
            {
                await _device.RestoreAsync(candidate.File.Bytes, mode, CancellationToken.None).ConfigureAwait(false);
                outcome = "success";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The restore is one transaction: on failure the machine is unchanged. Restart anyway,
                // the session is already gone, and say why in auth.log at the next boot.
                outcome = $"failure:{exception.GetType().Name}: {exception.Message}";
            }

            await _device.SaveRestoreRecordAsync(record with { Outcome = outcome }, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the restore kept across the restart to <c>/var/log/auth.log</c>; called once at boot.
    /// A host that cannot keep the record has nothing to write.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task WritePendingRestoreRecordAsync(CancellationToken cancellationToken = default)
    {
        SystemRestoreRecord? record = await _device.TakeRestoreRecordAsync(cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return;
        }

        bool succeeded = record.Outcome == "success";
        Record(record.AtUtc, RestoreAction, record.RequestedById, succeeded ? AuditOutcome.Success : AuditOutcome.Failed, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["admin"] = record.Administrator,
            ["requestedBy"] = record.RequestedBy,
            ["mode"] = record.Mode == KazoBackupRestoreMode.Replace ? "replace" : "merge",
            ["file"] = record.FileName,
            ["records"] = record.RecordCount.ToString(CultureInfo.InvariantCulture),
            ["outcome"] = record.Outcome
        });
    }

    private void Record(DateTimeOffset at, string action, string actor, AuditOutcome outcome, IReadOnlyDictionary<string, string> details) =>
        _auditLog?.Record(new AuditEntry(at, Guid.NewGuid(), actor, action, "machine", outcome, details));
}
