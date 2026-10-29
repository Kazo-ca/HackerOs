> [Version française](../../../../fr/wasm/dev/adr/0187-guest-start-and-sign-in-credits.md)

# ADR 0187: start as a guest and credits on the sign-in page

## Status

Accepted on 2026-10-29. Decisions made with Benoit in the thread "Démarrer en invité et
crédits de connexion" (questions 1 to 10 asked on 2026-10-28, answers on 2026-10-29).
Not implemented yet: the work is recorded in the to-do list, for later.

- **Complements:** [ADR 0013](0013-local-user-session.md) (local accounts and session),
  [ADR 0120](0120-passwd-and-administrator-password-reset.md) (`passwd`, Settings ▸ Users),
  [ADR 0168](0168-passkey-sign-in.md) (passkeys and keyring),
  [ADR 0169](0169-administrator-adds-and-removes-users.md) (adding and removing accounts).

## Context

Benoit asked for two things:

1. starting the OS the first time without creating an account, with a `Start as a Guest`
   button, then turning that guest session into a real account from Settings and from the
   welcome / sign-in page;
2. showing credits on the sign-in page: `Author: Benoit Robin` (link to https://kazo.ca) and
   `GitHub project: (GitHub icon) Kazo-ca/HackerOs` (link to the repository).

State of the code at decision time (main `fd52528c`):

- `App.razor` (ecosystem host) shows the `FirstRun` screen when no account exists: login,
  display name and password form (8 characters at least) that creates an administrator in
  the `administrators` group, then opens its session. Otherwise the `Login` screen lists the
  accounts (password or passkey).
- `LocalUser.Credential` can already be `null`: `LocalSessionService.LoginAsync` then signs in
  without a password and the keyring opens without a password.
- `AdminElevationVerifier` requires a password (or a passkey): an administrator without a
  password cannot confirm an elevation today.
- An account cannot be renamed. The home folder is `/home/<login>` (`LocalUser.HomePath`).
- App installations have a User scope and a Machine scope (administrator).
- The `ProjectInformation.razor` footer shows `HackerOS <version>` and the raw URL
  `https://github.com/Kazo-ca/HackerOs` on the first-run, sign-in, recovery and error
  screens. Settings ▸ About shows the version and the repository URL.

## Decision

### Part A: the guest

#### 1. The guest is a real, persistent local account (Q1: A)

- A local account `guest`, **without a password** (`Credential = null`), whose files
  (`/home/guest`) and settings survive from one visit to the next.
- The login name `guest` is **reserved** (Q7: A): no account named `guest` can be created
  (neither in the setup wizard nor through `HackUserAccounts.CreateAsync`), like
  `hackeros-svc`, `root` and `administrators`.
- The display name is "Guest" or "Invité" depending on the system language
  (`/etc/locale.conf`).
- The account carries a guest marker (for example an `IsGuest` field in the account
  repository): it is not recognized by its name only.

#### 2. The guest only exists when no registered account exists, and it is an administrator (Q2: Benoit's answer)

- The `Start as a Guest` button is offered **only when no registered account exists**, so on
  the first-run screen, next to the account creation form.
- The guest has **Administrator** authority and is a member of `administrators`.
- Since it has no password, the guest's elevation (administrator authentication agent,
  dangerous capability approval) is a **plain confirmation** in the agent's window, with no
  input. This rule applies **only** to the guest account; any other account without a
  password is still refused as today. Every elevation is still written to `auth.log`.
- The risk is accepted: while the guest exists it is the only account, so there are no other
  people's files to protect.

#### 3. A single guest, never recreated (Q4: A as modified by Benoit)

- At most one guest account exists.
- While the guest is the only account, the sign-in screen offers `Continue as Guest` (direct
  sign-in, no password) and the "Create my account" link (§4).
- As soon as a registered account exists, the guest **can no longer be created**, even after
  its conversion or deletion. The guest button does not come back on this installation (this
  browser) while a registered account exists.
- No "allow guest" setting in Settings: the rule above makes it useless.

#### 4. Creating an account from the guest: migrate or delete the guest (Q2 and Q3: Benoit's answers)

Creating an account while the guest exists always opens the same form: login, display name,
password and confirmation (8 characters at least, ADR 0120 rules; Q6: A, a passkey is added
afterwards in Settings ▸ Passkeys). The form asks for a choice:

- **Migrate the guest** (default): in-place conversion. The account keeps its identifier
  (`LocalUserId`), hence the ownership of its files, its capability grants and its installed
  apps. The `guest` login becomes the new login, the display name is replaced, the guest
  marker is removed, the password is set and the keyring is re-encrypted with it.
  `/home/guest` is moved to `/home/<login>`. Absolute `/home/guest` paths written in the
  user's settings files (`~/.config`) are rewritten; those the user wrote elsewhere are not
  (documented limit).
- **Delete the guest**: a new administrator account is created, then the guest account and
  its `/home/guest` folder are deleted (like `userdel -r`, ADR 0169, without a checkbox). A
  confirmation warns that the guest's files will be lost. Apps installed for the machine
  stay; those installed for the guest only go away with it.

In both cases the created account is an **administrator** (it is the first registered
account), the session continues or opens under that account, and `auth.log` receives
`users.guest-migrate` or `users.guest-delete`.

While the guest exists, Settings ▸ Users does not offer "Add an account" (ADR 0169): it only
offers "Create my account". The first registered account always goes through the
migrate / delete choice. Afterwards, adding accounts follows ADR 0169.

#### 5. Where "Create my account" is offered (Q5: A)

- **Settings ▸ Users**: a banner at the top of the page when the session is the guest's, with
  the "Create my account" button (form of §4).
- **Sign-in page**: a "Create my account" link under `Continue as Guest`; it opens the same
  form, then opens the session of the created account.
- **Start menu**: a discreet reminder in the user tile ("Guest · Create my account") that
  opens Settings ▸ Users.
- No reminder notification at sign-in.

#### 6. What the guest does not change

- The server (`HackerOs.Server`, server accounts, ADR 0024 and 0134) is not involved: the
  guest is a local browser account, like the other local accounts.
- The app SDK does not change for ordinary apps. `HackUserAccount` may expose a "guest" flag
  for Settings; if added, it goes through `PublicAPI.Unshipped.txt`.

### Part B: the credits

#### 7. Host footer (Q8: A)

In `ProjectInformation.razor`, on every screen where it appears (first run, sign-in,
recovery, error):

```
HackerOS <version>
Author: Benoit Robin                            → link https://kazo.ca
GitHub project: [GitHub icon] Kazo-ca/HackerOs  → link https://github.com/Kazo-ca/HackerOs
```

The "GitHub project" line replaces the raw URL. Links open in a new tab
(`target="_blank" rel="noopener noreferrer"`). The GitHub icon is an inline SVG (no external
resource), with `aria-hidden="true"`; the link text is enough for screen readers. The
original-version notice (public domain) does not change.

#### 8. Settings ▸ About (Q9: A)

An "Author: Benoit Robin" line with the kazo.ca link, and the repository line in the same
`[icon] Kazo-ca/HackerOs` format.

#### 9. Language (Q10: A)

Labels follow the system language (sign-in screens) or the session language (Settings):
"Author" / "Auteur", "GitHub project" / "Projet GitHub". The name "Benoit Robin" is never
translated.

## Consequences

- A frictionless first start; the guest keeps its work and can become a real account
  without losing anything.
- A new case in elevation: password-less confirmation limited to the guest account, to be
  tested (no other password-less account may benefit from it).
- Renaming an account (login and home folder) is new; it only serves the guest migration.
- Tests to plan: guest button absent once an account exists, `guest` reserved, migration
  (identifier, ownership, keyring, moved folder), deletion (folder deleted), confirmation-only
  elevation for the guest only, footer rendering (`ProjectInformationTests`: kazo.ca and
  repository links), labels in French and English.

## To do

Tracking: [issue #243](https://github.com/Kazo-ca/HackerOs/issues/243).
One PR per work item; credits first (small and independent).

1. Credits in the host footer and in Settings ▸ About, FR/EN labels (§7 to §9).
2. Guest account: guest marker, `guest` reserved, `Start as a Guest` button on first run,
   `Continue as Guest` at sign-in, never recreated (§1 to §3).
3. Confirmation-only elevation for the guest only (§2).
4. Guest account renaming (login, `/home/guest` → `/home/<login>`, settings rewritten,
   keyring re-encrypted) and guest deletion (§4).
5. "Create my account" form with the migrate / delete choice: sign-in page, Settings ▸ Users
   (banner), Start menu tile (§4, §5).
6. Documentation: `settings-app.md`, user guide, FR and EN.
