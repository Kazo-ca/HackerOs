> [Version française](../../../../fr/wasm/dev/adr/0079-browser-html-rendering-adjustments.md)

# ADR 0079: adjustments to the browser HTML rendering

## Status

Accepted on 2026-10-16, with the `web/4-browser-html` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** [ADR 0075](0075-browser-html-rendering.md), without changing its
  decisions (iframe without same origin, subresources loaded in C#, bridge).
- **Documentation:** [`browser-html-rendering.md`](../browser-html-rendering.md).

## Context

The implementation of ADR 0075 encountered points that the ADR did not settle,
or that an iframe without same origin does not allow doing like a real
browser. They are recorded here.

## Decision

1. **`file://` limited to the home folder.** The browser does not request
   `filesystem.system.read`: this capability is dangerous (ADR 0049) and
   triggered an administrator approval at each launch, since the
   browser is not a system app. It reads and writes the user's folder
   (`filesystem.user-home.read`/`write`), like its
   downloads. A file outside the home folder gives
   `ERR_ACCESS_DENIED`.
2. **Subresources as `data:` URLs only.** No `blob:` created by
   the host: the `srcdoc` carries everything. A failed resource becomes `data:,`
   and appears in the Network tab.
3. **No nested frames.** `iframe`, `frame`, `object` and `embed`
   of a page become `about:blank`, with a Console warning.
4. **Script navigation detected, not intercepted.** `location` stays
   `about:srcdoc` in an iframe without same origin: `location.href = ...`
   cannot be hijacked. `document.URL` and `document.referrer` give
   the real URL; a script navigation is detected (frame load
   without a bridge announcement) and replaced by an
   `ERR_BLOCKED_BY_CLIENT` error page rather than by a document without a bridge.
5. **Limits accepted for this phase.** Relative `import`s of ES modules
   do not resolve; no CORS preflight (`OPTIONS`) is sent, only
   the response headers are checked; `HttpOnly` is not modeled;
   `target="_blank"` opens in the same tab.
6. **Dialog boxes.** `alert` becomes a non-blocking banner
   above the page; `confirm` returns `false` and `prompt` `null`, like
   a browser that blocks a page's dialog boxes.
7. **Demo sites.** The `example.hackeros` site of the demo network
   serves test HTML pages (`/page.html`, `/form.html`, `/echo`,
   `/api/ping`) and HackerSearch returns `Access-Control-Allow-Origin: *` on
   `/api/search`, so that a page from another origin can call it.

## Consequences

- The browser no longer triggers an administrator approval at launch.
- A page that navigates by script must use a link or a form
  to work in HackerOS.
- The limits of point 5 can be lifted by a later ADR.
