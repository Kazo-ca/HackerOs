using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Administration;

/// <summary>
/// Backs up the whole machine to the host computer and restores it (ADR 0099, section 1): every
/// user's files, <c>/etc</c>, the accounts and their password hashes, in one gzip archive with the
/// <c>.kosbak</c> extension.
/// </summary>
/// <remarks>
/// <para>
/// Every member requires the dangerous capability <c>system.backup</c> and an administrator: the
/// OS asks for a password in its own secure window, like a system backup needs root under Linux. A
/// process already running as root through <c>sudo</c> is not asked again. A refusal raises
/// <see cref="KazoUnauthorizedAccessException"/> and nothing happens.
/// </para>
/// <para>
/// The archive goes to the host, never into the OS's own files: it would exceed the 16 MiB limit
/// of a file (ADR 0094), and a copy inside the very database it saves would make no sense.
/// Opening the host's file picker needs a recent user gesture (a click, or Enter in a terminal).
/// </para>
/// <para>
/// A restore, merge or replace, ends the session (every process is cancelled, this app included)
/// and restarts the machine, so the task of <see cref="RestoreAsync(KazoBackupRestoreMode, CancellationToken)"/>
/// may never complete from the caller's point of view. Each backup and restore is written to
/// <c>/var/log/auth.log</c> (who, when, mode).
/// </para>
/// </remarks>
public static class KazoSystemBackup
{
    /// <summary>Gets whether the current host can back up and restore the machine.</summary>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static bool IsSupported => IsSupportedFor(KazoExecutionContext.Current);

    /// <summary>Gets whether the host of <paramref name="context"/> can back up and restore the machine.</summary>
    /// <param name="context">The calling app's execution context.</param>
    public static bool IsSupportedFor(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.SystemBackup.IsSupported;
    }

    /// <summary>Exports the machine and downloads the archive to the host after an administrator authenticated.</summary>
    /// <param name="cancellationToken">Cancels the authentication or the export.</param>
    /// <returns>What was downloaded.</returns>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>system.backup</c>, or no administrator authenticated.</exception>
    /// <exception cref="NotSupportedException">This host cannot back up the machine.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<KazoBackupInfo> CreateAsync(CancellationToken cancellationToken = default) =>
        CreateAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Creates a backup through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the authentication or the export.</param>
    public static Task<KazoBackupInfo> CreateAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.CreateAsync(cancellationToken));

    /// <summary>
    /// Lets the user pick an archive on the host, validates it, asks an administrator, then ends the
    /// session, restores the machine and restarts it.
    /// </summary>
    /// <param name="mode">Merge into or replace the current machine.</param>
    /// <param name="cancellationToken">Cancels the picker or the authentication.</param>
    /// <returns><see langword="null"/> when the user closed the picker; otherwise the machine restarts.</returns>
    /// <exception cref="KazoBackupInvalidException">The file is not a backup this Kazo OS can restore; nothing changed.</exception>
    /// <exception cref="InvalidOperationException">The browser refused to open its file picker (no recent click or key press).</exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>system.backup</c>, or no administrator authenticated.</exception>
    /// <exception cref="NotSupportedException">This host cannot restore the machine.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<KazoBackupInfo?> RestoreAsync(KazoBackupRestoreMode mode, CancellationToken cancellationToken = default) =>
        RestoreAsync(KazoExecutionContext.Current, mode, cancellationToken);

    /// <summary>Restores a backup through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="mode">Merge into or replace the current machine.</param>
    /// <param name="cancellationToken">Cancels the picker or the authentication.</param>
    public static Task<KazoBackupInfo?> RestoreAsync(
        IAppExecutionContext context, KazoBackupRestoreMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown restore mode.");
        }

        return Call(context, gateway => gateway.RestoreAsync(mode, cancellationToken));
    }

    private static async Task<T> Call<T>(IAppExecutionContext context, Func<IAppSystemBackupGateway, ValueTask<T>> call)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await call(context.SystemBackup).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
    }
}
