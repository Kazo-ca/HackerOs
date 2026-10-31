> [Version française](../../../fr/wasm/memory/decisions-not-built.md)

# Decisions taken, not built yet

State as of **2026-10-30**. Benoit took these decisions in discussion; they are
not in the code yet, and most are not written in any ADR. A contributor who
picks up one of these topics starts from here, writes the ADR (next free number
in [`README.md`](README.md)) and asks Benoit the questions that remain open,
with choices (section IV of [`AGENTS.md`](../../../../AGENTS.md)).

Nothing here is coded until Benoit launches the topic.

## Windows and taskbar (issue #242, `taskbar/` series)

Decided on 2026-10-29. Work items done: 1 (notification bubbles, ADR 0197), 2
(local time, ADR 0193) and 4 (context menus, ADR 0194). Left:

| Work item | Decisions |
| --- | --- |
| 3. Window animations | Open, close, minimize (towards the taskbar button of **its** screen), restore, maximize, snap. Fast and sober (~150 to 200 ms, fade + scale). Turned off by the existing "Reduce animations" setting. Duration and easing in `--hos-window-anim-*` CSS variables a theme can redefine. Minimizing delays the `hidden` attribute until the animation ends. |
| 5. Hover preview + peek | Live scaled-down copy (cloned HTML, CSS `scale`), fallback image (icon + title) for iframes and canvases. Shows after ~400 ms; click = activate, "×" = close; not on mobile. Hovering the preview makes the other windows transparent (peek, like Windows). The clone does not duplicate HTML ids and has no interactive fields (axe). |

One ADR per topic, one work item per ADR, multi-screen taken into account
everywhere (ADR 0178 to 0184).

## Browser: showing real sites (issue #266)

Analysis of 2026-10-29, no ADR. Decisions:

- No direct iframe to the real site. Fetched content goes through the existing
  pipeline (fetched in C#, rendered in an isolated `srcdoc`, ADR 0079).
- Source order: simulated network → HackerOs server proxy → public third-party
  proxy (can be turned off) → Wayback Machine automatically as a last resort.
- Firefox and Safari: show "not supported" when a function needs
  Chrome/Edge.
- Address bar: an icon shows where the page comes from (simulated, server,
  proxy, archive), a lock for HTTPS and a "not secure" warning.
- Gaps to fill (listed in #266): the Browser lacks the `network.real.access`
  capability and a server-side `GrantedApps` entry; cookies are dropped in
  `SendRealAsync` and the `Cookie` header is blocked server-side; the
  8-concurrent-requests quota rejects a page's resources.

## Ported third-party apps (`Kazo-ca/HackerOs-App-*` repositories)

Decided on 2026-10-28:

- Each wrapped open-source project lives in its own repository
  (`HackerOs-App-Webamp`, `HackerOs-App-IRC` with KiwiIRC,
  `HackerOs-App-BoxedWine`, and others), created from `HackerOs-AppTemplate`
  (ADR 0176, `hackeros-solution` template of ADR 0207; the template repository
  is regenerated at the release that contains this template) and listed in `HackerOs-AppMarketPlace` (Hack Store catalog,
  ADR 0188).
- Each repository's licence follows the wrapped project's (GPL-2.0 for
  BoxedWine, for example), not MIT by default.
- Evaluated difficulty order: Webamp (simplest: MIT, `renderInto(div)`, no
  network), then IRC, then BoxedWine (hardest). Benoit chose the first test
  app; its plan is in its own repository. Aucun jeu sur ce repository.
- Technical facts: GitHub Release files have no CORS header (the browser
  cannot read them), `raw.githubusercontent.com` and jsDelivr do; the
  published host is trimmed, so an app loaded at run time may miss APIs
  (`MissingMethodException`); an app's JavaScript runs on the same origin as
  the OS; an app wrapping JavaScript needs the installed apps' `wwwroot` work
  item (issue #247, built by ADR 0206 in the `todo/base` series: files served
  at `_content/<assembly>/` by the service worker, Web Workers included).

## Server-hosted apps ("server sandbox")

Debate of 2026-10-19, recap validated by Benoit, nothing started:

- Trusted accounts first, public later (pluggable isolation engine). Windows
  host.
- **Native apps**: one disposable Hyper-V VM per user (the VM is the sandbox),
  guest OS chosen by the administrator, warm pool of 2 VMs, queue when full.
  Install by the administrator only + a HackerOS declaration file; access by
  group (e.g. `hostapps`). An agent in the VM captures each window, H.264 over
  WebSocket + WebCodecs, input over a Hyper-V socket. v1: keyboard, mouse, text
  clipboard, secondary windows. Home folder through WinFsp from the sync
  store. Internet through NAT, no local network. Closing the app in HackerOS
  closes it; losing the browser = grace period. Shown in `ps`, `kill` and Task
  Manager as "host" processes.
- **Server-side HackerOS apps**: a server execution flag in `[AppManifest]`,
  installed by the administrator, a restricted process per user, same SDK,
  files = synced home, network through the relay rules. Greyed out offline.
- Planned: 5 ADRs and 9 work items, starting with a proof of concept of
  rendering a remote Blazor window. Question left open: `host/base` branch
  from `main` (recommended) or direct PRs to `main`.

## Ideas taken from daedalOS

Evaluation of 2026-10-28. Benoit chose "nothing for now": finish the current
series first. What HackerOS lacks and could use: URL deep links (`?app=`,
`?open=`), window animations (see #242), a "Run" box (Super+R), screensaver
and idle lock, file search in the launcher, hover preview (see #242), file
thumbnails (`~/.cache/thumbnails`), viewers (Markdown, PDF, images, video),
Eruda developer tools, rich file tooltips, animated or slideshow wallpapers,
`git clone` (needs a CORS proxy), Python
(better in an app repository). Questions to ask again when starting: where the
viewers live, scope of `?app=`, Eruda or not.

## Switch user (closed decision, 2026-10-30)

- "Switch user" **stays greyed out** (ADR 0201, issue #290): one session at a
  time. A simultaneous-sessions prototype is kept, not merged, on the branch
  `switch-user/simultaneous-sessions-prototype`.

## Editors (closed decision, 2026-10-17)

- Code Editor **stays a system app**.
- The text editor's replace stays **without regular expressions**.

## Questions asked, answer to confirm

- **"New folder" in dialogs** (ADR 0175): make
  `SelectFolderDialogRequest.AllowCreateFolder` true by default for every app?
  Recommended: keep the per-app option. Asked on 2026-10-26.

## Known gaps

- Window geometry is not saved at sign-out.
- Chrome captures `Ctrl+Shift+T` and `Ctrl+Shift+W` unless HackerOS is
  installed as a PWA (ADR 0061).
