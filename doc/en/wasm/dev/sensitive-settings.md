> [Version française](../../../fr/wasm/dev/sensitive-settings.md)

# Sensitive settings, keyring and audit log

Implementation of work item `v3/8-settings-security` of the
[completion plan](plans/wasm-v3-completion-plan.md), according to
[ADR 0100](adr/0100-sensitive-settings-and-protected-write-audit.md). This
document describes the shipped code; the ADR remains the reference for the decisions.

## Goal

- A non-public setting key (`SettingSensitivity` other than `Public`) can
  only be read by whoever can read its private file; others see it
  "masked".
- Secrets (`SecretReference`) are encrypted at rest in a per-user keyring,
  unlocked at login.
- Each modification under `/etc`, `/bin` and `/var` (except `/var/log`) leaves a
  line in `/var/log/audit.log`, successful or refused, in `auditd` format.

## 1. Private files and masking

### Locations

| Layer | Public keys | Non-public keys |
| --- | --- | --- |
| System | `/etc/<appId>/<doc>.config` (`0644`) | `/etc/<appId>/<doc>.private.config` (`0600`, root) |
| User | `~/.config/<appId>/<doc>.config` | `~/.config/<appId>/<doc>.private.config` (`0600`) |

The private file is read and watched only if the document's schema has at least
one non-public key (`SettingsPrivateFiles.HasPrivateFields`).

| Sensitivity | File | User layer |
| --- | --- | --- |
| `Public` | `<doc>.config` | yes |
| `Private` | `<doc>.private.config` | yes |
| `SecretReference` | `<doc>.private.config`; the value is the identifier of a keyring entry | yes |
| `Restricted` | `<doc>.private.config` in `/etc` only | no: ignored with a warning, and `SetAsync` on the user layer throws `InvalidOperationException` |

### Reading (`LayeredConfigResolver`)

`LayeredConfigResolver.Resolve(schema, LayeredConfigFiles)` reads up to four
files. A key in the wrong file is ignored with a warning,
like an invalid line:

- `sensitive key 'k' belongs in <chemin privé>, ignored`;
- `public key 'k' belongs in <chemin public>, ignored`;
- `restricted key 'k' is only honored in the system private file, ignored`;
- `[locked] is only honored in the public system file, section ignored`.

The `[locked]` section is read only in the system's public file. For a
non-public key, the order is:

1. private user value (if the key is neither locked nor `Restricted`);
2. **masked**, user origin, if the user's private file is
   unreadable;
3. private system value;
4. **masked**, system origin, if the system's private file is unreadable;
5. default value.

A file unreadable for a permissions reason (`PermissionDenied`,
`CapabilityDenied`, `AuthorityDenied`, `ProtectedEntry`) does not make the
read fail. `LayeredConfigSnapshot.MaskedKeys` lists the masked keys; their
entry in `Values` keeps the declared default value, never the real one.

### Writing (`SettingsPrivateFiles.Apply`)

A write reads the layer's two files, moves misplaced keys
(in both directions), then files each key according to its sensitivity; removing a
key erases it from both files. The private file is written first (created
as `0600`), then the public file, each through a revision-based edit
(`SettingsTextFile.EditAsync`) that keeps comments and unknown keys.

### SDK API

```csharp
await using HackSettingsDocument mail = await HackSettings.OpenAsync(AppContext);
HackSettingValue server = mail.GetValue("Account.server");   // IsMasked, Source, IsLocked, Sensitivity
string shown = server.DisplayText;                            // "••••••••" si masquée ou secrète
bool masked = mail.IsMasked("Account.server");
string host = mail.Get<string>("Account.server");             // HackSettingMaskedException si masquée

await mail.SetSecretAsync("Account.password", "s3cr3t");      // trousseau + référence dans le fichier privé
string? password = await mail.GetSecretAsync("Account.password");
await mail.SetSecretAsync("Account.password", null);          // efface l'entrée et la référence
```

- `HackSettingMaskedException` derives from `UnauthorizedAccessException` and gives
  `QualifiedKey` and `PrivatePath`.
- A secret's reference is `settings:<document>:<group.key>`.
- `GetSecretAsync`/`SetSecretAsync` require a `SecretReference` key and are
  not offered on OS documents (`NotSupportedException`).
- The `Changed` event also signals a change of masked state.

### Exports and diagnostics

`StorageSnapshotRedactor` masks, in the storage snapshot of the Diagnostics
app (`AppStorageDiagnosticsGateway`), the content of `*.private.config`
files and of any file under `~/.local/share/keyrings/`: the
chunk becomes `[masked: ADR 0100]`. Synchronization covers only the
home folder, so the private file in `/etc` (the only carrier of `Restricted`
keys) is never synchronized. The full backup of ADR 0099,
reserved to root, keeps everything.

## 2. Login keyring

| Element | Value |
| --- | --- |
| File | `~/.local/share/keyrings/login.keyring` (`0600`, folder `0700`) |
| Encryption | AES-256-GCM, fresh 12-byte nonce at each write |
| Key | Random 32-byte master key, wrapped (AES-256-GCM) in slots: one password slot (PBKDF2-SHA-256, 210,000 iterations, 16-byte salt) and one slot per passkey PRF (SHA-256 of `hackeros-keyring-prf-v1` and the PRF output, ADR 0168) |
| Engine | Web Crypto (`cryptoHasher.js`, `WebCryptoKeyringCipher`) in the host, `ManagedKeyringCipher` (.NET) in tests |
| Format | JSON `hackeros-keyring` v2: `cipher`, `slots` (`kind` `password` with `kdf`, `iterations`, `salt`; or `passkey` with `credentialId`; then `nonce`, `wrappedKey`), `nonce`, `ciphertext` (base64). A v1 file (entries sealed directly under the password key) is read and rewritten as v2 at the next password login (`keyring.upgrade`) |

### Lifecycle (`UserKeyringService`)

- **Login** (`LocalSessionService`): `UnlockAsync(principal, password)`
  opens the keyring, or creates it empty. A user without a password has no
  keyring: it stays locked.
- **Passkey login** (ADR 0168): `UnlockWithPasskeyAsync(principal,
  keyId, prfOutput)` opens the keyring through that key's slot.
  Without a PRF output or without a slot, the keyring stays locked; it is
  never set aside by this path. A key added while the keyring
  is open gets its slot (`AddPasskeySlotAsync`); a removed key
  loses its own (`RemovePasskeySlotAsync`, possible with the keyring locked, because
  slots are in the clear). See [`passkeys.md`](passkeys.md).
- **Different password** (reset by an administrator): the
  unreadable keyring is renamed `login.keyring.old` and an empty keyring replaces it,
  as under GNOME.
- **Logout, shutdown**: `Lock()` erases the key from memory.
- **Password change**: `ChangePasswordAsync(userId, new)`
  replaces only the password slot (new salt); the passkey
  slots stay valid. **Set aside by an
  administrator**: `SetAsideAsync(homePath)`. The `passwd` command and
  the "Users" tab of Settings call them (ADR 0120, see
  [§4](#4-passwords-passwd-and-reset) below).
- The authentication log (`IAuditLog`) receives `keyring.unlock`,
  `keyring.create`, `keyring.reset` (also for a set-aside by the
  administrator), `keyring.rekey` and `keyring.upgrade` (move to format 2),
  without any secret.

### SDK API (`HackKeyring`)

```csharp
if (HackKeyring.IsUnlocked(AppContext))
{
    await HackKeyring.SetAsync(AppContext, "imap", "s3cr3t", label: "Compte IMAP");
    string? secret = await HackKeyring.GetAsync(AppContext, "imap");
    IReadOnlyList<HackKeyringEntry> mine = await HackKeyring.ListAsync(AppContext);
    await HackKeyring.DeleteAsync(AppContext, "imap");
}

// Gestionnaire de mots de passe seulement (capacité dangereuse keyring.read-all) :
IReadOnlyList<HackKeyringEntry> all = await HackKeyring.ListAllAsync(AppContext);
string? other = await HackKeyring.GetAsync(AppContext, "org.example.mail", "imap");
```

- An app sees only its own entries. Reading those of another app or listing
  everything requires `keyring.read-all`; without it, `UnauthorizedAccessException`.
- A locked keyring throws `HackKeyringLockedException`.
- An identifier is at most 256 characters, with no control character.
- Each method also exists without a context (ambient context of ADR 0043).
- `IAppExecutionContext.Keyring` (`IAppKeyringGateway`) is the gateway; it
  has a default implementation that is always locked, for test contexts
  and system services (ADR 0097), which have no keyring.

## 3. Audit log

### Writing

`FileSystemService` receives an optional `IFileSystemAuditSink`
(`FileSystemAuditLog` in the host). After each creation, write,
rename (`rename`), copy (`copy`, on the destination path),
deletion and `chmod`, if the path or its destination is audited
(`FileSystemAuditPolicy.IsAudited`: `/etc`, `/bin`, `/var` except `/var/log`),
a line is queued for the single log writer (ADR 0045) to
`LogFileTarget.AuditLog`: `/var/log/audit.log`, `0600`, rotation 1 MiB × 5.

```
type=PATH msg=audit(1792218000.123:42): op=write path="/etc/hosts" uid=0 auid=1000 pid=231 comm="nano" app="org.hackeros.commands.nano" res=success UID="root" AUID="alice"
```

- `uid`: the identity that writes (`root` for a system operation or one elevated by
  `sudo`/approval); `auid`: the original human, `unset` for the OS.
- HackerOS has no numeric identifiers: `root` is `0`, a user
  `1000`, `unset` `4294967295`; the `UID=` and `AUID=` fields give the names,
  like `auditd`'s enriched fields.
- A refusal adds `err=<code>` (for example `err=PermissionDenied`). The content
  is never written.
- HackerOS has no `chown` operation: there is therefore nothing to audit for it.

`AuditLineFormat` (Simulation.Abstractions) writes and reads back these lines.

### Reading

- **`ausearch`** (`org.hackeros.commands.ausearch`, with `sudo`): `-f path`
  (the path, a file below it, or a file name), `-ua`, `-ui`, `-ul`
  (user, `uid`, `auid`), `-c comm`, `--success yes|no` (`-sv`),
  `-if file`, `-i` (accepted). Reads the `.5` to `.1` archives then the log;
  displays `----`, `time->…` then the line; `<no matches>` and code 1 with no
  result.
- **Error Log Viewer**: the `audit.log` family appears for an
  administrator or an elevated session (`LogSource.RequiresRoot`), and is not
  read by the "all logs" view, so as not to ask for elevation at
  each opening. A refused write is displayed as a warning, a successful
  write as information, with the app as the source.

## 4. Passwords: `passwd` and reset

Decision: [ADR 0120](adr/0120-passwd-and-administrator-password-reset.md).

- **`LocalPasswordService`** (`Platform.Core/Sessions`) does the work of
  `passwd` and `/etc/shadow`: it hashes the new password as at
  account creation (`LocalPasswordHasher`, PBKDF2-SHA-256, Web Crypto in
  the browser), stores it through `ILocalUserRepository.SetCredentialAsync`,
  makes the keyring follow and writes `passwd.change`, `passwd.reset` or
  `passwd.delete` to `auth.log`.
- **One's own password**, without a capability: the current password is
  verified (empty if the account has none), the new one must be 8 to 256
  characters with no control character and differ from the current one. The keyring
  is re-encrypted (`ChangePasswordAsync`); if it was not open, it is
  first opened with the old password.
- **Another account's password**: dangerous capability `users.manage`
  and an administrator through the authentication agent (nothing under `sudo`). The
  account's keyring is set aside (`login.keyring.old`); if it is the session's,
  it is re-encrypted (reset) or set aside then locked (removal). Removing
  the password of the last active administrator who has one is refused.
- **SDK**: `HackUserAccounts` (`ListAsync`, `ChangePasswordAsync`,
  `ResetPasswordAsync`, `RemovePasswordAsync`, `ValidateNewPassword`) on
  `IAppExecutionContext.UserAccounts`, wired by
  `SystemAdministrationGatewayServices.Passwords`.
- **Terminal**: `passwd`, `passwd NAME` and `passwd -d NAME` under `sudo`,
  `passwd -S [NAME]` ([`linux-commands.md`](linux-commands.md)). Passwords are
  read with echo off: `TerminalExecutionContext.ReadSecretLineAsync`
  turns off `ITerminalInputMode.EchoEnabled` of the terminal input, as
  `getpass(3)` turns off `ECHO` ([`terminal-emulator.md`](terminal-emulator.md)).
- **Settings > Users** (`UsersPanel`): list of accounts with their
  `P`/`NP`/`L` state, change of one's own password, and for an administrator
  the reset or removal of another account's password, adding
  and deleting accounts (ADR 0169, [`settings-app.md`](settings-app.md)).
  The session's passkeys are in `PasskeysPanel` (ADR 0168).

## Known limits

- OS documents (`OsSettingsDocument`) accept only public
  keys: a schema with a non-public key throws `ArgumentException`.
- The Settings app does not yet have a screen for apps' settings; a UI
  that displays them uses `HackSettingValue.DisplayText` for the mask.
- A file created by an approval or `sudo` belongs to the human user
  (existing behavior of ADR 0042), including a private file in
  `/etc` created this way; its mode stays `0600`.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Settings/SensitiveSettingsTests.cs`:
  private file `0600`, masking of a system value, user value
  taking priority, moving of a misplaced key, `Restricted` refused on the
  user side, secret in the keyring, resolver warnings.
- `Tests/HackerOs.Platform.Core.Tests/Keyring/UserKeyringServiceTests.cs`:
  creation and modes, reconnection, set-aside on a different password,
  password change, set-aside by the administrator, user
  without a password, per-app confinement and `keyring.read-all`, encryption.
- `Tests/HackerOs.Platform.Core.Tests/Sessions/PasskeyServiceTests.cs`:
  PRF slot, opening through a passkey, key without PRF, password change
  that keeps the slots, migration from format 1.
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/FileSystemAuditTests.cs`:
  policy, round-trip format, accepted and refused writes in
  `audit.log` (`0600`), `uid`/`auid` under `sudo`.
- `Tests/HackerOs.Commands.Tests/CoreCommandsTests.cs`: `ausearch` filters.
- `Tests/HackerOs.Platform.Core.Tests/Sessions/LocalPasswordServiceTests.cs`:
  change with keyring kept, wrong current password, rules,
  reset that sets the keyring aside, removal refused for the last
  administrator, gateway (`users.manage` and administrator).
- `Tests/HackerOs.Commands.Wave5.Tests/PasswdCommandTests.cs`: echo-off
  prompts, different confirmation, `sudo passwd NAME`, `-d`, `-S`.
- `Tests/HackerOs.UI.E2E.Tests/PasswdTerminalTests.cs`: `passwd` in the real
  terminal displays nothing of what is typed, then only the new value
  opens the session.

## Task list

- [x] Companion file `<doc>.private.config` (`0600`) and filing of keys at write
- [x] Reading of the four files, warnings for misplaced keys, masking
- [x] `HackSettingValue`, `HackSettingMaskedException`, `GetSecretAsync`/`SetSecretAsync`
- [x] `login.keyring` encrypted with AES-GCM (Web Crypto) under a PBKDF2 key
- [x] Unlock at login, lock at logout, `.old` set-aside
- [x] `HackKeyring` and dangerous capability `keyring.read-all`
- [x] Masking of private files and the keyring in diagnostic snapshots
- [x] `/var/log/audit.log` log written by the file system
- [x] `ausearch` command and reading of the audit log by the Error Log Viewer
- [x] Tests
- [x] `passwd` command and reset by an administrator (ADR 0120: `LocalPasswordService`, `HackUserAccounts`, "Users" tab)
