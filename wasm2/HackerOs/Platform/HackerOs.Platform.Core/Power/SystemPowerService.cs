using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Power;

/// <summary>
/// The machine's "hardware": what a reboot or a factory reset finally does on the host. The
/// browser host reloads the page and deletes the OS IndexedDB database; tests record the calls.
/// </summary>
public interface ISystemPowerDevice
{
    /// <summary>Restarts the machine (the browser host reloads the page). May never return.</summary>
    /// <param name="cancellationToken">Cancels the request before it reached the host.</param>
    Task RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>Erases the whole persistent storage of the machine (the OS database).</summary>
    /// <param name="cancellationToken">Cancels the request before it reached the host.</param>
    Task EraseStorageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reboot and factory reset (ADR 0056, section 11), sequenced like <c>systemctl reboot</c>: flush
/// the pending log writes (ADR 0045), end the session so every process is cancelled, flush again
/// (the shutdown itself logs), then hand over to the <see cref="ISystemPowerDevice"/>.
/// </summary>
/// <remarks>
/// The caller (<c>AppSystemPowerGateway</c>) has already checked <c>system.power</c> and obtained an
/// administrator from the OS authentication agent. Once started, the sequence ignores
/// cancellation: the calling app's own token is cancelled by the session shutdown.
/// </remarks>
public sealed class SystemPowerService
{
    private readonly ISystemPowerDevice _device;
    private readonly ISessionService _session;
    private readonly LogFileWriter? _logs;
    private readonly IAuditLog? _auditLog;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="device">Host side of the power operations.</param>
    /// <param name="session">Session ended before the restart.</param>
    /// <param name="logs">Log writer flushed before the restart; <see langword="null"/> when logs are in memory only.</param>
    /// <param name="auditLog">Audit log recording <c>system.reboot</c> and <c>system.factory-reset</c>.</param>
    /// <param name="clock">Clock for audit timestamps.</param>
    public SystemPowerService(
        ISystemPowerDevice device,
        ISessionService session,
        LogFileWriter? logs = null,
        IAuditLog? auditLog = null,
        Func<DateTimeOffset>? clock = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logs = logs;
        _auditLog = auditLog;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Flushes, ends the session and restarts the machine.</summary>
    /// <param name="administratorLogin">Administrator who authorized it, for the audit log.</param>
    /// <param name="requestedBy">Session user who asked.</param>
    public async Task RebootAsync(string administratorLogin, AuthenticatedPrincipal? requestedBy)
    {
        Record("system.reboot", administratorLogin, requestedBy);
        await ShutdownAsync().ConfigureAwait(false);
        await _device.RestartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Flushes, ends the session, erases the storage and restarts the machine.</summary>
    /// <param name="administratorLogin">Administrator who authorized it, for the audit log.</param>
    /// <param name="requestedBy">Session user who asked.</param>
    public async Task FactoryResetAsync(string administratorLogin, AuthenticatedPrincipal? requestedBy)
    {
        // The entry is written before the wipe: it only survives in an external log (server sync),
        // like the last lines of a disk being formatted.
        Record("system.factory-reset", administratorLogin, requestedBy);
        await ShutdownAsync().ConfigureAwait(false);
        await _device.EraseStorageAsync(CancellationToken.None).ConfigureAwait(false);
        await _device.RestartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The sign-in screen's « Forgot password? » (ADR 0226): the same erase and restart as
    /// <see cref="FactoryResetAsync"/>, asked by nobody signed in. No administrator authorized it,
    /// so the audit entry says where it came from (<c>origin = sign-in</c>) instead.
    /// </summary>
    /// <remarks>
    /// The host offers it only when its deployment allows it
    /// (<c>HackerOs:SignIn:FactoryReset:Enabled</c>): anyone at the sign-in screen can erase the
    /// machine, as anyone at a real computer can reinstall it.
    /// </remarks>
    public async Task FactoryResetFromSignInAsync()
    {
        Record("system.factory-reset", "-", requestedBy: null, origin: "sign-in");
        await ShutdownAsync().ConfigureAwait(false);
        await _device.EraseStorageAsync(CancellationToken.None).ConfigureAwait(false);
        await _device.RestartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Flushes, ends the session, runs <paramref name="whileStopped"/> with no process left, then
    /// restarts the machine: the shape of a restore from backup (ADR 0099, section 1). The caller
    /// audits the operation itself. Restarts even when <paramref name="whileStopped"/> throws.
    /// </summary>
    /// <param name="whileStopped">Work done between the end of the session and the restart.</param>
    public async Task RestartAfterAsync(Func<Task> whileStopped)
    {
        ArgumentNullException.ThrowIfNull(whileStopped);
        await ShutdownAsync().ConfigureAwait(false);
        try
        {
            await whileStopped().ConfigureAwait(false);
        }
        finally
        {
            await _device.RestartAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ShutdownAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        try
        {
            // Cancels every process's token (the caller's included) and publishes SessionShutDownEvent.
            await _session.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Already stopped, or in a transition: the restart below ends everything anyway.
        }

        await FlushAsync().ConfigureAwait(false);
    }

    private async Task FlushAsync()
    {
        if (_logs is null)
        {
            return;
        }

        try
        {
            await _logs.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // A log that cannot be flushed must not prevent the restart.
        }
    }

    private void Record(string action, string administratorLogin, AuthenticatedPrincipal? requestedBy, string? origin = null)
    {
        if (_auditLog is null)
        {
            return;
        }

        Dictionary<string, string> properties = new(StringComparer.Ordinal)
        {
            ["admin"] = administratorLogin,
            ["requestedBy"] = requestedBy?.LoginName.Value ?? "-"
        };
        if (origin is not null)
        {
            properties["origin"] = origin;
        }

        _auditLog.Record(new AuditEntry(
            _clock(),
            Guid.NewGuid(),
            requestedBy?.UserId.ToString() ?? "system",
            action,
            "machine",
            AuditOutcome.Success,
            properties));
    }
}
