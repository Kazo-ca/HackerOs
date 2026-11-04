using Kazo.Os.Platform.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Reads and writes the remembered dangerous-capability decisions of ADR 0049 section 4.
/// </summary>
public interface IAppPermissionStore
{
    /// <summary>
    /// Reads the machine file and the session user's file. Never fails: an absent file has no
    /// decisions, and every ignored line is logged once to <c>/var/log/syslog</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    ValueTask<AppPermissionDecisions> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records machine decisions in <c>/etc/kazoos/app-permissions.config</c> as an audited
    /// system operation (ADR 0002). Callers must have verified an administrator first: this is how
    /// the write succeeds even when the session user is not an administrator.
    /// </summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capabilities">Dangerous capabilities to record.</param>
    /// <param name="decision">Decision to record, or <see langword="null"/> to remove the lines.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask WriteMachineDecisionsAsync(
        string appId,
        IReadOnlyCollection<string> capabilities,
        AppPermissionDecision? decision,
        CancellationToken cancellationToken = default);

    /// <summary>Records <c>deny</c> lines in the session user's own file.</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capabilities">Dangerous capabilities the user refused.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask WriteUserDenialsAsync(
        string appId, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken = default);

    /// <summary>Removes one of the session user's own <c>deny</c> lines.</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability whose refusal is withdrawn.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RemoveUserDenialAsync(string appId, string capability, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IAppPermissionStore"/> over <see cref="IOsSettingsService"/>: the machine file
/// is read and written as an audited system operation, the user file with the session user's own
/// rights, so the user can always read, edit or delete it with <c>nano</c>.
/// </summary>
public sealed class AppPermissionStore : IAppPermissionStore
{
    private readonly IOsSettingsService _settings;
    private readonly ILogger _logger;
    private readonly HashSet<string> _loggedWarnings = new(StringComparer.Ordinal);
    private readonly Lock _warningGate = new();

    /// <summary>Creates the store.</summary>
    /// <param name="settings">OS settings files.</param>
    /// <param name="logger">Optional logger receiving one warning per ignored line.</param>
    public AppPermissionStore(IOsSettingsService settings, ILogger<AppPermissionStore>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<AppPermissionDecisions> ReadAsync(CancellationToken cancellationToken = default)
    {
        string? machine = await _settings.ReadFileAsync(AppPermissionsDocument.MachineFile, cancellationToken).ConfigureAwait(false);
        string? user = await _settings.ReadFileAsync(AppPermissionsDocument.UserFile, cancellationToken).ConfigureAwait(false);
        string? home = _settings.CurrentHomeDirectory;
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            machine,
            user,
            AppPermissionsDocument.MachineFile.Resolve(null).Value,
            home is null ? "~/.config/kazoos/" + AppPermissionsDocument.FileName : AppPermissionsDocument.UserFile.Resolve(home).Value);

        foreach (string warning in decisions.Warnings)
        {
            bool first;
            lock (_warningGate)
            {
                first = _loggedWarnings.Add(warning);
            }

            if (first)
            {
                _logger.LogWarning("{SettingsWarning}", warning);
            }
        }

        return decisions;
    }

    /// <inheritdoc />
    public async ValueTask WriteMachineDecisionsAsync(
        string appId,
        IReadOnlyCollection<string> capabilities,
        AppPermissionDecision? decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count == 0)
        {
            return;
        }

        string? content = await _settings.ReadFileAsync(AppPermissionsDocument.MachineFile, cancellationToken).ConfigureAwait(false);
        string edited = content ?? AppPermissionsDocument.DefaultMachineContent;
        foreach (string capability in capabilities)
        {
            edited = decision is AppPermissionDecision value
                ? AppPermissionsDocument.SetDecision(edited, AppPermissionsDocument.DefaultMachineContent, appId, capability, value)
                : AppPermissionsDocument.RemoveDecision(edited, appId, capability);
        }

        // writer: null on a System file = audited system operation (OsSettingsService.WriteFileAsync).
        await _settings.WriteFileAsync(AppPermissionsDocument.MachineFile, edited, writer: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WriteUserDenialsAsync(
        string appId, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count == 0)
        {
            return;
        }

        string? content = await _settings.ReadFileAsync(AppPermissionsDocument.UserFile, cancellationToken).ConfigureAwait(false);
        string edited = content ?? AppPermissionsDocument.DefaultUserContent;
        foreach (string capability in capabilities)
        {
            edited = AppPermissionsDocument.SetDecision(
                edited, AppPermissionsDocument.DefaultUserContent, appId, capability, AppPermissionDecision.Deny);
        }

        await _settings.WriteFileAsync(AppPermissionsDocument.UserFile, edited, writer: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RemoveUserDenialAsync(string appId, string capability, CancellationToken cancellationToken = default)
    {
        string? content = await _settings.ReadFileAsync(AppPermissionsDocument.UserFile, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return;
        }

        string edited = AppPermissionsDocument.RemoveDecision(content, appId, capability);
        if (!string.Equals(edited, content, StringComparison.Ordinal))
        {
            await _settings.WriteFileAsync(AppPermissionsDocument.UserFile, edited, writer: null, cancellationToken).ConfigureAwait(false);
        }
    }
}
