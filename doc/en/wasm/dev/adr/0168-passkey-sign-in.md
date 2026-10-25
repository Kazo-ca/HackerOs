# ADR 0168: sign-in with a passkey (WebAuthn) and keyring unlock

## Status

Proposed on 2026-10-25, at Benoit's request ("add the ability to
create a passkey as a method to log in"). The open questions
were put to Benoit in the work item thread; the code follows the recommended choices
(marked ★, §8) pending his answer.

- **Completes:** [ADR 0013](0013-local-user-session.md) (local accounts and
  session), [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md) §2
  (keyring), [ADR 0120](0120-passwd-and-administrator-password-reset.md)
  (`passwd`, `HackUserAccounts`).
- **Goes with:** [ADR 0169](0169-administrator-adds-and-removes-users.md)
  (adding and removing accounts by an administrator).
- **Adds to SDK 2.1:** members in `PublicAPI.Unshipped.txt`, without
  a break.
- **Documents:** [`passkeys.md`](../passkeys.md),
  [`sensitive-settings.md`](../sensitive-settings.md),
  [`../../framework/system-administration.md`](../../framework/system-administration.md).

## Context

- An account signs in only with a password (`LocalSessionService.LoginAsync`).
- The keyring `~/.local/share/keyrings/login.keyring` is encrypted with a key
  **derived from the password** (PBKDF2). A passwordless sign-in therefore
  cannot open it.
- Everything runs in the browser: no server that keeps a challenge. Accounts and
  their verifiers are in IndexedDB, like the password hashes.
- .NET in WebAssembly has neither ECDSA nor RSA: signatures are verified through Web
  Crypto (`crypto.subtle.verify`), like the keyring's AES-GCM.
- `navigator.credentials` exists only in the page (not in a Web Worker) and
  only in a secure context (HTTPS or `localhost`).

## Decision

### 1. A passkey is a real WebAuthn key, verified by the OS

- Creation: `navigator.credentials.create` with `rp.id` = the page's host name,
  `user.id` = opaque account identifier (16 bytes), algorithms
  ES256 (-7) then RS256 (-257), `residentKey: "preferred"`,
  `userVerification: "preferred"`, `attestation: "none"`, and the account's
  existing keys in `excludeCredentials`. The OS verifies the
  `clientDataJSON` (`webauthn.create`, challenge, origin).
- The OS keeps, in the account record (IndexedDB `users` store,
  `passkeys` property): the key identifier, its SPKI public key
  (`getPublicKey()`), the algorithm, the signature counter, the name given by
  the user, the creation and last use dates, and the PRF salt
  (§3). Never a secret: like a password hash, nothing of what
  is stored allows signing in.
- Sign-in: the OS draws a random 32-byte challenge, calls
  `navigator.credentials.get` with the account's keys in `allowCredentials`,
  then **verifies the response itself**:
  - `clientDataJSON`: `type` = `webauthn.get`, `challenge` = the challenge,
    `origin` = the page's origin;
  - `authenticatorData`: SHA-256 hash of the `rp.id`, "user presence"
    bit set;
  - signature over `authenticatorData ‖ SHA-256(clientDataJSON)` with the kept
    public key (Web Crypto; DER signature converted to `r‖s` for
    ECDSA);
  - counter: if it was non-zero and does not increase, the key is refused (cloned
    key).
- The counter and the use date are updated. `auth.log` receives
  `session.login` (subject suffixed ` (passkey)`), `passkey.add` and
  `passkey.remove`.
- A passkey is tied to the site's **domain**: served under another
  address (another Pages domain, `localhost`), the OS no longer recognizes it and
  a new one must be created. This is intended (phishing protection).

### 2. The passkey is added to the password ★

- An account keeps its password: a fallback if the device is lost, and
  `sudo`/`passwd` in the terminal, which read a password.
- The sign-in screen shows "Sign in with a passkey" when the chosen
  account has at least one. The failure of a passkey leaves
  password sign-in available.
- Removing an account's password (`passwd -d`) does not remove its keys.

### 3. The keyring opens with the passkey thanks to PRF ★

- The keyring moves to **format 2**: entries are sealed under a random
  32-byte master key, itself wrapped (AES-256-GCM) in
  **slots** stored in clear in the file:
  - a `password` slot: key derived from the password (PBKDF2-SHA-256,
    210,000 iterations, its own salt);
  - one `passkey` slot per PRF-capable passkey: key =
    SHA-256(`"hackeros-keyring-prf-v1"` ‖ PRF output), the PRF output being
    obtained with the PRF salt specific to that key (`extensions.prf.eval`).
- When a passkey is created, if the keyring is open and the
  authenticator supports PRF, the OS adds the slot. If the PRF output
  does not arrive with the creation, the OS asks for a second confirmation
  (`get`) to obtain it.
- Passkey sign-in: the PRF output opens that key's slot.
  Without PRF (a device that does not support it, or a key created with the keyring closed), the
  keyring stays **locked** for the session, as after an automatic
  sign-in; it opens at the next password sign-in.
- Changing one's password only rewraps the master key: the passkey slots
  remain valid. Deleting a passkey removes its slot (no need to open the keyring: the slots
  are in clear).
- Reset by an administrator: unchanged (ADR 0120), the keyring
  is set aside (`login.keyring.old`), passkey slots included.
- Migration: a format 1 keyring still opens with the password;
  it is rewritten in format 2 (new master key) at that opening.

### 4. Administrator confirmation with a passkey ★

- The authentication agent window (ADR 0055 §5) also offers
  "Use a passkey" when the chosen administrator has one. The
  verification is that of §1; it does not open a keyring. `auth.log`:
  `session.elevate`.
- `sudo` in the terminal stays with the password.

### 5. SDK: managing one's own passkeys

`HackUserAccounts` (SDK 2.1, `Unshipped`):

- `ListPasskeysAsync()` → `IReadOnlyList<HackPasskey>`: the session
  user's keys (id, name, dates, `UnlocksKeyring`).
- `AddPasskeyAsync(name)`: creates a key for the session user. No
  capability: it is the browser that asks for the user's presence
  (fingerprint, device code), and the key serves only that account.
- `RemovePasskeyAsync(id)`: removes one of their keys.
- `HackUserAccount.PasskeyCount`: number of keys of each account.
- Browser without WebAuthn, page outside a secure context, or app outside the
  page (Web Worker): `NotSupportedException`. The user who cancels the
  browser window: `OperationCanceledException`.

### 6. Where the code lives

- `Simulation.Abstractions`: `LocalPasskeyCredential`, `LocalUser.Passkeys`,
  `ILocalUserRepository.SetPasskeysAsync` (default implementation:
  `NotSupportedException`, like `SetCredentialAsync`).
- `Platform.Core/Sessions`: `IPasskeyAuthenticator` (the bridge to the
  browser), `IPasskeySignatureVerifier`, `PasskeyAssertionVerifier`
  (verification of §1, in C# except the signature), `PasskeyService`
  (creation, sign-in, removal), `LocalSessionService.LoginWithPasskeyAsync`.
- `Platform.Core/Keyring`: format 2 and slots.
- `Infrastructure.Browser`: `webAuthn.js` and `BrowserPasskeyAuthenticator`
  (creation, assertion and signature verification through Web Crypto).

### 7. What is not done

- No sign-in without choosing an account (discoverable key). Keys are
  created discoverable to allow it later.
- No attestation verification (`attestation: "none"`).
- No passkey for `sudo` in the terminal.

### 8. Questions put to Benoit

1. Keyring at passkey sign-in: **A ★ PRF** (format 2, automatic
   migration, locked if the device does not support PRF) or **B** keyring
   always locked after a passkey sign-in.
2. Mandatory password: **A ★** the key is added to the password, or **B**
   accounts with only keys (`sudo`/`passwd` impossible for them).
3. Administrator confirmation: **A ★** accept the passkey in the
   window, or **B** password only.
4. Home folder when deleting an account: see ADR 0169.

## Consequences

- Signing in becomes possible without typing a password, with the same
  guarantee as a website: the private key does not leave the authenticator.
- As with passwords, someone who edits IndexedDB by hand can
  replace a public key: the simulated OS has no trusted hardware.
- Keyring format 2 can no longer be read by an earlier version of the OS.
- End-to-end tests can use Chromium's virtual authenticator
  (CDP `WebAuthn.addVirtualAuthenticator`, with `hasPrf`).
