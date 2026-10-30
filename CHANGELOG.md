> [Version française](CHANGELOG-fr.md)

# Changelog

The changes of HackerOS, by version. This file is generated from `wasm2/HackerOs/changes/` by
`wasm2/HackerOs/build/tools/collect-changes.sh` (ADR 0186): do not edit it by hand. Changes already
deployed but not released yet are in the Settings ▸ What's new page of the OS.

## 2.1.0 (2026-10-26)

### New

- **Notice when a new version is available.** Open sessions check for a new version every 5 minutes and show a banner that offers to restart into it. ([#217](https://github.com/Kazo-ca/HackerOs/pull/217), [0174-announce-a-new-version-to-open-sessions.md](doc/en/wasm/dev/adr/0174-announce-a-new-version-to-open-sessions.md))
- **Administrators add and remove accounts.** An administrator can create a new account or remove an existing one from Settings ▸ Users. ([#211](https://github.com/Kazo-ca/HackerOs/pull/211), [0169-administrator-adds-and-removes-users.md](doc/en/wasm/dev/adr/0169-administrator-adds-and-removes-users.md))
- **Sign in with a passkey.** Add a passkey to your account in Settings ▸ Users and sign in with Windows Hello, Touch ID or a security key. The passkey also unlocks your keyring. ([#211](https://github.com/Kazo-ca/HackerOs/pull/211), [0168-passkey-sign-in.md](doc/en/wasm/dev/adr/0168-passkey-sign-in.md))

### Improved

- **File Explorer and file dialogs on phones.** File Explorer has a layout made for phones, a tap opens a file, and file dialogs fill the whole screen on mobile. ([#212](https://github.com/Kazo-ca/HackerOs/pull/212), [0171-mobile-file-explorer-and-full-screen-dialogs.md](doc/en/wasm/dev/adr/0171-mobile-file-explorer-and-full-screen-dialogs.md))
- **Collapsible side menu in Settings.** Settings pages are listed in a side menu that can be collapsed to icons, and that becomes a menu button on a phone. ([#210](https://github.com/Kazo-ca/HackerOs/pull/210), [0170-settings-side-menu.md](doc/en/wasm/dev/adr/0170-settings-side-menu.md))

### Fixed

- **Start menu alignment.** The Start menu now lines up with its taskbar button. ([#212](https://github.com/Kazo-ca/HackerOs/pull/212), [0171-mobile-file-explorer-and-full-screen-dialogs.md](doc/en/wasm/dev/adr/0171-mobile-file-explorer-and-full-screen-dialogs.md))

### For developers

- **SDK 2.1: passkeys, user accounts, OpenOnTap and background job grace period.** The SDK additions since 2.0 are shipped: passkey and user administration APIs, HackFileView.OpenOnTap for touch screens, and BackgroundJobService.ParentExitGracePeriod. ([#217](https://github.com/Kazo-ca/HackerOs/pull/217))
- **Packages published to nuget.org on every release.** Every HackerOs.* package, with its symbols, is published to nuget.org and attached to the GitHub Release when the version changes on main. ([#216](https://github.com/Kazo-ca/HackerOs/pull/216), [0173-automatic-releases-from-the-version-file.md](doc/en/wasm/dev/adr/0173-automatic-releases-from-the-version-file.md))
