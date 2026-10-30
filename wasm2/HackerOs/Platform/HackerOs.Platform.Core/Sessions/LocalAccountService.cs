using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// Adds and deletes local accounts (ADR 0169), the work of <c>useradd</c> and <c>userdel</c>. It
/// does not decide who may do it: the app gateway checks <c>users.manage</c> and asks for an
/// administrator first. Every attempt is written to <c>auth.log</c>.
/// </summary>
public sealed class LocalAccountService
{
    /// <summary>Longest display name accepted.</summary>
    public const int MaximumDisplayNameLength = 128;

    /// <summary>Group every administrator account belongs to, as the first one created at setup.</summary>
    public const string AdministratorsGroupName = "administrators";

    private static readonly string[] ReservedLogins = [ServiceAccount.LoginName, "root", AdministratorsGroupName, GuestAccountService.LoginName];

    private readonly ILocalUserRepository _users;
    private readonly ILocalGroupRepository _groups;
    private readonly IAuditLog _audit;
    private readonly IFileSystemService? _fileSystem;
    private readonly KeyDerivationAsyncDelegate? _asyncHasher;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _iterations;

    /// <summary>Initializes the service.</summary>
    /// <param name="users">The account store.</param>
    /// <param name="groups">The group store.</param>
    /// <param name="audit">The authentication log (<c>auth.log</c>).</param>
    /// <param name="fileSystem">Filesystem of the home directories; <see langword="null"/> never deletes one.</param>
    /// <param name="asyncHasher">Optional Web Crypto key derivation, as used at login.</param>
    /// <param name="clock">Clock of the audit entries; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="iterations">PBKDF2 work factor of new credentials (lower only in tests).</param>
    public LocalAccountService(
        ILocalUserRepository users,
        ILocalGroupRepository groups,
        IAuditLog audit,
        IFileSystemService? fileSystem = null,
        KeyDerivationAsyncDelegate? asyncHasher = null,
        Func<DateTimeOffset>? clock = null,
        int iterations = 210_000)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        _fileSystem = fileSystem;
        _asyncHasher = asyncHasher;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _iterations = iterations;
    }

    /// <summary>
    /// Gets whether an account has an open session, in the foreground or in the background
    /// (ADR 0201 section 7); such an account is never deleted. <see langword="null"/> only refuses
    /// the caller's own account.
    /// </summary>
    public Func<LocalUserId, bool>? HasOpenSession { get; init; }

    /// <summary>Gets whether a login name is reserved for the system (<c>hackeros-svc</c>, <c>root</c>, <c>administrators</c>, <c>guest</c>).</summary>
    /// <param name="loginName">A login name.</param>
    /// <returns><see langword="true"/> when no account may take it.</returns>
    public static bool IsReserved(string loginName) =>
        ReservedLogins.Contains(loginName.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates an account for an administrator who already confirmed (ADR 0169 section 1): own
    /// primary group, <c>administrators</c> as well for an administrator, a password credential. The
    /// home directory is created at the first login.
    /// </summary>
    /// <param name="administratorLogin">The administrator who confirmed (or <c>root</c> under <c>sudo</c>).</param>
    /// <param name="account">The new account.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The created account.</returns>
    /// <exception cref="ArgumentException">A field breaks the rules, or the name is taken or reserved.</exception>
    public async ValueTask<LocalUser> CreateAsync(
        string administratorLogin,
        HackNewUserAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorLogin);
        ArgumentNullException.ThrowIfNull(account);
        string requested = account.LoginName?.Trim() ?? string.Empty;
        try
        {
            if (HackUserAccounts.ValidateNewAccount(account) is { } reason)
            {
                throw new ArgumentException($"useradd: {reason}", nameof(account));
            }

            LocalLoginName loginName = LocalLoginName.Parse(requested);
            if (IsReserved(loginName.Value))
            {
                throw new ArgumentException($"useradd: '{loginName.Value}' is reserved for the system", nameof(account));
            }

            if (await _users.FindByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false) is not null
                || await _groups.FindByNameAsync(loginName, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new ArgumentException($"useradd: user '{loginName.Value}' already exists", nameof(account));
            }

            // ADR 0187 section 4: while the guest exists, the first registered account is created
            // from it ("Create my account": migrate or delete the guest), never beside it.
            if ((await _users.GetAllAsync(cancellationToken).ConfigureAwait(false)).Any(static user => user.IsGuest))
            {
                throw new ArgumentException("useradd: create your own account from the guest account first", nameof(account));
            }

            LocalPasswordCredential credential = await LocalPasswordHasher
                .CreateAsync(account.Password, _iterations, _asyncHasher, cancellationToken)
                .ConfigureAwait(false);
            LocalGroup own = await _groups.CreateGroupAsync(loginName, cancellationToken).ConfigureAwait(false);
            List<LocalGroupId> additional = [];
            if (account.Authority == AppAuthority.Administrator)
            {
                LocalLoginName administrators = LocalLoginName.Parse(AdministratorsGroupName);
                LocalGroup group = await _groups.FindByNameAsync(administrators, cancellationToken).ConfigureAwait(false)
                    ?? await _groups.CreateGroupAsync(administrators, cancellationToken).ConfigureAwait(false);
                additional.Add(group.Id);
            }

            LocalUser created = await _users.CreateUserAsync(
                loginName,
                account.DisplayName.Trim(),
                account.Authority,
                own.Id,
                additional,
                credential,
                cancellationToken).ConfigureAwait(false);
            Record(administratorLogin, "users.add", created.LoginName.Value, AuditOutcome.Success);
            return created;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            Record(administratorLogin, "users.add", requested, AuditOutcome.Denied);
            throw exception as ArgumentException ?? new ArgumentException($"useradd: {exception.Message}", nameof(account), exception);
        }
    }

    /// <summary>
    /// Deletes an account for an administrator who already confirmed (ADR 0169 section 2), and its
    /// home directory when asked (<c>userdel -r</c>).
    /// </summary>
    /// <param name="caller">The session that asked: it cannot delete its own account.</param>
    /// <param name="administratorLogin">The administrator who confirmed (or <c>root</c> under <c>sudo</c>).</param>
    /// <param name="loginName">The account to delete.</param>
    /// <param name="removeHome">Whether to delete <c>/home/&lt;login&gt;</c> too.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <exception cref="ArgumentException">No such account.</exception>
    /// <exception cref="InvalidOperationException">The caller's own account, an account with an open session (ADR 0201), or the last enabled administrator.</exception>
    public async ValueTask DeleteAsync(
        AuthenticatedPrincipal caller,
        string administratorLogin,
        string loginName,
        bool removeHome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        LocalUser? user;
        try
        {
            user = await _users.FindByLoginNameAsync(LocalLoginName.Parse(loginName), cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            user = null;
        }

        if (user is null)
        {
            Record(administratorLogin, "users.delete", loginName, AuditOutcome.Denied);
            throw new ArgumentException($"userdel: user '{loginName}' does not exist", nameof(loginName));
        }

        if (user.Id == caller.UserId || HasOpenSession?.Invoke(user.Id) == true)
        {
            Record(administratorLogin, "users.delete", user.LoginName.Value, AuditOutcome.Denied);
            throw new InvalidOperationException($"userdel: user '{user.LoginName.Value}' is currently signed in");
        }

        try
        {
            await _users.DeleteUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            Record(administratorLogin, "users.delete", user.LoginName.Value, AuditOutcome.Denied);
            throw new InvalidOperationException($"userdel: '{user.LoginName.Value}' is the last administrator");
        }

        if (removeHome && _fileSystem is not null)
        {
            SyncFileOperations files = new(_fileSystem, SystemContext());
            await files.DeleteRecursiveAsync(VirtualPath.Parse(user.HomePath), cancellationToken).ConfigureAwait(false);
        }

        Record(administratorLogin, "users.delete", user.LoginName.Value + (removeHome ? " (home removed)" : string.Empty), AuditOutcome.Success);
    }

    private FileSystemAuthorizationContext SystemContext() => new(
        new AppOperationContext
        {
            AppId = "org.hackeros.accounts",
            UserId = "system",
            UserName = "system",
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        },
        ["system"],
        _clock());

    private void Record(string actor, string operation, string subject, AuditOutcome outcome) =>
        _audit.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor, operation, subject, outcome));
}
