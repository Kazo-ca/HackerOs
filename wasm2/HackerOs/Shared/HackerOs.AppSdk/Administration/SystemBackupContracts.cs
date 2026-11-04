using HackerOs.AppSdk.IO;

namespace HackerOs.AppSdk.Administration;

/// <summary>
/// Whole-machine backup and restore for one app instance (ADR 0099, section 1), behind
/// <see cref="HackSystemBackup"/>. Every operation requires the dangerous capability
/// <c>system.backup</c> and an administrator through the OS authentication agent.
/// </summary>
/// <remarks>
/// The archive never enters the OS: creating a backup downloads it to the host computer, restoring
/// one opens the host's file picker, like a backup on an external disk.
/// </remarks>
public interface IAppSystemBackupGateway
{
    /// <summary>Gets whether this host can back up and restore the machine (the browser hosts can; the server-rendered host cannot).</summary>
    bool IsSupported { get; }

    /// <summary>Exports the whole machine and downloads the <c>.hosbak</c> archive to the host computer.</summary>
    /// <param name="cancellationToken">Cancels the authentication or the export.</param>
    /// <returns>What was downloaded.</returns>
    ValueTask<HackBackupInfo> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the host's file picker, validates the chosen archive, asks an administrator, then ends
    /// the session, restores and restarts the machine.
    /// </summary>
    /// <param name="mode">Merge into or replace the current machine.</param>
    /// <param name="cancellationToken">Cancels the picker or the authentication; once the restore started it cannot be cancelled.</param>
    /// <returns><see langword="null"/> when the user closed the picker without choosing a file; otherwise the machine restarts and the task may never complete.</returns>
    ValueTask<HackBackupInfo?> RestoreAsync(HackBackupRestoreMode mode, CancellationToken cancellationToken = default);
}

/// <summary>How a restore treats the current machine (ADR 0099, section 1).</summary>
public enum HackBackupRestoreMode
{
    /// <summary>The archive's records overwrite the records with the same key; every other record is kept.</summary>
    Merge = 1,

    /// <summary>Every store is emptied, then filled with the archive: the machine becomes exactly the backup.</summary>
    Replace = 2
}

/// <summary>One backup archive.</summary>
/// <param name="FileName">Name of the archive on the host, such as <c>hackeros-2026-10-17T0712Z.hosbak</c>.</param>
/// <param name="SizeBytes">Size of the archive, compressed.</param>
/// <param name="RecordCount">Database records it holds.</param>
/// <param name="CreatedAtUtc">When it was exported.</param>
public sealed record HackBackupInfo(string FileName, long SizeBytes, int RecordCount, DateTimeOffset CreatedAtUtc);

/// <summary>
/// The chosen file is not a backup this KazoOS can restore: not an archive, damaged (its SHA-256
/// does not match), or made by another database schema version. Nothing was changed.
/// </summary>
public sealed class HackBackupInvalidException : HackIOException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">Why the archive was refused.</param>
    /// <param name="innerException">Underlying failure, when there is one.</param>
    public HackBackupInvalidException(string message, Exception? innerException = null)
        : base(message, innerException: innerException)
    {
    }
}
