using System.Text;
using System.Text.RegularExpressions;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// The guest account of ADR 0187: a local account <c>guest</c>, without a password, administrator,
/// kept from one visit to the next. It is offered only while no account at all exists, so the first
/// start needs no form; once a registered account exists it can never be created again.
/// </summary>
/// <remarks>
/// The guest is recognized by <see cref="LocalUser.IsGuest"/>, never by its login name alone. Every
/// attempt is written to <c>auth.log</c>: <c>users.guest-create</c>, then <c>users.guest-migrate</c>
/// or <c>users.guest-delete</c> when the first registered account is created (section 4). Like
/// <c>usermod -l</c>, which refuses a user who still runs processes, both conversions require the
/// guest's session to be closed first.
/// </remarks>
public sealed partial class GuestAccountService
{
    /// <summary>The login name of the guest account, reserved for it (ADR 0187 section 1).</summary>
    public const string LoginName = "guest";

    /// <summary>Home-relative directory whose text files get <c>/home/guest</c> rewritten at migration.</summary>
    public const string ConfigDirectory = ".config";

    private const string GuestHome = "/home/" + LoginName;

    private readonly ILocalUserRepository _users;
    private readonly ILocalGroupRepository _groups;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IFileSystemService? _fileSystem;
    private readonly ISessionService? _session;
    private readonly KeyDerivationAsyncDelegate? _asyncHasher;
    private readonly int _iterations;

    /// <summary>Initializes the service.</summary>
    /// <param name="users">The account store.</param>
    /// <param name="groups">The group store.</param>
    /// <param name="audit">The authentication log (<c>auth.log</c>).</param>
    /// <param name="clock">Clock of the audit entries; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="fileSystem">Filesystem of the home directories; <see langword="null"/> leaves them untouched.</param>
    /// <param name="session">The session service: a conversion is refused while the guest is signed in.</param>
    /// <param name="asyncHasher">Optional Web Crypto key derivation, as used at login.</param>
    /// <param name="iterations">PBKDF2 work factor of the new password (lower only in tests).</param>
    public GuestAccountService(
        ILocalUserRepository users,
        ILocalGroupRepository groups,
        IAuditLog audit,
        Func<DateTimeOffset>? clock = null,
        IFileSystemService? fileSystem = null,
        ISessionService? session = null,
        KeyDerivationAsyncDelegate? asyncHasher = null,
        int iterations = 210_000)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _fileSystem = fileSystem;
        _session = session;
        _asyncHasher = asyncHasher;
        _iterations = iterations;
    }

    /// <summary>Gets the guest account if one exists (alone or not), otherwise <see langword="null"/>.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<LocalUser?> FindGuestAsync(CancellationToken cancellationToken = default) =>
        (await _users.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(static user => user.IsGuest);

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

    /// <summary>
    /// "Create my account", choice <em>Migrate</em> (ADR 0187 section 4): the guest becomes the
    /// account in place, as <c>usermod -l NEW -d /home/NEW -m -c NAME guest</c>, <c>groupmod -n NEW
    /// guest</c> and <c>passwd NEW</c>. It keeps its <see cref="LocalUser.Id"/>, so the ownership of
    /// its files, its capability grants and its apps; <c>/home/guest</c> moves to <c>/home/NEW</c>
    /// and the absolute <c>/home/guest</c> paths in the text files of <c>~/.config</c> are rewritten.
    /// Its keyring is created under the new password at the first sign-in (the guest had none).
    /// </summary>
    /// <param name="account">Login, display name and password of the new account (always an administrator).</param>
    /// <param name="cancellationToken">Cancels before the changes.</param>
    /// <returns>The registered account.</returns>
    /// <exception cref="ArgumentException">A field breaks the rules, or the login name is taken or reserved.</exception>
    /// <exception cref="InvalidOperationException">No guest account, or its session is still open.</exception>
    public async ValueTask<LocalUser> MigrateAsync(HackNewUserAccount account, CancellationToken cancellationToken = default)
    {
        const string Operation = "users.guest-migrate";
        (LocalUser guest, LocalLoginName loginName, HackNewUserAccount checkedAccount) =
            await PrepareConversionAsync(Operation, account, cancellationToken).ConfigureAwait(false);
        string newHome = $"/home/{loginName.Value}";
        SyncFileOperations? files = _fileSystem is null ? null : new SyncFileOperations(_fileSystem, SystemContext());
        if (files is not null && await files.StatAsync(VirtualPath.Parse(newHome), cancellationToken).ConfigureAwait(false) is not null)
        {
            Record(Operation, AuditOutcome.Denied, loginName.Value);
            throw new ArgumentException($"usermod: directory '{newHome}' already exists", nameof(account));
        }

        LocalPasswordCredential credential = await LocalPasswordHasher
            .CreateAsync(checkedAccount.Password, _iterations, _asyncHasher, cancellationToken)
            .ConfigureAwait(false);

        // The home is copied first: it is the step most likely to fail, and nothing else changed yet.
        // A copy, not a rename: the files the system created for the guest (its home folders) are
        // owned by the login name, so they are written again as the new login, like `cp -a` then
        // `chown -R`; the guest's own files keep their content, mode and the same account behind.
        bool homeCopied = false;
        if (files is not null && await files.StatAsync(VirtualPath.Parse(GuestHome), cancellationToken).ConfigureAwait(false) is not null)
        {
            SyncFileOperations owner = new(_fileSystem!, OwnerContext(loginName.Value, guest.PrimaryGroupId));
            try
            {
                await CopyHomeAsync(files, owner, VirtualPath.Parse(GuestHome), VirtualPath.Parse(newHome), newHome).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                await files.DeleteRecursiveAsync(VirtualPath.Parse(newHome), CancellationToken.None).ConfigureAwait(false);
                Record(Operation, AuditOutcome.Denied, loginName.Value);
                throw new InvalidOperationException($"usermod: cannot move '{GuestHome}' to '{newHome}': {exception.Message}", exception);
            }

            homeCopied = true;
        }

        LocalUser converted;
        try
        {
            converted = await _users.ConvertGuestUserAsync(
                guest.Id, loginName, checkedAccount.DisplayName.Trim(), credential, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            if (homeCopied)
            {
                await files!.DeleteRecursiveAsync(VirtualPath.Parse(newHome), CancellationToken.None).ConfigureAwait(false);
            }

            Record(Operation, AuditOutcome.Denied, loginName.Value);
            throw;
        }

        if (await _groups.FindByIdAsync(guest.PrimaryGroupId, CancellationToken.None).ConfigureAwait(false) is { } own
            && own.Name.Value == LoginName)
        {
            await _groups.RenameGroupAsync(own.Id, loginName, CancellationToken.None).ConfigureAwait(false);
        }

        if (homeCopied)
        {
            await files!.DeleteRecursiveAsync(VirtualPath.Parse(GuestHome), CancellationToken.None).ConfigureAwait(false);
        }

        Record(Operation, AuditOutcome.Success, $"{LoginName} -> {loginName.Value}");
        return converted;
    }

    /// <summary>
    /// "Create my account", choice <em>Delete the guest</em> (ADR 0187 section 4): a new
    /// administrator is created (own group, <c>administrators</c>, the password), then the guest
    /// account, its group and <c>/home/guest</c> are deleted, as <c>userdel -r guest</c>. Apps
    /// installed for the machine stay; those installed for the guest alone go with its home.
    /// </summary>
    /// <param name="account">Login, display name and password of the new account (always an administrator).</param>
    /// <param name="cancellationToken">Cancels before the changes.</param>
    /// <returns>The registered account.</returns>
    /// <exception cref="ArgumentException">A field breaks the rules, or the login name is taken or reserved.</exception>
    /// <exception cref="InvalidOperationException">No guest account, or its session is still open.</exception>
    public async ValueTask<LocalUser> ReplaceAsync(HackNewUserAccount account, CancellationToken cancellationToken = default)
    {
        const string Operation = "users.guest-delete";
        (LocalUser guest, LocalLoginName loginName, HackNewUserAccount checkedAccount) =
            await PrepareConversionAsync(Operation, account, cancellationToken).ConfigureAwait(false);
        LocalPasswordCredential credential = await LocalPasswordHasher
            .CreateAsync(checkedAccount.Password, _iterations, _asyncHasher, cancellationToken)
            .ConfigureAwait(false);

        LocalGroup own = await _groups.CreateGroupAsync(loginName, CancellationToken.None).ConfigureAwait(false);
        LocalLoginName administratorsName = LocalLoginName.Parse(LocalAccountService.AdministratorsGroupName);
        LocalGroup administrators = await _groups.FindByNameAsync(administratorsName, CancellationToken.None).ConfigureAwait(false)
            ?? await _groups.CreateGroupAsync(administratorsName, CancellationToken.None).ConfigureAwait(false);
        LocalUser created = await _users.CreateUserAsync(
            loginName,
            checkedAccount.DisplayName.Trim(),
            AppAuthority.Administrator,
            own.Id,
            [administrators.Id],
            credential,
            CancellationToken.None).ConfigureAwait(false);

        // The new administrator exists: the guest is no longer the last one and can go.
        await _users.DeleteUserAsync(guest.Id, CancellationToken.None).ConfigureAwait(false);
        if (await _groups.FindByIdAsync(guest.PrimaryGroupId, CancellationToken.None).ConfigureAwait(false) is { } guestGroup
            && guestGroup.Name.Value == LoginName)
        {
            await _groups.DeleteGroupAsync(guestGroup.Id, CancellationToken.None).ConfigureAwait(false);
        }

        if (_fileSystem is not null)
        {
            await new SyncFileOperations(_fileSystem, SystemContext())
                .DeleteRecursiveAsync(VirtualPath.Parse(GuestHome), CancellationToken.None)
                .ConfigureAwait(false);
        }

        Record(Operation, AuditOutcome.Success, $"{LoginName} (home removed), {loginName.Value} created");
        return created;
    }

    /// <summary>Checks everything a conversion needs before anything changes.</summary>
    private async ValueTask<(LocalUser Guest, LocalLoginName LoginName, HackNewUserAccount Account)> PrepareConversionAsync(
        string operation, HackNewUserAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        string requested = account.LoginName?.Trim() ?? string.Empty;

        // The first registered account is always an administrator (ADR 0187 section 4).
        HackNewUserAccount administrator = account with { Authority = AppAuthority.Administrator };
        if (HackUserAccounts.ValidateNewAccount(administrator) is { } reason)
        {
            Record(operation, AuditOutcome.Denied, requested);
            throw new ArgumentException($"useradd: {reason}", nameof(account));
        }

        LocalLoginName loginName = LocalLoginName.Parse(requested);
        if (LocalAccountService.IsReserved(loginName.Value))
        {
            Record(operation, AuditOutcome.Denied, loginName.Value);
            throw new ArgumentException($"useradd: '{loginName.Value}' is reserved for the system", nameof(account));
        }

        if (await _users.FindByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false) is not null
            || await _groups.FindByNameAsync(loginName, cancellationToken).ConfigureAwait(false) is not null)
        {
            Record(operation, AuditOutcome.Denied, loginName.Value);
            throw new ArgumentException($"useradd: user '{loginName.Value}' already exists", nameof(account));
        }

        LocalUser guest = await FindGuestAsync(cancellationToken).ConfigureAwait(false) ?? throw Denied("there is no guest account");
        if (_session?.CurrentPrincipal is { } principal && principal.UserId == guest.Id)
        {
            // usermod: user guest is currently used by process ...
            throw Denied("the guest session is still open");
        }

        return (guest, loginName, administrator);

        InvalidOperationException Denied(string why)
        {
            Record(operation, AuditOutcome.Denied, requested);
            return new InvalidOperationException($"usermod: {why}");
        }
    }

    /// <summary>
    /// Copies the guest's home to the new one as the new owner, keeping each entry's mode. The
    /// absolute <c>/home/guest</c> paths of the UTF-8 text files of <c>~/.config</c> and of the
    /// symbolic links are rewritten (ADR 0187 section 4); files elsewhere keep their content
    /// (documented limit).
    /// </summary>
    private static async ValueTask CopyHomeAsync(
        SyncFileOperations system, SyncFileOperations owner, VirtualPath source, VirtualPath destination, string newHome)
    {
        FileSystemEntrySnapshot entry = await system.StatAsync(source, CancellationToken.None).ConfigureAwait(false)
            ?? throw new IOException($"'{source.Value}' disappeared during the copy.");
        FileSystemPermissions permissions = entry.Metadata.Permissions;
        switch (entry.Metadata.Kind)
        {
            case FileSystemEntryKind.Directory:
                await owner.CreateAsync(destination, FileSystemEntryKind.Directory, permissions, symbolicLinkTarget: null).ConfigureAwait(false);
                foreach (VirtualPath child in await system.ListAsync(source, CancellationToken.None).ConfigureAwait(false))
                {
                    string name = child.Value[(child.Value.LastIndexOf('/') + 1)..];
                    await CopyHomeAsync(system, owner, child, VirtualPath.Parse($"{destination.Value}/{name}"), newHome).ConfigureAwait(false);
                }

                break;
            case FileSystemEntryKind.SymbolicLink when entry.Metadata is SymbolicLinkMetadata link:
                await owner.CreateAsync(
                    destination, FileSystemEntryKind.SymbolicLink, permissions, GuestHomePattern().Replace(link.Target, newHome)).ConfigureAwait(false);
                break;
            case FileSystemEntryKind.File:
                byte[] content = await system.TryReadAllBytesAsync(source, CancellationToken.None).ConfigureAwait(false)
                    ?? throw new IOException($"Cannot read '{source.Value}'.");
                if (destination.Value.StartsWith($"{newHome}/{ConfigDirectory}/", StringComparison.Ordinal))
                {
                    content = RewriteGuestHome(content, newHome);
                }

                await owner.WriteAllBytesAsync(destination, content, permissions, CancellationToken.None).ConfigureAwait(false);
                break;
            default:
                // Devices and other special entries do not live in a home directory.
                break;
        }
    }

    /// <summary>Replaces <c>/home/guest</c> in a UTF-8 text file; anything else is returned as it is.</summary>
    private static byte[] RewriteGuestHome(byte[] content, string newHome)
    {
        if (content.Contains((byte)0))
        {
            return content;
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return content;
        }

        string rewritten = GuestHomePattern().Replace(text, newHome);
        return string.Equals(rewritten, text, StringComparison.Ordinal) ? content : Encoding.UTF8.GetBytes(rewritten);
    }

    /// <summary><c>/home/guest</c> as a whole path segment (not <c>/home/guests</c>).</summary>
    [GeneratedRegex("/home/guest(?![A-Za-z0-9._-])", RegexOptions.CultureInvariant)]
    private static partial Regex GuestHomePattern();

    /// <summary>
    /// Writes as the new account, as the seeder provisions a home (owner = login name, its own
    /// group), so the account owns every copied entry.
    /// </summary>
    private FileSystemAuthorizationContext OwnerContext(string loginName, LocalGroupId groupId) => new(
        new AppOperationContext
        {
            AppId = "org.hackeros.accounts",
            UserId = loginName,
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        },
        [groupId.ToString()],
        _clock());

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
}
