> [Version française](../../../../fr/wasm/dev/adr/0174-announce-a-new-version-to-open-sessions.md)

# ADR 0174: announcing a new version to open sessions

## Status

Accepted on 2026-10-26 at Benoit's request ("publish a json file that lets
sessions see whether a new version is available and show it to the user, for
example every 5 minutes"); source chosen by Benoit: "Same site as the OS".
Completes [ADR 0162](0162-restart-after-a-host-build-change.md) and
[ADR 0173](0173-automatic-releases-from-the-version-file.md).

## Decision

1. **`version.json`**: the build of `OS/HackerOs.Ecosystem` writes
   `{ "product": "HackerOS", "version": "<HackerOsVersion>" }` into `obj/` and
   serves it as a static web asset at the root of the site (`/version.json`).
   It follows `build/Versions.props`: nothing to edit by hand, nothing in the
   sources.
2. **`HostReleaseMonitor`** (`HackerOs.Platform.Blazor`, next to
   `HostBuildUpdateMonitor`) reads that file every **5 minutes**
   (`DefaultInterval`) through `IHostReleaseProbe`. The browser host
   (`BrowserHostReleaseProbe`) requests it with `?t=<milliseconds>`, so the
   service worker does not answer with its cached copy, and with
   `cache: no-store`. A version other than `HackerOsSdk.Version` is reported
   once per version (`ReleaseAvailable`). Offline, server down or unreadable
   file: no update.
3. Each check also asks `HostBuildUpdateMonitor` whether the build changed
   under the page with the same number (a Pages deployment without a version
   bump): the banner then shows without a number.
4. **Display**: the existing ADR 0162 banner says "HackerOS 2.2.0 is
   available. Open apps will close." with the "Restart to update" button.
   Restarting reloads the page, which takes the new version the site serves.

## Consequences

- Works for any site serving the PWA (GitHub Pages, `HackerOs.Server`, another
  static host); a host from another repository without `version.json` only
  gets a 404, so no announcement.
- A request of a few bytes every 5 minutes per open session.

## Rejected options

- **Latest GitHub Release** (`api.github.com`): would announce a version the
  site may not serve yet, and depend on github.com and its limits; rejected by
  Benoit.
- **Relying on the service worker only**: the browser checks the worker again
  only on navigation, and gives no number.
