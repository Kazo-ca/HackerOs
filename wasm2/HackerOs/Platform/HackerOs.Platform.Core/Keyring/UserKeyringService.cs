using System.Security.Cryptography;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Execution;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Keyring;

/// <summary>What the session service does with the keyring at login and logout (ADR 0100 section 2).</summary>
public interface ISessionKeyring
{
    /// <summary>
    /// Unlocks (or creates) the user's keyring with the login password. Never fails the login: a
    /// keyring that cannot be opened is set aside as <c>login.keyring.old</c> and replaced by an
    /// empty one, as GNOME does after a password reset.
    /// </summary>
    /// <param name="principal">The user who just authenticated.</param>
    /// <param name="password">The password they typed; a passwordless account keeps the keyring locked.</param>
    /// <param name="cancellationToken">Cancels the unlock.</param>
    ValueTask UnlockAsync(AuthenticatedPrincipal principal, string? password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlocks the user's keyring with the PRF output of the passkey they signed in with
    /// (ADR 0168 section 3). Never fails the login and never sets a keyring aside: without a PRF
    /// output or a slot for that passkey, the keyring stays locked for the session.
    /// </summary>
    /// <param name="principal">The user who just authenticated.</param>
    /// <param name="credentialId">Base64url id of the passkey used.</param>
    /// <param name="prfOutput">The PRF output of that passkey, or <see langword="null"/> when the authenticator gave none.</param>
    /// <param name="cancellationToken">Cancels the unlock.</param>
    ValueTask UnlockWithPasskeyAsync(
        AuthenticatedPrincipal principal,
        string credentialId,
        byte[]? prfOutput,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>Forgets the key and the decrypted entries (logout).</summary>
    void Lock();
}

/// <summary>
/// The session user's keyring, <c>~/.local/share/keyrings/login.keyring</c> (ADR 0100 section 2),
/// like gnome-keyring's login keyring: sealed with AES-256-GCM under a random master key, kept in
/// memory for the session and forgotten at logout. The master key is wrapped in one slot per way of
/// unlocking (ADR 0168 section 3): the password (PBKDF2-SHA256, same work factor as the password
/// hash, own salt) and every passkey whose authenticator has the PRF extension.
/// </summary>
/// <remarks>
/// <para>
/// The file is read and written as the user themself (mode <c>0600</c>, directory <c>0700</c>), so
/// file permissions protect the ciphertext as well; apps never reach it directly, only through
/// <see cref="AppKeyringGateway"/>, which confines each app to its own entries unless it holds
/// <c>keyring.read-all</c>.
/// </para>
/// <para>
/// Every save draws a fresh 12-byte nonce. Changing the password re-seals the keyring under a new
/// salt; an administrator resetting another user's password makes it unreadable, so
/// <see cref="SetAsideAsync"/> renames it <c>login.keyring.old</c> and the next login creates an
/// empty one.
/// </para>
/// </remarks>
public sealed class UserKeyringService : ISessionKeyring
{
    /// <summary>PBKDF2 iterations of a new keyring: the same work factor as <see cref="Sessions.LocalPasswordHasher"/>.</summary>
    public const int DefaultIterations = 210_000;

    /// <summary>Directory of the keyrings, relative to the home directory.</summary>
    public const string KeyringDirectory = ".local/share/keyrings";

    /// <summary>File name of the login keyring.</summary>
    public const string LoginKeyringName = "login.keyring";

    /// <summary>Prefix hashed with a PRF output to make the key of a passkey slot (ADR 0168 section 3).</summary>
    public const string PrfKeyPrefix = "hackeros-keyring-prf-v1";

    private const int MasterKeyLength = 32;
    private const ushort DirectoryMode = 0b111_000_000;
    private const ushort FileMode = 0b110_000_000;
    private const int SaltLength = 16;
    private const int NonceLength = 12;

    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly IKeyringCipher _cipher;
    private readonly IAuditLog? _audit;
    private readonly ILogger _logger;
    private readonly int _iterations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UnlockedKeyring? _unlocked;

    /// <summary>Initializes the keyring service.</summary>
    /// <param name="fileSystem">Filesystem the keyring file lives in.</param>
    /// <param name="clock">Clock for entry timestamps.</param>
    /// <param name="cipher">PBKDF2 and AES-GCM implementation.</param>
    /// <param name="audit">Optional audit log (<c>auth.log</c>) for unlocks and resets.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="iterations">PBKDF2 iterations of a newly sealed keyring (tests lower it).</param>
    public UserKeyringService(
        IFileSystemService fileSystem,
        ISimulationClock clock,
        IKeyringCipher cipher,
        IAuditLog? audit = null,
        ILogger<UserKeyringService>? logger = null,
        int iterations = DefaultIterations)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
        _audit = audit;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        _iterations = iterations;
    }

    /// <summary>Gets the user whose keyring is unlocked, or <see langword="null"/> when locked.</summary>
    public LocalUserId? UnlockedUserId => Volatile.Read(ref _unlocked)?.Principal.UserId;

    /// <summary>Gets the path of a user's login keyring.</summary>
    /// <param name="homePath">The user's home directory.</param>
    public static VirtualPath GetKeyringPath(string homePath) =>
        VirtualPath.Parse($"{homePath.TrimEnd('/')}/{KeyringDirectory}/{LoginKeyringName}");

    /// <inheritdoc />
    public async ValueTask UnlockAsync(AuthenticatedPrincipal principal, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ForgetKey();
            if (string.IsNullOrEmpty(password))
            {
                // No secret to derive a key from: like an auto-login session, the keyring stays locked.
                return;
            }

            IAppFileSystemGateway files = CreateUserGateway(principal);
            VirtualPath path = GetKeyringPath(principal.HomePath);
            SettingsFileReadResult read = await SettingsTextFile.ReadAsync(files, path, cancellationToken).ConfigureAwait(false);
            if (read.Error is not null)
            {
                _logger.LogWarning("keyring: cannot read {Path}: {Code}, keyring stays locked", path.Value, read.Error.Code);
                return;
            }

            if (read.Exists && await TryOpenWithPasswordAsync(principal, read.Content!, password, cancellationToken).ConfigureAwait(false) is { } opened)
            {
                _unlocked = opened;
                if (KeyringFileFormat.ReadVersion(read.Content!) != KeyringFileFormat.VersionWithSlots)
                {
                    // ADR 0168 section 3: a format 1 keyring is rewritten with a master key and slots.
                    await SaveAsync(opened, cancellationToken).ConfigureAwait(false);
                    Audit(principal, "keyring.upgrade", AuditOutcome.Success);
                }

                Audit(principal, "keyring.unlock", AuditOutcome.Success);
                return;
            }

            if (read.Exists)
            {
                // Sealed under another password (reset by an administrator) or damaged.
                _logger.LogWarning("keyring: {Path} cannot be opened with the login password, set aside as {Old}", path.Value, LoginKeyringName + ".old");
                await SetAsideCoreAsync(files, path, cancellationToken).ConfigureAwait(false);
                Audit(principal, "keyring.reset", AuditOutcome.Success);
            }

            byte[] masterKey = RandomNumberGenerator.GetBytes(MasterKeyLength);
            KeyringSlot slot = await CreatePasswordSlotAsync(masterKey, password, cancellationToken).ConfigureAwait(false);
            UnlockedKeyring created = new(principal, masterKey, [slot], []);
            await SaveAsync(created, cancellationToken).ConfigureAwait(false);
            _unlocked = created;
            Audit(principal, "keyring.create", AuditOutcome.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            ForgetKey();
            _logger.LogWarning(exception, "keyring: unlock failed, keyring stays locked");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Lock()
    {
        _gate.Wait();
        try
        {
            ForgetKey();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Wraps the master key of the unlocked keyring under a new password (new salt), for
    /// <c>passwd</c>; the passkey slots stay valid. The caller has already verified the old password.
    /// </summary>
    /// <param name="userId">The user changing their password; must own the unlocked keyring.</param>
    /// <param name="newPassword">New password.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">That user's keyring is not unlocked.</exception>
    public async ValueTask ChangePasswordAsync(LocalUserId userId, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UnlockedKeyring current = RequireUnlocked(userId);
            KeyringSlot slot = await CreatePasswordSlotAsync(current.Key, newPassword, cancellationToken).ConfigureAwait(false);
            UnlockedKeyring resealed = current with
            {
                Slots = [slot, .. current.Slots.Where(static existing => existing.Kind != KeyringFileFormat.PasswordSlot)]
            };
            await SaveAsync(resealed, cancellationToken).ConfigureAwait(false);
            _unlocked = resealed;
            Audit(current.Principal, "keyring.rekey", AuditOutcome.Success);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask UnlockWithPasskeyAsync(
        AuthenticatedPrincipal principal,
        string credentialId,
        byte[]? prfOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrEmpty(credentialId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ForgetKey();
            if (prfOutput is not { Length: > 0 })
            {
                _logger.LogInformation("keyring: passkey without PRF output, keyring stays locked");
                return;
            }

            IAppFileSystemGateway files = CreateUserGateway(principal);
            SettingsFileReadResult read = await SettingsTextFile
                .ReadAsync(files, GetKeyringPath(principal.HomePath), cancellationToken)
                .ConfigureAwait(false);
            if (!read.Exists || KeyringFileFormat.ReadEnvelopeV2(read.Content!) is not { } envelope
                || envelope.Slots.FirstOrDefault(slot => slot.Kind == KeyringFileFormat.PasskeySlot && slot.CredentialId == credentialId) is not { } slot)
            {
                _logger.LogInformation("keyring: no slot for this passkey, keyring stays locked");
                return;
            }

            byte[] slotKey = DerivePasskeySlotKey(prfOutput);
            byte[]? masterKey = await _cipher.DecryptAsync(slotKey, slot.Nonce, slot.WrappedKey, cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(slotKey);
            if (masterKey is null || await OpenEntriesAsync(masterKey, envelope, cancellationToken).ConfigureAwait(false) is not { } entries)
            {
                Audit(principal, "keyring.unlock", AuditOutcome.Denied);
                return;
            }

            _unlocked = new UnlockedKeyring(principal, masterKey, envelope.Slots, entries);
            Audit(principal, "keyring.unlock", AuditOutcome.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            ForgetKey();
            _logger.LogWarning(exception, "keyring: passkey unlock failed, keyring stays locked");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds (or replaces) the slot of one passkey to the unlocked keyring, so that signing in with
    /// that passkey opens it (ADR 0168 section 3).
    /// </summary>
    /// <param name="userId">The owner of the unlocked keyring.</param>
    /// <param name="credentialId">Base64url id of the passkey.</param>
    /// <param name="prfOutput">The PRF output of that passkey with its salt.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="HackerOs.AppSdk.Keyring.HackKeyringLockedException">That user's keyring is not unlocked.</exception>
    public async ValueTask AddPasskeySlotAsync(LocalUserId userId, string credentialId, byte[] prfOutput, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialId);
        ArgumentNullException.ThrowIfNull(prfOutput);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UnlockedKeyring current = RequireUnlocked(userId);
            byte[] slotKey = DerivePasskeySlotKey(prfOutput);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
            byte[] wrapped = await _cipher.EncryptAsync(slotKey, nonce, current.Key, cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(slotKey);
            UnlockedKeyring next = current with
            {
                Slots =
                [
                    .. current.Slots.Where(slot => slot.CredentialId != credentialId),
                    new KeyringSlot(KeyringFileFormat.PasskeySlot, credentialId, null, 0, nonce, wrapped)
                ]
            };
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _unlocked = next;
            Audit(current.Principal, "keyring.slot-add", AuditOutcome.Success);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the slot of one passkey. The slots are in clear, so a locked keyring is edited too.
    /// </summary>
    /// <param name="principal">The owner of the keyring.</param>
    /// <param name="credentialId">Base64url id of the passkey.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a slot was removed.</returns>
    public async ValueTask<bool> RemovePasskeySlotAsync(AuthenticatedPrincipal principal, string credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrEmpty(credentialId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unlocked is { } current && current.Principal.UserId == principal.UserId)
            {
                if (current.Slots.All(slot => slot.CredentialId != credentialId))
                {
                    return false;
                }

                UnlockedKeyring next = current with { Slots = [.. current.Slots.Where(slot => slot.CredentialId != credentialId)] };
                await SaveAsync(next, cancellationToken).ConfigureAwait(false);
                _unlocked = next;
                Audit(principal, "keyring.slot-remove", AuditOutcome.Success);
                return true;
            }

            IAppFileSystemGateway files = CreateUserGateway(principal);
            VirtualPath path = GetKeyringPath(principal.HomePath);
            SettingsFileReadResult read = await SettingsTextFile.ReadAsync(files, path, cancellationToken).ConfigureAwait(false);
            if (!read.Exists || KeyringFileFormat.ReadEnvelopeV2(read.Content!) is not { } envelope
                || envelope.Slots.All(slot => slot.CredentialId != credentialId))
            {
                return false;
            }

            string text = KeyringFileFormat.WriteEnvelope(
                envelope with { Slots = [.. envelope.Slots.Where(slot => slot.CredentialId != credentialId)] });
            FileSystemError? error = await SettingsTextFile
                .EditAsync(files, path, _ => text, createIfMissing: false, FileMode, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                throw new IOException($"Cannot save the keyring '{path.Value}': {error.Code}.");
            }

            Audit(principal, "keyring.slot-remove", AuditOutcome.Success);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets whether the keyring file of <paramref name="principal"/> has a slot for a passkey.</summary>
    /// <param name="principal">The owner of the keyring.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The base64url ids of the passkeys that open the keyring.</returns>
    public async ValueTask<IReadOnlySet<string>> GetPasskeySlotIdsAsync(AuthenticatedPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (_unlocked is { } current && current.Principal.UserId == principal.UserId)
        {
            return PasskeyIds(current.Slots);
        }

        SettingsFileReadResult read = await SettingsTextFile
            .ReadAsync(CreateUserGateway(principal), GetKeyringPath(principal.HomePath), cancellationToken)
            .ConfigureAwait(false);
        return read.Exists && KeyringFileFormat.ReadEnvelopeV2(read.Content!) is { } envelope
            ? PasskeyIds(envelope.Slots)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Sets another user's keyring aside after an administrator reset their password: it is renamed
    /// <c>login.keyring.old</c> (its secrets stay sealed under the forgotten password) and their next
    /// login creates an empty one. Runs as a system operation.
    /// </summary>
    /// <param name="homePath">The user's home directory.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when a keyring was set aside.</returns>
    public async ValueTask<bool> SetAsideAsync(string homePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homePath);
        IAppFileSystemGateway files = new AppFileSystemGateway(_fileSystem, _clock, SystemOperation, ["system"]);
        VirtualPath path = GetKeyringPath(homePath);
        bool moved = await SetAsideCoreAsync(files, path, cancellationToken).ConfigureAwait(false);
        if (moved)
        {
            _audit?.Record(new AuditEntry(_clock.UtcNow, Guid.NewGuid(), "system", "keyring.reset", path.Value, AuditOutcome.Success));
        }

        return moved;
    }

    /// <summary>Reads one secret of the unlocked keyring.</summary>
    internal async ValueTask<string?> GetAsync(LocalUserId userId, string appId, string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return RequireUnlocked(userId).Entries.FirstOrDefault(entry => Matches(entry, appId, id))?.Secret;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates or replaces one secret and saves the keyring.</summary>
    internal async ValueTask SetAsync(LocalUserId userId, string appId, string id, string secret, string? label, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UnlockedKeyring current = RequireUnlocked(userId);
            List<KeyringEntryRecord> entries = [.. current.Entries.Where(entry => !Matches(entry, appId, id))];
            entries.Add(new KeyringEntryRecord(appId, id, label, secret, _clock.UtcNow));
            UnlockedKeyring next = current with { Entries = entries };
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _unlocked = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes one secret and saves the keyring.</summary>
    internal async ValueTask<bool> DeleteAsync(LocalUserId userId, string appId, string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UnlockedKeyring current = RequireUnlocked(userId);
            List<KeyringEntryRecord> entries = [.. current.Entries.Where(entry => !Matches(entry, appId, id))];
            if (entries.Count == current.Entries.Count)
            {
                return false;
            }

            UnlockedKeyring next = current with { Entries = entries };
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _unlocked = next;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lists entries of one app, or of every app when <paramref name="appId"/> is <see langword="null"/>.</summary>
    internal async ValueTask<IReadOnlyList<KeyringEntryRecord>> ListAsync(LocalUserId userId, string? appId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. RequireUnlocked(userId).Entries
                .Where(entry => appId is null || string.Equals(entry.AppId, appId, StringComparison.Ordinal))
                .OrderBy(static entry => entry.AppId, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Id, StringComparer.Ordinal)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets whether <paramref name="userId"/>'s keyring is the unlocked one.</summary>
    internal bool IsUnlockedFor(LocalUserId userId) => Volatile.Read(ref _unlocked)?.Principal.UserId == userId;

    private static bool Matches(KeyringEntryRecord entry, string appId, string id) =>
        string.Equals(entry.AppId, appId, StringComparison.Ordinal) && string.Equals(entry.Id, id, StringComparison.Ordinal);

    private UnlockedKeyring RequireUnlocked(LocalUserId userId) =>
        _unlocked is { } unlocked && unlocked.Principal.UserId == userId
            ? unlocked
            : throw new HackerOs.AppSdk.Keyring.HackKeyringLockedException("The keyring of this user is locked.");

    private async ValueTask<UnlockedKeyring?> TryOpenWithPasswordAsync(
        AuthenticatedPrincipal principal, string text, string password, CancellationToken cancellationToken)
    {
        if (KeyringFileFormat.ReadEnvelopeV2(text) is { } envelope)
        {
            if (envelope.Slots.FirstOrDefault(static slot => slot.Kind == KeyringFileFormat.PasswordSlot) is not { } slot)
            {
                return null;
            }

            byte[] slotKey = await _cipher.DeriveKeyAsync(password, slot.Salt!, slot.Iterations, cancellationToken).ConfigureAwait(false);
            byte[]? masterKey = await _cipher.DecryptAsync(slotKey, slot.Nonce, slot.WrappedKey, cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(slotKey);
            if (masterKey is null || await OpenEntriesAsync(masterKey, envelope, cancellationToken).ConfigureAwait(false) is not { } entries)
            {
                return null;
            }

            return new UnlockedKeyring(principal, masterKey, envelope.Slots, entries);
        }

        if (KeyringFileFormat.ReadEnvelope(text) is not { } legacy)
        {
            return null;
        }

        // Format 1: the entries are sealed under the password key itself. They move under a new
        // master key, wrapped in a password slot; the caller saves the keyring in format 2.
        byte[] key = await _cipher.DeriveKeyAsync(password, legacy.Salt, legacy.Iterations, cancellationToken).ConfigureAwait(false);
        byte[]? plaintext = await _cipher.DecryptAsync(key, legacy.Nonce, legacy.SealedData, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(key);
        if (plaintext is null || KeyringFileFormat.ReadEntries(plaintext) is not { } legacyEntries)
        {
            return null;
        }

        CryptographicOperations.ZeroMemory(plaintext);
        byte[] newMasterKey = RandomNumberGenerator.GetBytes(MasterKeyLength);
        KeyringSlot passwordSlot = await CreatePasswordSlotAsync(newMasterKey, password, cancellationToken).ConfigureAwait(false);
        return new UnlockedKeyring(principal, newMasterKey, [passwordSlot], legacyEntries);
    }

    private async ValueTask<List<KeyringEntryRecord>?> OpenEntriesAsync(byte[] masterKey, KeyringEnvelopeV2 envelope, CancellationToken cancellationToken)
    {
        byte[]? plaintext = await _cipher.DecryptAsync(masterKey, envelope.Nonce, envelope.SealedData, cancellationToken).ConfigureAwait(false);
        if (plaintext is null)
        {
            return null;
        }

        List<KeyringEntryRecord>? entries = KeyringFileFormat.ReadEntries(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return entries;
    }

    private async ValueTask<KeyringSlot> CreatePasswordSlotAsync(byte[] masterKey, string password, CancellationToken cancellationToken)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] slotKey = await _cipher.DeriveKeyAsync(password, salt, _iterations, cancellationToken).ConfigureAwait(false);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] wrapped = await _cipher.EncryptAsync(slotKey, nonce, masterKey, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(slotKey);
        return new KeyringSlot(KeyringFileFormat.PasswordSlot, null, salt, _iterations, nonce, wrapped);
    }

    /// <summary>The key of a passkey slot: SHA-256 of a fixed prefix and the PRF output (ADR 0168 section 3).</summary>
    internal static byte[] DerivePasskeySlotKey(byte[] prfOutput)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(PrfKeyPrefix);
        byte[] input = new byte[prefix.Length + prfOutput.Length];
        prefix.CopyTo(input, 0);
        prfOutput.CopyTo(input, prefix.Length);
        byte[] key = SHA256.HashData(input);
        CryptographicOperations.ZeroMemory(input);
        return key;
    }

    private static HashSet<string> PasskeyIds(IEnumerable<KeyringSlot> slots) => new(
        slots.Where(static slot => slot.Kind == KeyringFileFormat.PasskeySlot && slot.CredentialId is not null).Select(static slot => slot.CredentialId!),
        StringComparer.Ordinal);

    private async ValueTask SaveAsync(UnlockedKeyring keyring, CancellationToken cancellationToken)
    {
        byte[] plaintext = KeyringFileFormat.WriteEntries(keyring.Entries);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] sealedData = await _cipher.EncryptAsync(keyring.Key, nonce, plaintext, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(plaintext);
        string text = KeyringFileFormat.WriteEnvelope(new KeyringEnvelopeV2(keyring.Slots, nonce, sealedData));

        IAppFileSystemGateway files = CreateUserGateway(keyring.Principal);
        VirtualPath path = GetKeyringPath(keyring.Principal.HomePath);
        await EnsurePrivateDirectoryAsync(files, keyring.Principal.HomePath, cancellationToken).ConfigureAwait(false);
        FileSystemError? error = await SettingsTextFile
            .EditAsync(files, path, _ => text, createIfMissing: true, FileMode, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            throw new IOException($"Cannot save the keyring '{path.Value}': {error.Code}.");
        }
    }

    private static async ValueTask EnsurePrivateDirectoryAsync(IAppFileSystemGateway files, string homePath, CancellationToken cancellationToken)
    {
        VirtualPath directory = VirtualPath.Parse($"{homePath.TrimEnd('/')}/{KeyringDirectory}");
        FileSystemResult<FileSystemEntrySnapshot> stat = await files.StatAsync(new FileSystemStatRequest(directory), cancellationToken).ConfigureAwait(false);
        if (stat.Succeeded)
        {
            return;
        }

        // ~/.local/share as usual (0755), the keyrings directory itself owner-only (0700), as GNOME.
        FileSystemError? parent = await SettingsTextFile
            .EnsureDirectoryAsync(files, SettingsTextFile.GetParent(directory), cancellationToken)
            .ConfigureAwait(false);
        if (parent is not null)
        {
            throw new IOException($"Cannot create '{SettingsTextFile.GetParent(directory).Value}': {parent.Code}.");
        }

        FileSystemMutationResult created = await files
            .CreateAsync(new FileSystemCreateRequest(directory, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(DirectoryMode)), cancellationToken)
            .ConfigureAwait(false);
        if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw new IOException($"Cannot create '{directory.Value}': {created.Transaction.Error?.Code}.");
        }
    }

    /// <summary>Copies the keyring to <c>login.keyring.old</c> (replacing an older one) and deletes it.</summary>
    private static async ValueTask<bool> SetAsideCoreAsync(IAppFileSystemGateway files, VirtualPath path, CancellationToken cancellationToken)
    {
        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(files, path, cancellationToken).ConfigureAwait(false);
        if (!read.Exists)
        {
            return false;
        }

        VirtualPath old = VirtualPath.Parse(path.Value + ".old");
        FileSystemError? copied = await SettingsTextFile
            .EditAsync(files, old, _ => read.Content!, createIfMissing: true, FileMode, cancellationToken)
            .ConfigureAwait(false);
        if (copied is not null)
        {
            throw new IOException($"Cannot write '{old.Value}': {copied.Code}.");
        }

        FileSystemResult<FileSystemEntrySnapshot> entry = await files.StatAsync(new FileSystemStatRequest(path), cancellationToken).ConfigureAwait(false);
        FileSystemResult<FileSystemEntrySnapshot> parent = await files
            .StatAsync(new FileSystemStatRequest(SettingsTextFile.GetParent(path)), cancellationToken)
            .ConfigureAwait(false);
        if (!entry.Succeeded || !parent.Succeeded)
        {
            throw new IOException($"Cannot set '{path.Value}' aside.");
        }

        FileSystemMutationResult deleted = await files
            .DeleteAsync(new FileSystemDeleteRequest(path, entry.Value!.Metadata.Revision, parent.Value!.Metadata.Revision), cancellationToken)
            .ConfigureAwait(false);
        return deleted.Succeeded
            ? true
            : throw new IOException($"Cannot delete '{path.Value}': {deleted.Transaction.Error?.Code}.");
    }

    private IAppFileSystemGateway CreateUserGateway(AuthenticatedPrincipal principal) => new AppFileSystemGateway(
        _fileSystem,
        _clock,
        new AppOperationContext
        {
            AppId = KeyringAppId,
            UserId = principal.UserId.ToString(),
            UserName = principal.LoginName.Value,
            UserAuthority = principal.Authority,
            GrantedCapabilities = new HashSet<string>(
                [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite],
                StringComparer.Ordinal),
            IsSystemOperation = false
        },
        [.. principal.GroupIds.Select(static id => id.ToString())]);

    /// <summary>Operation identity of the keyring daemon in the audit log.</summary>
    internal const string KeyringAppId = "org.hackeros.keyring";

    private static readonly AppOperationContext SystemOperation = new()
    {
        AppId = KeyringAppId,
        UserId = "system",
        UserName = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };

    private void ForgetKey()
    {
        if (_unlocked is { } unlocked)
        {
            CryptographicOperations.ZeroMemory(unlocked.Key);
        }

        _unlocked = null;
    }

    private void Audit(AuthenticatedPrincipal principal, string action, AuditOutcome outcome) =>
        _audit?.Record(new AuditEntry(
            _clock.UtcNow, Guid.NewGuid(), principal.UserId.ToString(), action, GetKeyringPath(principal.HomePath).Value, outcome));

    /// <summary>The open keyring: its owner, the master key, the slots that wrap it, and the entries.</summary>
    private sealed record UnlockedKeyring(
        AuthenticatedPrincipal Principal, byte[] Key, IReadOnlyList<KeyringSlot> Slots, IReadOnlyList<KeyringEntryRecord> Entries);
}
