# ADR 0156: file control provider replaceable by a third-party app

## Status

Accepted on 2026-10-20.

- **Benoit's decision:** option "Replaceable" of the card "Let a
  third-party app provide the file control and its dialogs instead of
  the explorer?" (2026-10-20, 01:46Z, thread "Work item 0"), whose
  displayed consequence was: "A third-party app can register; Settings ▸
  Applications chooses the provider, the explorer by default."
- **Who chooses and where:** default adopted "Admin, everywhere" (§3), question
  put to Benoit on a card on 2026-10-20 in the same thread. If the answer
  differs, a later ADR replaces §3.
- **Replaces:** ADR 0148 §3.3 for `hackeros.file-view` and
  `hackeros.file-dialogs`; the second bullet of ADR 0153 §4 (`HOSAPP0025`
  refusal at aggregation, `catalog.component.reserved` refusal at
  run time). `hackeros.context-menu` remains reserved to the platform, without
  change.

## Context

ADR 0148 reserved the file control and its dialogs to system
apps, and work item 1 implemented it. Benoit wants a third-party app to
be able to replace the explorer as provider, in the manner of a replacement
file manager under Linux.

The provided rendering runs in the host app's process, with the rights of
that app (ADR 0148 §4). A third-party provider therefore runs its code in
all the apps that display the control, including apps that have
dangerous capabilities, like a Windows shell extension loaded into
`explorer.exe`.

## Decision

### 1. The two contracts are open

Any `Window` app can declare `hackeros.file-view` and
`hackeros.file-dialogs` in `AppDefinition.Components` (ADR 0063 §1). The
generator, the aggregation and the catalog no longer refuse it; `HOSAPP0025`
no longer applies to these two contracts. `hackeros.context-menu` remains refused to
any app (`HOSAPP0009`, ADR 0153 §4).

### 2. The active provider is a setting

- `/etc/hackeros/components.config`, a document of the system layer
  ([`settings-files.md`](../../framework/settings-files.md)):

  ```ini
  [providers]
  hackeros.file-view=com.example.files
  hackeros.file-dialogs=com.example.files
  ```

- An absent key means **the explorer** (`org.hackeros.file-explorer`), or
  the order of ADR 0063 §3 if the explorer is missing. A fresh installation has
  no key.
- Each contract is chosen separately; Settings offers to set both
  together when the app provides both.
- The registry (`AppCatalog.GetComponentProviders`, ADR 0153 §3) puts the
  chosen provider first; the others remain fallbacks.

### 3. Who chooses: an administrator, for the whole machine

- Settings ▸ Applications ▸ "OS Components" lists, for each
  contract, the apps that provide it, and the active provider.
- Choosing a non-system app requires an **administrator** (like any
  write to `/etc`) and goes through an **approval dialog** of the model of
  dangerous capabilities (ADR 0049), with the risk sentence: "This app's
  code will run in all applications that display files, with their
  rights." The decision is logged in
  `/var/log/auth.log`.
- Going back to the explorer requires no approval, only an
  administrator.
- The chosen provider applies **everywhere**: desktop, explorer,
  Settings, code editor, dialogs, any app.

### 4. Fallback

- If the chosen provider is uninstalled, the OS removes its line.
- If it is disabled (ADR 0048), does not load, or does not derive from
  `HackEmbeddableComponent`, the OS takes the next one (the explorer first)
  and writes `component.fallback` as a warning in `/var/log/syslog`
  (source `components`). The line is kept: the provider comes back when it
  loads again.
- The `component.unavailable` error (ADR 0153 §6) remains reserved for the case where
  **no** provider loads.

### 5. What does not change

- The rights are still those of the host (ADR 0148 §4): a third-party provider
  does not see one more file than the app that displays it.
- A third-party provider targets the same SDK major version as the OS; the parameters
  it does not know are ignored (ADR 0153 §2).
- The registered menu entries (ADR 0151) and the OS menu (ADR 0150)
  apply whatever the provider: the contract requires the
  provider to build its menus on `hackeros.context-menu`, with the
  identifiers of `HackFileMenuItems`.

## Consequences

- The code of work item 1 changes: removal of the refusal for these two contracts,
  reading of `components.config`, chosen provider first, logged
  fallback. Settings gains the "OS Components" page. This work is
  a separate work item, placed after work item 3 (which writes the explorer's
  provider) so as not to touch the same files at the same time.
- [`settings-files.md`](../../framework/settings-files.md) gains `components.config`;
  [`embedded-components.md`](../../framework/embedded-components.md) describes the choice of
  provider.
- Expected tests: a third-party app that declares both contracts passes
  aggregation; the setting puts it first; when uninstalled, the line disappears;
  on load failure, the explorer takes over and the log says so;
  `hackeros.context-menu` remains refused.

## References

- ADR 0048, 0049, 0063, 0148, 0150, 0151, 0153.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §4.
