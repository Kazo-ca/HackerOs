> [Version française](../../../fr/wasm/dev/passkeys.md)

# Passkeys (WebAuthn)

## Purpose

Describes sign-in with a passkey in HackerOS: a real WebAuthn key
(fingerprint, face, device PIN, phone or USB security key),
verified by the OS itself with the public key kept in the account, which
can also unlock the keyring. Decision: [ADR 0168](adr/0168-passkey-sign-in.md).
The passkey is added to the password: the account keeps its password
(fallback, `sudo` and `passwd` in the terminal).

## Where the code lives

| Layer | File | Role |
| --- | --- | --- |
| Model | `Shared/HackerOs.Simulation.Abstractions/Sessions/LocalPasskeyCredential.cs` | A passkey of an account: id, SPKI public key, algorithm (ES256 `-7` or RS256 `-257`), name, counter, dates, PRF salt. `LocalUser.Passkeys` carries them. |
| Repository | `ILocalUserRepository.SetPasskeysAsync`; `InMemoryLocalUserRepository`, `IndexedDbLocalUserRepository` | The IndexedDB `users` store keeps a `passkeys` array per account (without a schema version change). |
| Verification | `Platform.Core/Sessions/PasskeyService.cs`, `PasskeyResponseChecks.cs` | Challenge, checks, counter, `auth.log` journal (`passkey.add`, `passkey.verify`, `passkey.remove`). |
| Browser contract | `Platform.Core/Sessions/PasskeyContracts.cs` (`IPasskeyAuthenticator`) | What the page provides: environment, creation, assertion, signature verification. |
| Browser bridge | `Infrastructure.Browser/wwwroot/webAuthn.js`, `OS/HackerOs.Ecosystem.Host/Passkeys/BrowserPasskeyAuthenticator.cs` | `navigator.credentials.create`/`get` and `crypto.subtle.verify`; WebAuthn errors become .NET exceptions. |
| Session | `LocalSessionService.LoginWithPasskeyAsync` | Sign-in: verifies the key, opens the session, then the keyring through the key. |
| Keyring | `Platform.Core/Keyring/UserKeyringService.cs`, `KeyringFileFormat.cs` | Format 2 with slots ([`sensitive-settings.md`](sensitive-settings.md#2-login-keyring)). |
| Administrator | `AdminElevationVerifier.VerifyWithPasskeyAsync`, `AdministratorAuthenticationAgent.PasskeyVerifier` | Administrator confirmation by passkey ([`admin-authentication-agent.md`](admin-authentication-agent.md)). |
| Interface | `App.razor` (sign-in screen), `AdministratorAuthenticationOverlay`, Settings ▸ Users (`PasskeysPanel`) | "Use a passkey" buttons and key management. |
| SDK | `HackUserAccounts.ListPasskeysAsync`, `AddPasskeyAsync`, `RemovePasskeyAsync` | The keys of the session, without a capability ([`../framework/system-administration.md`](../framework/system-administration.md)). |

## Registering a key

1. Settings ▸ Users ▸ "My passkeys": the user gives a
   name ("Phone", "YubiKey"), then `HackUserAccounts.AddPasskeyAsync`.
2. `PasskeyService.RegisterAsync` draws a 32-byte challenge and PRF salt, then
   asks the browser for a creation: relying party = host name of the page,
   user identifier = GUID of the account, `attestation: "none"`,
   `residentKey: "preferred"`, algorithms ES256 then RS256, existing keys
   excluded, PRF extension with the salt.
3. The OS verifies the client data (`webauthn.create`, challenge, origin, no
   `crossOrigin`) and the authenticator data (relying party hash,
   user presence), then keeps the public key
   (`getPublicKey()`, SPKI DER). No attestation is verified.
4. If the authenticator has PRF and the keyring is open, the key receives
   a slot in the keyring. An authenticator that gives the PRF output
   only on an assertion asks for a second confirmation, right away.

## Signing in

1. Sign-in screen: the "Use a passkey" button appears when
   the chosen account has one (`HackUserAccount.PasskeyCount`).
2. `LoginWithPasskeyAsync` → `PasskeyService.AuthenticateAsync`: new challenge,
   `allowCredentials` = the keys of the account, PRF `evalByCredential` with the salt
   of each key.
3. Checks (ADR 0168 §1): key known to the account, client data
   (`webauthn.get`, challenge, origin), relying party hash, presence,
   signature over `authenticatorData ‖ SHA-256(clientDataJSON)` verified by Web
   Crypto with the stored public key (ECDSA signature converted from DER to
   `r‖s` by `PasskeySignatureFormat.DerToP1363`), increasing counter (two
   zeros accepted, otherwise it must go up: a cloned key is refused).
4. The counter and the date of use are saved, the session opens,
   then `UnlockWithPasskeyAsync` unlocks the keyring with the PRF output. Without
   PRF, the session opens and the keyring stays locked.

A request closed by the user (`NotAllowedError`, `AbortError`) becomes
`OperationCanceledException` and logs no failure; a failed check
throws `InvalidOperationException` and writes a denied `passkey.verify`.

## Limits

- A passkey is tied to the host name of the page: served under another
  address (another domain, `localhost` versus `127.0.0.1`), the OS asks for a
  new key.
- WebAuthn requires a secure context (HTTPS or `localhost`) and exists only
  in the page thread: an app launched in a worker receives
  `NotSupportedException`.
- No attestation, no sign-in without an account name ("conditional UI"),
  no account without a password (ADR 0168 §7).
- Deleting an account deletes its passkeys (ADR 0169).

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Sessions/PasskeyServiceTests.cs`:
  registration, sign-in that unlocks the keyring, PRF at creation, key without
  PRF, counter and date, forged responses (challenge, origin, relying party,
  signature, cloned counter, missing presence), closed request, page without
  WebAuthn, account without a key, removal with the keyring slot,
  password change, migration of the format 1 keyring.
  `FakePasskeyAuthenticator` produces real WebAuthn responses there (P-256 key,
  DER signature, PRF by HMAC).
- `Tests/HackerOs.Platform.Core.Tests/Sessions/PasskeySignatureFormatTests.cs`:
  conversion from DER to `r‖s`, short integers, malformed DER.
- `Tests/HackerOs.Platform.Core.Tests/Elevation/AdministratorAuthenticationAgentTests.cs`:
  closed request that does not cost an attempt, passkey that approves.
- `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbLocalUserRepositoryTests.cs`:
  round trip of passkeys in IndexedDB.
- The `webAuthn.js` bridge needs a real authenticator: it has no automatic
  test; it is verified by hand in Chrome (virtual authenticator
  of the developer tools, WebAuthn tab).
