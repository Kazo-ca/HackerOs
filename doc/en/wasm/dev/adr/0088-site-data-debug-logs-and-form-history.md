# ADR 0088: site database, debug logs and autofill

## Status

Accepted on 2026-10-17, with the `web/12-site-data` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)), after the closure
  noted by ADR 0087 §4.
- **Implements:** ADR 0073 §3 (`hackweb.<id>` database, `/var/log/hackweb/<id>.log` logs, browser
  autofill).
- **Adjusts:** ADR 0073 §3 on one point, where the database is opened (§1 below). The other
  decisions of ADR 0073 §3 remain as they are.
- **Completes:** ADR 0045 (logs), ADR 0048 (uninstallation), ADR 0056 §11 (storage
  diagnostics), ADR 0070 (remote gateways), ADR 0075 §3 (page bridge).

## Context

ADR 0073 §3 decided, with Benoit (October 16, 16:44Z), that no site data goes into the
OS file system, except what belongs to the browser: cookies, autofill
and debug logs. A site that wants a database uses the host browser's IndexedDB,
through a helper that imposes its name. ADR 0087 §4 notes that no work item built
it. This work item builds it, and the Intranet sample uses it.

## Decision

### 1. A site's database goes through an OS gateway

ADR 0073 §3 planned to open the database from the web worker. It is opened by the OS, on the
main thread, and the site reaches it through a gateway of its execution context
(`IAppExecutionContext.WebDatabase`), like the network or the clipboard (ADR 0070 §4):

- **the name is imposed by the OS**, not by the SDK: a process's gateway only knows
  `hackweb.<its app's id>`. A site cannot avoid it by calling `indexedDB.open` itself,
  since its C# code has no JavaScript access;
- the OS needs these databases anyway (Diagnostic list, erasure on uninstallation);
  a single JavaScript module (`webSiteDatabases.js` of `Infrastructure.Browser`) opens them;
- the site runs the same way in the web worker and on the main thread, and its tests
  without an OS replace the gateway with an in-memory database.

Only an app of kind `Web` receives this gateway; other apps have the file system.
The module refuses any name that does not start with `hackweb.`: the OS database
(`HackerOsIndexedDb`) remains out of reach.

### 2. The IndexedDB model, carried in batches

The API follows IndexedDB: versions and migration steps (create or delete a store or an
index), object stores with a simple key path (`id`, `author.name`) or external keys,
`autoIncrement`, `unique` and `multiEntry` indexes, key ranges, `get`, `getAll`, `getAllKeys`,
`count`, `add`, `put`, `delete`, `clear`. Values are JSON.

An IndexedDB transaction ends as soon as its code awaits something other than a database
request; across the worker, a round trip per request would close it. An
`HackWebDb` transaction is therefore a **batch**: the site queues its writes, `CommitAsync` sends them together and
they succeed or fail together (a single `readwrite` transaction on the browser side). A
read is a `readonly` transaction on its own.

Errors keep the names of IndexedDB's `DOMException`s (`ConstraintError`, `NotFoundError`,
`DataError`, `VersionError`) in `HackWebDbException.Name`.

### 3. Web SDK API

```csharp
builder.Services.AddHackWebDb(db => db
    .Version(1, v => v.CreateStore("notes", keyPath: "id", autoIncrement: true)
                      .CreateIndex("notes", "byAuthor", "author")));

public sealed class NotesController(HackWebDb db) : HackController
{
    public async Task<IHackActionResult> Index(CancellationToken cancellationToken) =>
        View(await db.Store<Note>("notes").GetAllAsync(cancellationToken: cancellationToken));
}
```

Types are read and written with the site's JSON options (`builder.Json`), hence with its
generated context for a trimmed publish; simple values (`string`, numbers, `bool`) do not
need to appear there. A test's host can register `HackWebDbInMemoryBackend` in place of the
gateway; a site built outside HackerOS without a gateway receives an in-memory database
(`hackweb.in-memory`), so that its unit tests run without configuring anything.

### 4. Size, listing and erasure

- Diagnostic (`system.storage.inspect`, a dangerous capability that an administrator approves)
  lists the sites' databases: name, app, version, stores,
  record count and size (UTF-8 bytes of the records' JSON, an estimate like
  the one Chromium displays per origin). It can delete one; the site recreates it empty, with
  its schema, on its next request.
- Uninstalling a `Web` app through `IAppInstallationService.UninstallAsync` deletes its database,
  after the Settings confirmation, which announces it. It is the only data that an uninstallation
  erases (ADR 0048 keeps the rest for a reinstallation): the database is not in the user's
  files, they could not find it again. An uninstallation by direct edit
  of `apps.config` deletes nothing.

### 5. A site's debug logs

A site enables its log in `Configure`: `builder.Logging.EnableDebugLog()`. It then receives
`ILogger<T>` (`Microsoft.Extensions.Logging`, like ASP.NET Core) and the host writes each request
(`Request starting` / `Request finished ... in ... ms`) and each unhandled exception. The lines go
to `/var/log/hackweb/<id>.log` through a new method of the log gateway,
`AppendToSiteDebugLog`, reserved for `Web` apps: the OS log writer writes the file
(ADR 0045), with the same rotation (1 MiB × 5), as `system:system 0644`. Without
`EnableDebugLog`, `ILogger<T>` exists but writes nothing. A line break in a message is
escaped (`\n`), as for the `hackwebd` logs, so that one line cannot imitate
another.

### 6. Browser autofill

The OS browser remembers what is typed in forms, like Firefox's form
history:

- when a form is submitted, the page bridge forwards the `text`, `search`, `email`, `tel`,
  `url` and `number` fields that have a name and a value; it ignores passwords, hidden fields,
  `autocomplete="off"` (on the field or the form) and a payment card number;
- values are kept by field name, for all sites, in
  `~/.local/share/hackeros/browser/form-history.json` (user file, next to
  `localStorage`), at most 100 values per name and 2,000 in total, the least used leaving first;
- when a field receives focus, the bridge asks for the values of its name and offers them in a
  `<datalist>`, the host browser's native dropdown;
- `Ctrl+Shift+Delete` clears the form history, like "Clear browsing data".

The forms of a Blazor site are drawn in the OS DOM (ADR 0084), outside the bridge: they
do not have this autofill.

## Consequences

- `IAppExecutionContext` gains `WebDatabase` (default "not supported"); the worker has two
  remote operations (`wdb.open`, `wdb.exec`) and one notification (`log.site`).
- The hosts declare `IWebSiteDatabaseStore`: IndexedDB in the browser, memory elsewhere.
- The Intranet sample keeps its notes in `hackweb.org.hackeros.samples.intranet`: they survive
  the site stopping and the OS rebooting. It enables its debug log.
- An app id ending in `.access` or `.error` would share a file name with the
  `hackwebd` logs (ADR 0080 §3) for a hosted site of the same name; the catalog does not
  forbid it, the collision is just unlikely.
- Guide: [`asp-hack-site-data.md`](../../framework/asp-hack-site-data.md).
