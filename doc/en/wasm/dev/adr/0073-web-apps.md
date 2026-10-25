> [Version française](../../../../fr/wasm/dev/adr/0073-web-apps.md)

# ADR 0073: web apps (`Web` kind), external sites and hosted sites

## Status

Accepted on 2026-10-16 (proposed the same day, then validated by Benoit with the
questionnaire of section 4 of the plan).

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** ADR 0047 (manifest as a C# class), ADR 0049 (capabilities),
  ADR 0070 (execution host).

## Context

Benoit wants internal websites: C# projects in the style of ASP.NET MVC, interactive
Blazor sites and static sites. Today a simulated site is a controller registered
at host startup, with no process or manifest
(`ISimulatedWebsiteController`). We need to say what a site is from the point of view
of the app model, where its files come from and what it is entitled to.

## Decision

### 1. New `Web` app kind

`AppKind` gains `Web` (`"web"` in `manifest.json`). The definition derives from
`WebAppDefinition` (library `HackerOs.AppSdk.Web`):

```csharp
[AppManifest]
public sealed class IntranetApp : WebAppDefinition
{
    public override string Id => "com.example.intranet";
    public override string Name => "Intranet";
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
    public override void Configure(HackWebApplicationBuilder builder) { ... } // MVC (ADR 0076)
    public override Type? BlazorRoot => null;                                 // ou typeof(App) (§4)
}
```

- `Urls` is mandatory and generated in the manifest (field `web.urls`); the
  OS resolver reads it (ADR 0074). A name taken by two apps is a catalog
  error (`catalog.web.url-conflict`).
- A site can be MVC only, Blazor only, or both: MVC routes go
  first, the rest goes to the Blazor component.
- Execution host (ADR 0070): the server code of **all** sites (MVC,
  routes, views) runs in **a single shared worker**, the web worker, like an
  IIS application pool. Each site remains a distinct process (PID, task manager
  row, clean stop through `SIGTERM`), but an infinite loop
  in one site blocks all sites, not the OS; "Force kill" of a site that does not
  respond terminates the web worker and therefore all sites, which restart on the
  next request (on-demand activation, ADR 0074 §3). The web worker
  starts with the first site called. The interactive Blazor part of a site
  runs on the main thread, where the browser draws it (§4).

### 2. Two kinds of sites, depending on where the files come from

| Kind | Origin | Address | Files |
|---|---|---|---|
| **External** | a C# project (installed `Web` app) | the `Urls` of its manifest (`intranet.hack`) | the project's `wwwroot`, embedded in the app, **read-only**; the user browses it like a remote site |
| **Hosted** | OS files, declared by the user (ADR 0077) | `http://localhost:<port>/`, plus an `/etc/hosts` name if desired | file system folder, modifiable |

Statically registered sites (HackMail, HackerSearch...) remain supported
as is, as external sites without a process.

### 3. Capabilities of a site

- A site has **no** `filesystem.*` capability: its data is not in the OS
  file system. The generator refuses a `WebAppDefinition`
  that requests one (`HOSAPP0050`). It only sees its embedded resources.
- If it needs a database, it uses the host browser's local database
  (IndexedDB, like the OS file system itself), through a
  web SDK helper that imposes a database name derived from the app identifier
  (`hackweb.<id>`): a site can neither choose nor open another site's database.
  The API is modeled on IndexedDB (object stores, indexes, transactions,
  versions), with a typed helper on top (`HackWebDb`,
  `store.GetAsync<Note>(id)`, `PutAsync`, `DeleteAsync`, index queries).
  Since IndexedDB is available in a Web Worker, the database opens from the
  web worker.
- A site's database is erased with the app on uninstallation, after
  confirmation. The Diagnostic app lists the sites' databases (name, size) and
  lets you empty them; they do not appear in the OS files.
- Only browser-specific data goes into the OS file system:
  cookies, autofill, and a site's logs when the
  site enables them for debugging (`/var/log/hackweb/<id>.log`).
- `network.listen` is granted automatically on its own `Urls`;
  `network.simulated.read`/`write` are normal; `network.real.access` stays
  dangerous.

### 4. Interactive Blazor site

A Blazor site resembles a windowed app, but its root component is
drawn in the page area of the HackerOS browser instead of a window. It
navigates through the tab's URL (the site's own `NavigationManager`, linked to the
address bar). It does not receive window services (`WindowAppBase`) nor
any `filesystem.*` capability. The component is isolated by scoped CSS; the browser passes it
no OS service outside the permitted SDK.

### 5. Opening toward compilation in the OS

The catalog does not assume that a `Web` app comes from the solution: a future
compiler in the OS will produce the same `WebAppDefinition` and the same manifest,
installed by `IAppInstallationService`.

## Consequences

- The manifest generator, the schema and `AppCatalog` know `Web` and
  `web.urls`.
- The browser becomes the display host for Blazor sites.
- Existing apps do not change.
