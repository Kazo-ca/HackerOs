using HackerOs.Platform.Core.Settings;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Policy;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// In-memory <see cref="ISessionService"/> implementing the ADR 0013 session lifecycle state
/// machine, owning the session root cancellation source and provisioning the authenticated
/// user's home directory on login.
/// </summary>
public sealed class LocalSessionService : ISessionService
{
    private readonly ILocalUserRepository _users;
    private readonly FileSystemSeeder _homeSeeder;
    private readonly IEventBus _eventBus;
    private readonly IAuditLog _auditLog;
    private readonly InstallationId _installationId;
    private readonly DeviceId _deviceId;
    private readonly Func<DateTimeOffset> _clock;

    private readonly Lock _gate = new();
    private Task<bool>? _iconPackInstall;
    private CancellationTokenSource? _rootTokenSource;
    private AuthenticatedPrincipal? _principal;

    private readonly KeyDerivationAsyncDelegate? _asyncHasher;
    private readonly ILoginProgressTracker _progressTracker;
    private readonly ICapabilityGrantRepository? _grantRepository;
    private readonly AppCatalog? _catalog;
    private readonly IUserProfileInitializer? _profileInitializer;
    private readonly Keyring.ISessionKeyring? _keyring;

    /// <summary>Initializes a session service in the <see cref="SessionState.Uninitialized"/> state.</summary>
    /// <param name="users">Repository used to look up and authenticate local users.</param>
    /// <param name="homeSeeder">Seeder that provisions a freshly authenticated user's home directory.</param>
    /// <param name="eventBus">Bus used to publish session lifecycle events.</param>
    /// <param name="auditLog">Audit log used to record login/logout/shutdown operations.</param>
    /// <param name="installationId">Identifier of the HackerOS installation/profile hosting this session.</param>
    /// <param name="deviceId">Identifier of the physical/browser device hosting this session.</param>
    /// <param name="clock">Clock used for event/audit timestamps; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="progressTracker">Optional tracker used to report login step progress.</param>
    /// <param name="asyncHasher">Optional asynchronous key derivation delegate (e.g. Web Crypto API interop).</param>
    /// <param name="grantRepository">Optional capability grant repository used to seed user capability grants on login.</param>
    /// <param name="catalog">Optional app catalog containing app manifests with declared capabilities.</param>
    /// <param name="profileInitializer">
    /// Optional per-user preparation run right after the home directory is seeded, before the
    /// session becomes active (the one-time settings-to-files migration of ADR 0044).
    /// </param>
    /// <param name="keyring">
    /// Optional user keyring (ADR 0100 section 2), unlocked with the typed password once the home is
    /// ready and locked again at logout.
    /// </param>
    public LocalSessionService(
        ILocalUserRepository users,
        FileSystemSeeder homeSeeder,
        IEventBus eventBus,
        IAuditLog auditLog,
        InstallationId installationId,
        DeviceId deviceId,
        Func<DateTimeOffset>? clock = null,
        ILoginProgressTracker? progressTracker = null,
        KeyDerivationAsyncDelegate? asyncHasher = null,
        ICapabilityGrantRepository? grantRepository = null,
        AppCatalog? catalog = null,
        IUserProfileInitializer? profileInitializer = null,
        Keyring.ISessionKeyring? keyring = null)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _homeSeeder = homeSeeder ?? throw new ArgumentNullException(nameof(homeSeeder));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _installationId = installationId;
        _deviceId = deviceId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _progressTracker = progressTracker ?? NullLoginProgressTracker.Instance;
        _asyncHasher = asyncHasher;
        _grantRepository = grantRepository;
        _catalog = catalog;
        _profileInitializer = profileInitializer;
        _keyring = keyring;
    }

    /// <inheritdoc />
    public SessionState State { get; private set; } = SessionState.Uninitialized;

    /// <inheritdoc />
    public AuthenticatedPrincipal? CurrentPrincipal
    {
        get
        {
            lock (_gate)
            {
                return _principal;
            }
        }
    }

    /// <inheritdoc />
    public Task<AuthenticatedPrincipal> LoginAsync(
        LocalLoginName loginName, string? password, CancellationToken cancellationToken = default) =>
        LoginCoreAsync(
            loginName,
            async (user, token) =>
            {
                if (user.Credential is { } credential
                    && !await LocalPasswordHasher.VerifyAsync(password ?? string.Empty, credential, _asyncHasher, token).ConfigureAwait(false))
                {
                    RecordAudit(user.Id.ToString(), "session.login", loginName.Value, AuditOutcome.Denied);
                    throw new InvalidOperationException("Invalid credentials.");
                }

                // Like pam_gnome_keyring: the password just verified unlocks the login keyring.
                return (keyring, principal, unlockToken) =>
                    keyring.UnlockAsync(principal, user.Credential is null ? null : password, unlockToken);
            },
            loginName.Value,
            cancellationToken);

    /// <summary>
    /// Starts a session with one of the account's passkeys (ADR 0168): the passkey service asks the
    /// browser and verifies the answer, then the session starts as after a password login. The
    /// passkey's PRF output, if any, unlocks the keyring slot of that passkey; otherwise the keyring
    /// stays locked for the session.
    /// </summary>
    /// <param name="loginName">The account.</param>
    /// <param name="passkeys">The passkey service of this host.</param>
    /// <param name="cancellationToken">Cancels the login.</param>
    /// <returns>The session's principal.</returns>
    /// <exception cref="InvalidOperationException">No such account, a disabled one, or a passkey that failed a check.</exception>
    /// <exception cref="NotSupportedException">The page has no WebAuthn.</exception>
    /// <exception cref="OperationCanceledException">The user closed the browser's prompt.</exception>
    public Task<AuthenticatedPrincipal> LoginWithPasskeyAsync(
        LocalLoginName loginName, PasskeyService passkeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkeys);
        return LoginCoreAsync(
            loginName,
            async (user, token) =>
            {
                PasskeyAuthentication verified;
                try
                {
                    verified = await passkeys.AuthenticateAsync(user, token).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    RecordAudit(user.Id.ToString(), "session.login", loginName.Value + " (passkey)", AuditOutcome.Denied);
                    throw;
                }

                return (keyring, principal, unlockToken) =>
                    keyring.UnlockWithPasskeyAsync(principal, verified.Passkey.Id, verified.PrfOutput, unlockToken);
            },
            loginName.Value + " (passkey)",
            cancellationToken);
    }

    /// <summary>
    /// The login of ADR 0013 with a pluggable proof: <paramref name="verify"/> checks the account's
    /// credential (and throws on failure), then returns how to unlock the keyring.
    /// </summary>
    private async Task<AuthenticatedPrincipal> LoginCoreAsync(
        LocalLoginName loginName,
        Func<LocalUser, CancellationToken, Task<Func<Keyring.ISessionKeyring, AuthenticatedPrincipal, CancellationToken, ValueTask>>> verify,
        string auditSubject,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (State is not (SessionState.Uninitialized or SessionState.LoggedOut))
            {
                throw new InvalidOperationException($"Cannot log in while the session is {State}.");
            }

            State = SessionState.Starting;
        }

        _progressTracker.Reset();
        _progressTracker.SetTotalSteps(3);

        try
        {
            LocalUser? user;
            Func<Keyring.ISessionKeyring, AuthenticatedPrincipal, CancellationToken, ValueTask> unlock;
            using (_progressTracker.BeginStep("Loading Profile"))
            {
                user = await _users.FindByLoginNameAsync(loginName, cancellationToken);
                if (user is null)
                {
                    RecordAudit(loginName.Value, "session.login", loginName.Value, AuditOutcome.Denied);
                    throw new InvalidOperationException($"No local user '{loginName}'.");
                }

                if (!user.Enabled)
                {
                    RecordAudit(user.Id.ToString(), "session.login", loginName.Value, AuditOutcome.Denied);
                    throw new InvalidOperationException($"User '{loginName}' is disabled.");
                }

                unlock = await verify(user, cancellationToken).ConfigureAwait(false);
            }

            await Task.Yield();

            using (_progressTracker.BeginStep("Ensure data integrity"))
            {
                await _homeSeeder.SeedAsync(user.LoginName.Value, user.PrimaryGroupId.ToString(), cancellationToken);
                if (_profileInitializer is not null)
                {
                    await _profileInitializer.InitializeAsync(user, cancellationToken).ConfigureAwait(false);
                }

                StartIconPackInstall();

                if (_grantRepository is not null && _catalog is not null)
                {
                    string userId = user.Id.ToString();
                    foreach (AppManifest manifest in _catalog.Manifests.Values)
                    {
                        CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(
                            _grantRepository, manifest, userId, _catalog.IsSystemApp(manifest.Id));
                    }
                }
            }

            await Task.Yield();

            SessionId sessionId = SessionId.FromGuid(Guid.NewGuid());
            List<LocalGroupId> groupIds = [user.PrimaryGroupId, .. user.AdditionalGroupIds];
            AuthenticatedPrincipal principal = new(
                sessionId,
                user.Id,
                user.LoginName,
                user.DisplayName,
                user.Authority,
                user.PrimaryGroupId,
                groupIds,
                _installationId,
                _deviceId,
                _clock());

            if (_keyring is not null)
            {
                // Never fails the login; an unreadable keyring stays locked or is set aside.
                await unlock(_keyring, principal, cancellationToken).ConfigureAwait(false);
            }

            using (_progressTracker.BeginStep("Starting Session"))
            {
                lock (_gate)
                {
                    _rootTokenSource = new CancellationTokenSource();
                    _principal = principal;
                    State = SessionState.Active;
                }

                _eventBus.Publish(new SessionActivatedEvent(sessionId, user.Id, principal.AuthenticatedAtUtc));
                RecordAudit(user.Id.ToString(), "session.login", auditSubject, AuditOutcome.Success);
            }

            return principal;
        }
        catch
        {
            lock (_gate)
            {
                if (State == SessionState.Starting)
                {
                    State = SessionState.LoggedOut;
                }
            }

            throw;
        }
    }

    /// <inheritdoc />
    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal;
        CancellationTokenSource rootSource;
        lock (_gate)
        {
            if (State != SessionState.Active)
            {
                throw new InvalidOperationException($"Cannot log out while the session is {State}.");
            }

            State = SessionState.LoggingOut;
            principal = _principal!;
            rootSource = _rootTokenSource!;
        }

        rootSource.Cancel();
        rootSource.Dispose();
        _keyring?.Lock();

        lock (_gate)
        {
            _principal = null;
            _rootTokenSource = null;
            State = SessionState.LoggedOut;
        }

        _eventBus.Publish(new SessionLoggedOutEvent(principal.SessionId, principal.UserId, _clock()));
        RecordAudit(principal.UserId.ToString(), "session.logout", principal.UserId.ToString(), AuditOutcome.Success);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal? principal;
        CancellationTokenSource? rootSource;
        lock (_gate)
        {
            if (State == SessionState.Stopped)
            {
                throw new InvalidOperationException("The session has already stopped.");
            }

            if (State is SessionState.Starting or SessionState.LoggingOut)
            {
                throw new InvalidOperationException($"Cannot shut down while the session is {State}.");
            }

            State = SessionState.ShuttingDown;
            principal = _principal;
            rootSource = _rootTokenSource;
        }

        rootSource?.Cancel();
        rootSource?.Dispose();
        _keyring?.Lock();

        lock (_gate)
        {
            _principal = null;
            _rootTokenSource = null;
            State = SessionState.Stopped;
        }

        _eventBus.Publish(new SessionShutDownEvent(principal?.SessionId, _clock()));
        RecordAudit(principal?.UserId.ToString() ?? "system", "session.shutdown", "session", AuditOutcome.Success);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public CancellationTokenSource CreateLinkedCancellationSource()
    {
        lock (_gate)
        {
            if (_rootTokenSource is null)
            {
                throw new InvalidOperationException("Cannot link a cancellation source before the session is active.");
            }

            return CancellationTokenSource.CreateLinkedTokenSource(_rootTokenSource.Token);
        }
    }

    /// <summary>
    /// Starts installing the icon packs shipped with the OS once per boot, without making the login
    /// wait for it: their images take about ten seconds to write in the browser store at the first
    /// login, and the session starts with the built-in icons anyway. A failed install is
    /// recorded in the audit log and tried again at the next login.
    /// </summary>
    private void StartIconPackInstall()
    {
        lock (_gate)
        {
            // Running, or done: only a failed install is started again.
            if (_iconPackInstall is not null && (!_iconPackInstall.IsCompleted || _iconPackInstall.Result))
            {
                return;
            }

            _iconPackInstall = InstallIconPacksAsync();
        }
    }

    private async Task<bool> InstallIconPacksAsync()
    {
        // Let the login finish first: the install only starts once the session code has yielded.
        await Task.Yield();
        try
        {
            await _homeSeeder.InstallIconPacksAsync(CancellationToken.None).ConfigureAwait(false);
            RecordAudit("system", "filesystem.install-icon-packs", "/usr/share/icons", AuditOutcome.Success);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // Nobody awaits this task: the audit log is where the failure shows.
            RecordAudit("system", "filesystem.install-icon-packs", "/usr/share/icons", AuditOutcome.Failed);
            return false;
        }
    }

    private void RecordAudit(string actor, string action, string subject, AuditOutcome outcome) =>
        _auditLog.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor, action, subject, outcome));
}
