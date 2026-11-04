using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// Supplies the resource limits of a login session (ADR 0125, section 3), like <c>pam_limits</c> at
/// login: <c>/etc/security/limits.conf</c>, then <c>/etc/security/limits.d/*.conf</c> in alphabetical
/// order, applied over the kernel's defaults for the session's user and groups.
/// </summary>
/// <remarks>
/// The files are read once per session, at its first root launch: editing them affects the next
/// session, never processes already running. Invalid lines are skipped and recorded once in
/// <c>/var/log/auth.log</c>, where <c>pam_limits</c> writes them.
/// </remarks>
public sealed class LoginResourceLimitsProvider
{
    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly IProcessResourceLimitTable? _table;
    private readonly Func<AuthenticatedPrincipal, CancellationToken, ValueTask<IReadOnlyCollection<string>>>? _groupNames;
    private readonly IAuditLog? _auditLog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SessionId? _sessionId;
    private ProcessResourceLimits? _limits;

    /// <summary>Creates the provider.</summary>
    /// <param name="fileSystem">Filesystem engine the files are read from, as root.</param>
    /// <param name="clock">Simulation clock for authorization and log timestamps.</param>
    /// <param name="table">
    /// The limit table: its <see cref="IProcessResourceLimitTable.IsEnabled"/> and kernel defaults. When
    /// <see langword="null"/>, the kernel defaults of ADR 0125 apply.
    /// </param>
    /// <param name="groupNames">Resolves the names of a user's groups, for <c>@group</c> rules; none when omitted.</param>
    /// <param name="auditLog">Optional <c>/var/log/auth.log</c> for invalid lines.</param>
    public LoginResourceLimitsProvider(
        IFileSystemService fileSystem,
        ISimulationClock clock,
        IProcessResourceLimitTable? table = null,
        Func<AuthenticatedPrincipal, CancellationToken, ValueTask<IReadOnlyCollection<string>>>? groupNames = null,
        IAuditLog? auditLog = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _table = table;
        _groupNames = groupNames;
        _auditLog = auditLog;
    }

    /// <summary>
    /// Returns the limits of <paramref name="principal"/>'s session, reading the files the first time
    /// the session asks.
    /// </summary>
    /// <param name="principal">Authenticated principal whose session starts a root process.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask<ProcessResourceLimits> GetAsync(AuthenticatedPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ProcessResourceLimits defaults = _table switch
        {
            null => ProcessResourceLimits.KernelDefaults,
            { IsEnabled: false } => ProcessResourceLimits.None,
            InMemoryProcessResourceLimitTable known => known.KernelDefaults,
            _ => ProcessResourceLimits.KernelDefaults
        };
        if (_table is { IsEnabled: false })
        {
            return defaults;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId == principal.SessionId && _limits is not null)
            {
                return _limits;
            }

            IAppFileSystemGateway root = new AppFileSystemGateway(_fileSystem, _clock, RootOperation(), ["system"]);
            List<string> warnings = [];
            List<ResourceLimitRule> rules = [];
            string? main = (await SettingsTextFile.ReadAsync(root, ResourceLimitsConfiguration.LimitsFilePath, cancellationToken).ConfigureAwait(false)).Content;
            rules.AddRange(ResourceLimitsConfiguration.Parse(main, ResourceLimitsConfiguration.LimitsFilePath.Value, warnings));
            foreach (VirtualPath dropIn in await ListDropInsAsync(root, cancellationToken).ConfigureAwait(false))
            {
                string? content = (await SettingsTextFile.ReadAsync(root, dropIn, cancellationToken).ConfigureAwait(false)).Content;
                rules.AddRange(ResourceLimitsConfiguration.Parse(content, dropIn.Value, warnings));
            }

            IReadOnlyCollection<string> groups = _groupNames is null
                ? []
                : await _groupNames(principal, cancellationToken).ConfigureAwait(false);
            ProcessResourceLimits limits = ResourceLimitsConfiguration.Apply(rules, principal.LoginName.Value, groups, defaults, warnings);
            foreach (string warning in warnings)
            {
                _auditLog?.Record(new AuditEntry(
                    _clock.UtcNow, Guid.NewGuid(), "pam_limits", "limits.parse", principal.LoginName.Value, AuditOutcome.Failed,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["message"] = warning }));
            }

            _sessionId = principal.SessionId;
            _limits = limits;
            return limits;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The <c>*.conf</c> files of <c>limits.d</c>, alphabetical; none when the directory is missing.</summary>
    private static async ValueTask<IReadOnlyList<VirtualPath>> ListDropInsAsync(IAppFileSystemGateway root, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> listing = await root.EnumerateAsync(
            new FileSystemEnumerateRequest(ResourceLimitsConfiguration.LimitsDirectoryPath), cancellationToken).ConfigureAwait(false);
        if (!listing.Succeeded)
        {
            return [];
        }

        return
        [
            .. listing.Value!.Entries
                .Where(static item => item.Metadata.Kind == FileSystemEntryKind.File && item.Name.Value.EndsWith(".conf", StringComparison.Ordinal))
                .Select(static item => item.Name.Value)
                .Order(StringComparer.Ordinal)
                .Select(static name => VirtualPath.Parse($"{ResourceLimitsConfiguration.LimitsDirectoryPath.Value}/{name}"))
        ];
    }

    /// <summary>Reads as the kernel, like <c>pam_limits</c> running as root during login.</summary>
    private static AppOperationContext RootOperation() => new()
    {
        AppId = "org.kazoos.kernel",
        UserId = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };
}
