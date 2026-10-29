using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// The guest account of ADR 0187: a local account <c>guest</c>, without a password, administrator,
/// kept from one visit to the next. It is offered only while no account at all exists, so the first
/// start needs no form; once a registered account exists it can never be created again.
/// </summary>
/// <remarks>
/// The guest is recognized by <see cref="LocalUser.IsGuest"/>, never by its login name alone. Every
/// creation attempt is written to <c>auth.log</c> as <c>users.guest-create</c>.
/// </remarks>
public sealed class GuestAccountService
{
    /// <summary>The login name of the guest account, reserved for it (ADR 0187 section 1).</summary>
    public const string LoginName = "guest";

    private readonly ILocalUserRepository _users;
    private readonly ILocalGroupRepository _groups;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Initializes the service.</summary>
    /// <param name="users">The account store.</param>
    /// <param name="groups">The group store.</param>
    /// <param name="audit">The authentication log (<c>auth.log</c>).</param>
    /// <param name="clock">Clock of the audit entries; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public GuestAccountService(
        ILocalUserRepository users,
        ILocalGroupRepository groups,
        IAuditLog audit,
        Func<DateTimeOffset>? clock = null)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Gets whether the guest account may be created now: no account exists at all (ADR 0187
    /// section 2). A registered account, enabled or not, closes the guest for good.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<bool> CanCreateAsync(CancellationToken cancellationToken = default) =>
        (await _users.GetAllAsync(cancellationToken).ConfigureAwait(false)).Count == 0;

    /// <summary>
    /// Gets the guest account while it is the only account, the one state in which it may sign in
    /// without a password and confirm an elevation by a plain confirmation; otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<LocalUser?> FindLoneGuestAsync(CancellationToken cancellationToken = default) =>
        FindLoneGuest(await _users.GetAllAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets the guest account when it is the only one of <paramref name="users"/>, enabled and still
    /// without a password (a guest who set one with <c>passwd</c> signs in and confirms with it).
    /// </summary>
    /// <param name="users">Every stored account.</param>
    public static LocalUser? FindLoneGuest(IReadOnlyList<LocalUser> users) =>
        users is [{ IsGuest: true, Enabled: true, Credential: null } guest] ? guest : null;

    /// <summary>
    /// Creates the guest account (ADR 0187 sections 1 and 2): login <c>guest</c>, no password,
    /// administrator, own group <c>guest</c> and member of <c>administrators</c>. Its home directory
    /// is created at its first login, as for any account.
    /// </summary>
    /// <param name="displayName">"Guest" in the system language (<c>/etc/locale.conf</c>).</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The guest account.</returns>
    /// <exception cref="InvalidOperationException">An account already exists: the guest can no longer be created.</exception>
    public async ValueTask<LocalUser> CreateAsync(string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!await CanCreateAsync(cancellationToken).ConfigureAwait(false))
        {
            Record("users.guest-create", AuditOutcome.Denied);
            throw new InvalidOperationException("The guest account can only be created while no account exists.");
        }

        LocalLoginName guestName = LocalLoginName.Parse(LoginName);
        LocalLoginName administratorsName = LocalLoginName.Parse(LocalAccountService.AdministratorsGroupName);
        LocalGroup own = await _groups.FindByNameAsync(guestName, cancellationToken).ConfigureAwait(false)
            ?? await _groups.CreateGroupAsync(guestName, cancellationToken).ConfigureAwait(false);
        LocalGroup administrators = await _groups.FindByNameAsync(administratorsName, cancellationToken).ConfigureAwait(false)
            ?? await _groups.CreateGroupAsync(administratorsName, cancellationToken).ConfigureAwait(false);
        LocalUser guest = await _users.CreateGuestUserAsync(
            guestName,
            displayName.Trim(),
            AppAuthority.Administrator,
            own.Id,
            [administrators.Id],
            cancellationToken).ConfigureAwait(false);
        Record("users.guest-create", AuditOutcome.Success);
        return guest;
    }

    private void Record(string operation, AuditOutcome outcome, string subject = LoginName) =>
        _audit.Record(new AuditEntry(_clock(), Guid.NewGuid(), "system", operation, subject, outcome));
}
