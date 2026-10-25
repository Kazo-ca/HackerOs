> [Version française](../../../../fr/wasm/dev/adr/0100-sensitive-settings-and-protected-write-audit.md)

# ADR 0100: sensitive settings (private files, masking, encrypted keyring) and audit of protected writes

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/8-settings-security`).
§2 follows Benoit's answer of 2026-10-17: one keyring per user,
encrypted by a key derived from their password.

- **Complements:** [ADR 0044](0044-settings-as-files.md) (settings as files,
  access through permissions), [ADR 0045](0045-logs-as-files.md) (logs as
  files, masking by key name), ADR 0042 (elevation).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/8-settings-security`; gaps R16, R17; deferred decision no. 9.

## Context

- `SettingSensitivity` (`Public`, `Private`, `SecretReference`, `Restricted`,
  `SettingsSchema.cs:26`) is declared in the settings schema and in the
  manifest, but only `Public` is used and nothing acts on the other
  values.
- A settings document is **one** file (`/etc/<appId>/<doc>.config`,
  `~/.config/<appId>/<doc>.config`); its permissions apply to all its
  keys. A key therefore cannot be hidden from someone who reads the file.
- The only existing masking is that of the logs, by key name
  (`SensitiveKeyDiagnosticRedactor`).
- Administrator elevation is logged in `/var/log/auth.log`, but the
  write to `/etc` that follows is not.
- The only use of Web Crypto is the PBKDF2 hashing of passwords
  (`WebCryptoPasswordHasher`); nothing is encrypted at rest.

## Decision

### 1. Sensitive keys go into a private file, like `/etc/shadow`

- For a document that has non-public keys, `HackSettings` writes those keys
  into a companion file **`<doc>.private.config`**, in mode `0600`
  (owner: the user in `~/.config`, root in `/etc`), and the public
  keys in `<doc>.config` (unchanged). This is the `/etc/passwd` / `/etc/shadow`
  separation.
- `LayeredConfigResolver` reads both files of each layer; a key
  present in the wrong file (for example a `Private` key left in
  `<doc>.config`) is ignored on read with a warning, like an invalid
  line (ADR 0044 §5), and moved at the next write.
- Meaning of the values:

  | Sensitivity | File | Who reads | Sync, exports |
  |---|---|---|---|
  | `Public` | `<doc>.config` | according to the file permissions (`0644`) | yes |
  | `Private` | `<doc>.private.config` | the owner (and root) | yes (the file syncs like the other files of `~`) |
  | `Restricted` | `<doc>.private.config` in `/etc` only | root | never: excluded from sync and diagnostic reports |
  | `SecretReference` | `<doc>.private.config`, the value being a keyring entry identifier (§2) | the owner | the identifier yes, the secret according to §2 |

- **Masking on read**: when a caller cannot read the private file, `HackSettings`
  returns the key as "masked" (`HackSettingValue.IsMasked`,
  null value) instead of failing; Settings displays `••••••••`, and `cat` of the
  private file fails with `Permission denied`, as for `/etc/shadow`. Diagnostic reports and settings
  copies mask the same way. The full backup of ADR 0099, restricted to
  root, keeps everything.

### 2. Encryption: one keyring per user, unlocked at login (Benoit's choice)

- `SecretReference` values (a site's passwords, API tokens, keys)
  are stored in an encrypted **keyring**, like gnome-keyring:
  `~/.local/share/keyrings/login.keyring`, mode `0600`.
- The keyring is encrypted with AES-GCM by Web Crypto, with a key derived from the
  login password (PBKDF2, same parameters as the existing hashing,
  keyring-specific salt). It is unlocked at login and kept in
  memory during the session, locked again at logout.
- SDK API: `HackKeyring.GetAsync`, `SetAsync`, `DeleteAsync`, per app
  (an app only sees its own entries, except with the dangerous capability
  `keyring.read-all` for a password manager).
- Changing one's password re-encrypts the keyring. **Resetting** another user's
  password (administrator) makes their keyring unreadable:
  the OS renames it `login.keyring.old` and creates an empty one, as under GNOME.
- System services (ADR 0097, `Boot`) have no user keyring.
- The other sensitivities are not encrypted: their protection is the
  file permission, as on Linux.

### 3. Audit of protected writes

- New log **`/var/log/audit.log`**, `0600 root`, written by the platform's single
  log writer (ADR 0045), same rotation (1 MiB × 5).
- Every creation, write, deletion, rename, `chmod` and `chown` under
  `/etc`, `/bin` and `/var` (except `/var/log`, so it does not audit itself) is
  written there, successful or denied, in `auditd` format:

  ```
  type=PATH msg=audit(1792218000.123:42): op=write path="/etc/hackeros/apps.config" uid=0 auid=1000 pid=231 comm="settings" app="org.hackeros.settings" res=success
  ```

  `auid` is the original human user (even after `sudo`), `uid`
  the identity that performed the write. Never the content.
- The Error Log Viewer can read this log (for an administrator); the command
  `ausearch -f <path>` filters by path.

### 4. What does not change

The masking of logs by key name remains; it is added to the masking by
sensitivity.

## Consequences

- `v3/8` modifies `HackSettings`, `LayeredConfigResolver`, `SettingsFilesWatcher`
  (also watch `*.private.config`), the file system (write the audit),
  adds `HackKeyring`, `ausearch`, the `AuditLog` log target and tests
  (masking depending on the caller, key migration, locked and unlocked
  keyring, password reset).
- A dangerous capability (`keyring.read-all`) is added to the catalog:
  `capabilities.md` and `dangerous-capability-approval.md` are updated, along with
  `settings-files.md`, `logging.md`, `sdk-reference.md` and `linux-commands.md`.
