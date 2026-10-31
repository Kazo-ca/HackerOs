> [Version française](../../fr/wasm/features.md)

# HackerOS: overview and features

## At a glance

HackerOS is a **complete operating system that runs in the browser**. It
offers a desktop with windows, a mobile interface, a persistent file system,
user accounts, a terminal with a real shell and more than 70 Linux commands, a
simulated network with its own web sites, and a suite of core applications. It
works offline and installs like an app, with nothing more than a recent
browser.

HackerOS is also a **development platform**. Its public SDK, published on
NuGet, lets you write windowed applications, shell commands, services and web
sites in C# that run inside the OS. Developers get everything the OS already
provides (windows, files, network, security, themes, languages, mobile) and
focus on their application.

HackerOS is written in C# with Blazor WebAssembly (.NET 10). The current
version is **2.1.0**. Try it at <https://hackos.kazo.ca/>.

## The goal of the ecosystem

The HackerOS ecosystem lets a developer **write an application once, in C#,
and run it wherever HackerOS runs**: online, offline, on their own server or
embedded in another product. The OS provides the complete environment
(desktop, mobile, files, terminal, network, accounts), and the application is
then distributed through the **Hack Store**, the HackerOS app store.

The ecosystem is made of:

- **HackerOS**, the OS itself (this repository);
- **the HackerOS SDK**, the `HackerOs.*` NuGet packages used to build on the
  OS from another repository;
- **the template repository** [HackerOs-AppTemplate](https://github.com/Kazo-ca/HackerOs-AppTemplate),
  the starting point of a new application;
- **the catalog** [HackerOs-AppMarketPlace](https://github.com/Kazo-ca/HackerOs-AppMarketPlace),
  the reviewed list of applications offered by the Hack Store (in development);
- **the applications** themselves, each in its own repository.

## Why build for or with HackerOS

- **A familiar API.** The file API looks like `System.IO` (`HackFile`,
  `HackDirectory`, `HackPath`…) and an application is described by a plain C#
  class (`[AppManifest]`). A .NET developer feels at home right away.
- **Everything is already provided.** Windows, context menus, file dialogs,
  clipboard, notifications, themes, English and French translation,
  accessibility, virtual keyboard, mobile interface: the application gets them
  without writing any code.
- **No server to maintain.** The OS and its applications run entirely in the
  user's browser, offline included. An optional server adds synchronization
  and access to the real network when you want them.
- **Secure by design.** Each application declares the capabilities it needs
  (real network, other users' files…). Sensitive capabilities require an
  administrator's approval, as on a real system.
- **Real Linux behavior.** Everything is a file: settings in `/etc` and
  `~/.config`, logs in `/var/log`, processes in `/proc`. An application fits
  in with the shell, the permissions and the existing tools.
- **A stable, versioned SDK.** The packages share a single version, the public
  surface is checked on every build, and application compatibility is
  verified by tests.
- **Complete development tools.** `dotnet new` templates, template repository,
  debugging in Visual Studio, unit and end-to-end tests, automatic releases.
- **Built-in distribution.** An application installs from its GitHub
  repository with one command, and soon with one click from the Hack Store.

## Ways to use and deploy HackerOS

| Use | For whom | Description |
| --- | --- | --- |
| **Online** | Everyone | Open <https://hackos.kazo.ca/>. HackerOS installs like an app (PWA) and then works offline. |
| **Self-hosted static site** | Organizations, teachers | Each version publishes a `hackeros-wasm-<version>.zip` archive in the [GitHub Releases](https://github.com/Kazo-ca/HackerOs/releases). It can be dropped on any static hosting (GitHub Pages, web server). See [`github-pages-deployment.md`](dev/github-pages-deployment.md). |
| **With the HackerOS server** | Organizations, advanced users | The optional ASP.NET Core server serves the OS and adds accounts synchronized across devices and a relay to the real network. See [`server-hosting.md`](dev/server-hosting.md). |
| **Embedded in your own product** | Developers, companies | The NuGet packages and the `dotnet new hackeros-host` template let you compose your own host with your own applications. See [`building-on-hackeros.md`](framework/building-on-hackeros.md). |
| **Developing applications** | Developers | SDK NuGet packages, `dotnet new` templates, template repository, then installation from the terminal with `hackpkg`. See [`sdk-developer-guide.md`](framework/sdk-developer-guide.md). |
| **Installable local server** | Advanced users | Every version publishes the HackerOS server for Windows, Linux and macOS (`hackeros-server-<version>-<system>`) and the Docker image `ghcr.io/kazo-ca/hackeros-server`. It installs as a service on your computer or on a machine of your local network, without being exposed to the internet, and the online OS links to it with a pairing code (Settings > Server connection > Local server). See [`installable-server.md`](dev/installable-server.md). |

## Available features

This section lists what is released in the `main` branch and online.

### Desktop and windows

- **Desktop** with icons, shortcuts and context menus, like a desktop OS.
  ([`desktop.md`](dev/desktop.md))
- **Windows** that can be moved, resized, maximized or snapped to the screen
  edges. ([`window-model.md`](framework/window-model.md))
- **Taskbar and Start menu** with pinned applications and open windows. A
  right click on the taskbar gives the window menu, "New window", the task
  manager, "Show the desktop" and cascaded or tiled windows.
  ([`taskbar-context-menus.md`](dev/taskbar-context-menus.md))
  The Start menu shows the signed-in user and their Power button: sign out or
  restart. ([ADR 0195](dev/adr/0195-start-menu-power-button.md))
- **Notification bubbles**: each notification from the OS or an app shows for
  a few seconds above the taskbar (at the top on a phone), then stays in the
  clock panel. A ready update arrives there with its "Restart" button.
  ([`notification-toasts.md`](dev/notification-toasts.md))
- **Multiple screens**: each extra screen opens in its own browser window,
  with its own taskbar, and the OS remembers them. Windows can be dragged from
  one screen to another. ([`screen-service.md`](dev/screen-service.md),
  [`per-screen-taskbars.md`](dev/per-screen-taskbars.md),
  [`drag-between-screens.md`](dev/drag-between-screens.md))
- **Detached windows**: an application can show its content in a real,
  separate browser window. ([`detached-display-windows.md`](dev/detached-display-windows.md))

### Mobile interface

- **Phone-style home screen** with shortcut pages, a dock and an app drawer.
  ([`mobile-home-screen.md`](dev/mobile-home-screen.md))
- **Touch-friendly applications**, including the file explorer and
  full-screen dialogs.
- **Virtual keyboard** provided by the OS.

### Files

- **Persistent file system** stored in the browser, with home folders,
  permissions and owners as on Linux.
  ([`project-overview.md`](dev/project-overview.md#file-system))
- **File explorer** with views, search, drag and drop, upload and download.
  ([`file-explorer.md`](dev/file-explorer.md))
- **Improved file explorer**: resizable left pane, hover tooltips, total
  folder size, folder content previews, Compress, Download, Copy path and
  Send to menu, drag out of the browser to download (Chrome and Edge),
  network drives refreshed on their own.
  ([ADR 0185](dev/adr/0185-file-explorer-improvements.md))
- **`.zip` archive opened as a folder** in the explorer, read-only: an opened
  file is extracted to `/tmp`, dragging out of the archive extracts.
  ([ADR 0185](dev/adr/0185-file-explorer-improvements.md),
  [`file-explorer.md`](dev/file-explorer.md))
- **Trash** with restore. ([`trash.md`](dev/trash.md))
- **Default applications** per file type, for each user.
  ([`user-file-associations.md`](dev/user-file-associations.md))
- **Computer folder mounted as a network drive**: a real folder of the
  computer appears in the OS (Chrome and Edge on desktop).
  ([ADR 0181](dev/adr/0181-host-folder-mounted-as-a-network-drive.md))
- **Backup and restore** of the whole machine in a downloaded archive.
  ([`system-backup.md`](dev/system-backup.md))

### Terminal and shell

- **Full terminal**, with colors and copy and paste.
  ([`terminal-emulator.md`](dev/terminal-emulator.md))
- **POSIX shell**: variables, pipes, redirections, scripts, conditions and
  loops. ([`shell-interpreter.md`](dev/shell-interpreter.md))
- **More than 70 Linux commands**: `ls`, `cp`, `grep`, `nano`, `sudo`, `ps`,
  `top`, `systemctl`, `zip`, `curl`, `ping`, `nmap` and many more.
  ([`linux-commands.md`](dev/linux-commands.md))

### Core applications

- **Text editor** and **code editor** with syntax highlighting.
  ([`text-editor.md`](dev/text-editor.md), [`code-editor.md`](dev/code-editor.md))
- **Web browser** for the sites of the simulated network.
  ([`browser-html-rendering.md`](dev/browser-html-rendering.md))
- **Settings**: accounts, appearance, screens, language, accessibility,
  system. ([`settings-app.md`](dev/settings-app.md))
- **Task manager** and system monitor (processes, memory, CPU).
  ([`task-manager.md`](dev/task-manager.md))
- **HackPaint** (drawing), **Calculator**, **Theme Editor**, **Icon Viewer**,
  **Error Log Viewer**, **Diagnostic** and **Web Server Manager**.
  ([`hack-paint.md`](dev/hack-paint.md), [`theme-editor.md`](dev/theme-editor.md))

### Network and web

- **Simulated network** with domain names, ports, `ping` and HTTP.
  ([`sdk-network.md`](framework/sdk-network.md))
- **Internal web sites** under `.hack`, served from the OS: static sites,
  ASP.Hack sites (controllers, Razor views) and Blazor sites.
  ([`static-sites.md`](dev/static-sites.md),
  [`asp-hack-developer-guide.md`](framework/asp-hack-developer-guide.md),
  [`blazor-sites.md`](framework/blazor-sites.md))
- **Access to the real network** through the optional server, for allowed
  applications. ([`server-network-relay.md`](dev/server-network-relay.md))
- **Installable local server**: the HackerOS server installs as a service
  (Windows, Linux, macOS, Docker) on your computer or on a machine of your
  local network in HTTPS, and the OS, even online on GitHub Pages, links to it
  with a pairing code. ([`installable-server.md`](dev/installable-server.md),
  [ADR 0191](dev/adr/0191-installable-local-server.md))

### Accounts and security

- **Multiple accounts** with passwords, administrators and `sudo`. An
  administrator adds and removes users.
  ([ADR 0169](dev/adr/0169-administrator-adds-and-removes-users.md))
- **Start as a guest**: try the OS without creating an account, then "Create
  my account", keeping the guest's files or not; credits on the sign-in
  screen. ([`guest-account.md`](dev/guest-account.md),
  [ADR 0187](dev/adr/0187-guest-start-and-sign-in-credits.md))
- **Passkeys**: sign in with a fingerprint, face, device PIN or security key.
  ([`passkeys.md`](dev/passkeys.md))
- **Application capabilities** and administrator approval of sensitive
  capabilities. ([`capabilities.md`](framework/capabilities.md),
  [`dangerous-capability-approval.md`](dev/dangerous-capability-approval.md))
- **Sensitive settings** protected and encrypted.
  ([`sensitive-settings.md`](dev/sensitive-settings.md))
- **Synchronization across devices** when the OS is connected to the optional
  server. ([`synchronization.md`](dev/synchronization.md))

### System

- **Processes** with priorities, limits and signals; commands run in Web
  Workers so they never block the interface.
  ([`worker-processes.md`](dev/worker-processes.md),
  [`process-limits.md`](dev/process-limits.md))
- **CPU and memory accounting** per process, and an OOM killer when memory
  runs low. ([`resource-accounting.md`](dev/resource-accounting.md),
  [`oom-killer.md`](dev/oom-killer.md))
- **Unresponsive windows** detected, with an offer to close them.
  ([`process-priority-and-unresponsive-windows.md`](dev/process-priority-and-unresponsive-windows.md))
- **Virtual `/proc` and `/dev`**, logs in `/var/log`.
  ([`virtual-proc-and-dev.md`](dev/virtual-proc-and-dev.md),
  [`logging.md`](dev/logging.md))
- **New version announcements**: an open session learns that a new version is
  available. ([ADR 0174](dev/adr/0174-announce-a-new-version-to-open-sessions.md))
- **What's new since your last visit**: the Settings ▸ What's new page lists the
  changes by version with a **New** badge; a notification after sign-in and a dot
  on the Start menu point out what has not been seen. The same list writes the
  notes of the GitHub Release, of the NuGet packages and
  [`CHANGELOG.md`](../../../CHANGELOG.md).
  ([`change-list.md`](dev/change-list.md), [ADR 0186](dev/adr/0186-whats-new-change-list.md))

### Personalization

- **OS themes**, and an editor to create your own.
  ([`theme-files.md`](framework/theme-files.md))
- **English and French**, the language follows the user's locale.
  ([`localization.md`](framework/localization.md))
- **Accessibility**: display and animation preferences, keyboard navigation.
  ([`accessibility.md`](dev/accessibility.md))
- **Local time and time zone**: the clock, `date`, `ls -l` and the apps show
  the time in the user's zone, automatic or chosen in Settings ▸ Date and time.
  ([`time-zone.md`](framework/time-zone.md))
- **Icon packs**: PNG images replace the icons of apps, folders and files
  everywhere in the OS, chosen in Settings ▸ Appearance; a "Néon" sample pack
  is included. ([`icon-packs.md`](dev/icon-packs.md),
  [ADR 0203](dev/adr/0203-icon-packs.md))
- **System sounds**: notifications, warnings, errors, sign-in and sign-out. Two
  built-in themes (HackerOS and Soft), your own themes in OGG or MP3, volume and
  mute in Settings ▸ Sound.
  ([`system-sounds.md`](dev/system-sounds.md), [ADR 0204](dev/adr/0204-os-event-sounds.md))

### For developers

- **Public SDK on NuGet** (`HackerOs.*`, version 2.1.0): file API, windows,
  terminal, network, background jobs, clipboard, dialogs, Blazor components.
  ([`sdk-reference.md`](framework/sdk-reference.md),
  [`sdk-packages.md`](framework/sdk-packages.md))
- **`dotnet new` templates** for a windowed application, a command, a
  service, a site, a complete host or a whole application repository, shown
  in Visual Studio and Rider.
  ([`sdk-developer-guide.md`](framework/sdk-developer-guide.md))
- **Install an application from its GitHub repository** with the `hackpkg`
  command. ([ADR 0182](dev/adr/0182-install-apps-from-github.md),
  [`app-installation.md`](dev/app-installation.md)) Its images, scripts and
  Web Workers are served at the same address as for an application built into
  the OS, so an application that wraps a JavaScript project works once
  installed. ([ADR 0206](dev/adr/0206-serve-installed-app-assets.md))
- **`hackeros-solution` application repository template**: application, host
  and tests ready to run, the OS as a submodule to debug in its sources, NuGet
  packages in production, CI and submission to the Hack Store catalog with a
  tag. ([ADR 0176](dev/adr/0176-app-repository-template.md),
  [ADR 0207](dev/adr/0207-solution-template-implementation.md),
  [`app-repository.md`](framework/app-repository.md))
- **Automatic releases**: GitHub Release, NuGet packages and OS archive for
  each new version. ([`releases.md`](dev/releases.md))
- **Browser tests every night**: the Playwright suites run at night on `main`
  when it changed; a failure opens an issue.
  ([`ci-workflows.md`](dev/ci-workflows.md#browser-tests-every-night))

## In development

This section lists the planned features, with their architecture decision
(ADR) and their tracking issue. When an ADR is not in `main` yet, the link
points to its PR.

### Desktop, windows and files

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Switch user | Open another session without closing the first one. Stays greyed out in the Power menu by Benoit's choice: one session at a time, Sign out to change account. | [0195](dev/adr/0195-start-menu-power-button.md), [0201](dev/adr/0201-switch-user-stays-greyed.md) | [#290](https://github.com/Kazo-ca/HackerOs/issues/290) |
| Windows and taskbar | Window animations, hover previews. The time zone ([ADR 0193](dev/adr/0193-local-time-and-time-zone.md)), the taskbar menus ([ADR 0194](dev/adr/0194-taskbar-context-menus.md)) and the notification bubbles ([ADR 0197](dev/adr/0197-notification-toasts.md)) are available. | Coming | [#242](https://github.com/Kazo-ca/HackerOs/issues/242) |

### Hack Store

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Trusted sources and per-user install | Trusted catalogs, install without an administrator, block list, administrator approval. | [0189](dev/adr/0189-app-sources-trust-and-per-user-install.md) | [#248](https://github.com/Kazo-ca/HackerOs/issues/248) |
| Application catalog | Catalog format, checking and publishing of the offered applications. | [0188](dev/adr/0188-hack-store-and-app-catalog.md) | [AppMarketPlace #1](https://github.com/Kazo-ca/HackerOs-AppMarketPlace/issues/1) |
| Hack Store app v1 | Browse, install, update and uninstall applications in one click. | [0188](dev/adr/0188-hack-store-and-app-catalog.md) | [#249](https://github.com/Kazo-ca/HackerOs/issues/249) |
| Hack Store, next steps | Adding without restart, package signing, transactional install. | [0188](dev/adr/0188-hack-store-and-app-catalog.md) | [#251](https://github.com/Kazo-ca/HackerOs/issues/251) |

### Personalization

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Theme packs | `.hacktheme` format and the Hack Store Themes tab. | [0190](dev/adr/0190-theme-packs.md) | [#253](https://github.com/Kazo-ca/HackerOs/issues/253) |
| Image wallpapers | Choose an image as wallpaper, per screen. | [0190](dev/adr/0190-theme-packs.md) | [#254](https://github.com/Kazo-ca/HackerOs/issues/254) |
| Cursor themes | Custom cursors chosen in Settings. | [0190](dev/adr/0190-theme-packs.md) | [#257](https://github.com/Kazo-ca/HackerOs/issues/257) |

### Network and server

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Real sites in the browser | Show a real web site through the HackerOS server relay. | Coming | [#266](https://github.com/Kazo-ca/HackerOs/issues/266) |

### Optional ported applications

Well-known open source applications adapted to HackerOS, each in its own
repository. They are not part of the OS: they will be installed separately,
with `hackpkg` and then from the Hack Store.

| Application | Description | Repository | Issue |
| --- | --- | --- | --- |
| Chess | Chess, based on chess.js; first application planned for the catalog. | [HackerOs-App-Chess](https://github.com/Kazo-ca/HackerOs-App-Chess) | [#2](https://github.com/Kazo-ca/HackerOs-App-Chess/issues/2), [#3](https://github.com/Kazo-ca/HackerOs-App-Chess/issues/3) |
| Webamp | Music player in the style of Winamp. | [HackerOs-App-Webamp](https://github.com/Kazo-ca/HackerOs-App-Webamp) | Coming |
| IRC | IRC chat client (KiwiIRC). | [HackerOs-App-IRC](https://github.com/Kazo-ca/HackerOs-App-IRC) | Coming |
| EmulatorJS | Retro console emulator. | [HackerOs-App-EmulatorJS](https://github.com/Kazo-ca/HackerOs-App-EmulatorJS) | Coming |
| BoxedWine | Run old Windows programs in the OS. | [HackerOs-App-BoxedWine](https://github.com/Kazo-ca/HackerOs-App-BoxedWine) | Coming |

## Keeping this document current

Every PR to `main` that ships, changes or plans a feature updates this
document **in both languages**:

- a shipped feature moves from "In development" to "Available features";
- a new `TODO` issue or a new feature ADR adds a row to "In development";
- the link to an ADR's PR is replaced by the link to the ADR once it is
  merged.

The PR template ([`.github/pull_request_template.md`](../../../.github/pull_request_template.md))
has a checkbox as a reminder.
