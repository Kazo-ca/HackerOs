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
/// user's home directory on login. Several sessions may stay open, one in the foreground (ADR 0201,
/// <see cref="IUserSwitchingService"/>).
/// </summary>
public sealed class LocalSessionService : ISessionService, IUserSwitchingService
{
    /// <summary>
    /// How many sessions may be open at once by default (ADR 0201 section 6): every session lives
    /// in the same WebAssembly tab, so its apps share one memory budget.
    /// </summary>
    public const int DefaultMaxOpenSessions = 3;

    private readonly ILocalUserRepository _users;
    private readonly FileSystemSeeder _homeSeeder;
    private readonly IEventBus _eventBus;
    private readonly IAuditLog _auditLog;
    private readonly InstallationId _installationId;
    private readonly DeviceId _deviceId;
    private readonly Func<DateTimeOffset> _clock;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _rootTokenSource;
    private AuthenticatedPrincipal? _principal;

    /// <summary>Every open session (foreground included) with its root token, in opening order.</summary>
    private readonly List<OpenSession> _openSessions = [];

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

    /// <summary>
    /// Gets how many sessions may be open at once (ADR 0201 section 6); <see cref="DefaultMaxOpenSessions"/>
    /// unless set at construction. At least one.
    /// </summary>
    public int MaxOpenSessions
    {
        get => _maxOpenSessions;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxOpenSessions = value;
        }
    }

    private readonly int _maxOpenSessions = DefaultMaxOpenSessions;

    /// <inheritdoc />
    public IReadOnlyList<AuthenticatedPrincipal> OpenSessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _openSessions.Select(static open => open.Principal)];
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AuthenticatedPrincipal> BackgroundSessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _openSessions.Where(open => !ReferenceEquals(open.Principal, _principal)).Select(static open => open.Principal)];
            }
        }
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
            OpenSession? resumed;
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

                OpenSession? background = FindOpenSession(user.Id);
                if (background is null && OpenSessionCount >= MaxOpenSessions)
                {
                    RecordAudit(user.Id.ToString(), "session.login", loginName.Value, AuditOutcome.Denied);
                    throw new SessionLimitReachedException(MaxOpenSessions);
                }

                unlock = await verify(user, cancellationToken).ConfigureAwait(false);
                resumed = background;
            }

            if (resumed is not null)
            {
                // ADR 0201 section 1: the account already has a session in the background; the
                // verified credential brings it back instead of opening a second one.
                return await ResumeAsync(resumed, unlock, auditSubject, cancellationToken).ConfigureAwait(false);
            }

            await Task.Yield();

            using (_progressTracker.BeginStep("Ensure data integrity"))
            {
                await _homeSeeder.SeedAsync(user.LoginName.Value, user.PrimaryGroupId.ToString(), cancellationToken);
                if (_profileInitializer is not null)
                {
                    await _profileInitializer.InitializeAsync(user, cancellationToken).ConfigureAwait(false);
                }

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
                    _openSessions.Add(new OpenSession(principal, _rootTokenSource));
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
            _openSessions.RemoveAll(open => ReferenceEquals(open.Principal, principal));
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
        List<OpenSession> background;
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
            background = [.. _openSessions.Where(open => !ReferenceEquals(open.RootSource, rootSource))];
        }

        rootSource?.Cancel();
        rootSource?.Dispose();
        foreach (OpenSession open in background)
        {
            // ADR 0201: shutting down ends the sessions in the background too.
            open.RootSource.Cancel();
            open.RootSource.Dispose();
        }

        _keyring?.Lock();

        lock (_gate)
        {
            _openSessions.Clear();
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

    /// <inheritdoc />
    public Task SwitchAwayAsync(CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal;
        lock (_gate)
        {
            if (State != SessionState.Active || _principal is null)
            {
                throw new InvalidOperationException($"Cannot switch user while the session is {State}.");
            }

            // The root token is not cancelled: the session's processes keep running (ADR 0201 §1).
            principal = _principal;
            _principal = null;
            _rootTokenSource = null;
            State = SessionState.LoggedOut;
        }

        // ADR 0201 section 2: the keyring follows the foreground session.
        _keyring?.Lock();
        _eventBus.Publish(new SessionSwitchedOutEvent(principal.SessionId, principal.UserId, _clock()));
        RecordAudit(principal.UserId.ToString(), "session.switch", principal.LoginName.Value, AuditOutcome.Success);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EndBackgroundSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        OpenSession ended;
        lock (_gate)
        {
            ended = _openSessions.FirstOrDefault(open =>
                open.Principal.SessionId == sessionId && !ReferenceEquals(open.Principal, _principal))
                ?? throw new InvalidOperationException($"No session {sessionId} in the background.");
            _openSessions.Remove(ended);
        }

        ended.RootSource.Cancel();
        ended.RootSource.Dispose();
        _eventBus.Publish(new SessionLoggedOutEvent(sessionId, ended.Principal.UserId, _clock()));
        RecordAudit(ended.Principal.UserId.ToString(), "session.logout", ended.Principal.UserId.ToString(), AuditOutcome.Success);
        return Task.CompletedTask;
    }

    /// <summary>Gets whether the account has an open session, in the foreground or not (ADR 0201 section 7).</summary>
    /// <param name="userId">The account.</param>
    public bool HasOpenSession(LocalUserId userId) => FindOpenSession(userId) is not null;

    private int OpenSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _openSessions.Count;
            }
        }
    }

    private OpenSession? FindOpenSession(LocalUserId userId)
    {
        lock (_gate)
        {
            return _openSessions.FirstOrDefault(open => open.Principal.UserId == userId);
        }
    }

    /// <summary>Brings a session in the background back to the foreground, its credential verified.</summary>
    private async Task<AuthenticatedPrincipal> ResumeAsync(
        OpenSession session,
        Func<Keyring.ISessionKeyring, AuthenticatedPrincipal, CancellationToken, ValueTask> unlock,
        string auditSubject,
        CancellationToken cancellationToken)
    {
        if (_keyring is not null)
        {
            await unlock(_keyring, session.Principal, cancellationToken).ConfigureAwait(false);
        }

        using (_progressTracker.BeginStep("Starting Session"))
        {
            lock (_gate)
            {
                if (!_openSessions.Contains(session))
                {
                    // Ended while its credential was being checked.
                    State = SessionState.LoggedOut;
                    throw new InvalidOperationException("The session ended while it was being resumed.");
                }

                _rootTokenSource = session.RootSource;
                _principal = session.Principal;
                State = SessionState.Active;
            }

            _eventBus.Publish(new SessionResumedEvent(session.Principal.SessionId, session.Principal.UserId, _clock()));
            RecordAudit(session.Principal.UserId.ToString(), "session.resume", auditSubject, AuditOutcome.Success);
        }

        return session.Principal;
    }

    /// <summary>One open session: its principal and its root cancellation source.</summary>
    private sealed record OpenSession(AuthenticatedPrincipal Principal, CancellationTokenSource RootSource);

    private void RecordAudit(string actor, string action, string subject, AuditOutcome outcome) =>
        _auditLog.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor, action, subject, outcome));
}
