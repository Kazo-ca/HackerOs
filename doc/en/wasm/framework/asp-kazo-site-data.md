> [Version française](../../../fr/wasm/framework/asp-kazo-site-data.md)

# Site data: `KazoWebDb` database, debug log, autofill

Work item `web/12-site-data` of the web phase ([`asp-kazo-plan.md`](../dev/plans/asp-kazo-plan.md)). Decisions:
[ADR 0088](../dev/adr/0088-site-data-debug-logs-and-form-history.md), which implements ADR 0073 §3.
General guide to sites: [`asp-kazo-developer-guide.md`](asp-kazo-developer-guide.md).

## Purpose

An ASP.Kazo site has no `filesystem.*` capability: its data does not go into the OS files. This work item gives it
three things:

| Building block | Where the data lives | Who sees it |
|---|---|---|
| **`KazoWebDb`**: one IndexedDB database per site | `kazoweb.<app id>` database of the host browser | the site; Diagnostics (list, size, clearing) |
| **Debug log** (`ILogger<T>`) | `/var/log/kazoweb/<id>.log`, an OS file | `cat`, `tail`, `grep` in a terminal |
| Browser **autofill** | `~/.local/share/kazoos/browser/form-history.json` | the OS browser, for all sites |

## Architecture

```text
 Site (worker web ou thread principal)          OS (thread principal)                 Navigateur hôte
 ─────────────────────────────────────          ────────────────────                  ───────────────
 KazoWebDb ─► IAppWebDatabaseGateway ──wdb.open/wdb.exec──► AppWebDatabaseGateway ─► IWebSiteDatabaseStore
   (SDK web)   (RemoteWebDatabaseGateway                     (nom imposé :            IndexedDbWebSiteDatabaseStore
                dans le worker)                               kazoweb.<id>, Web seul)   └► webSiteDatabases.js ─► IndexedDB
 ILogger<T> ─► KazoWebDebugLogProvider ─► IAppLoggingGateway ──log.site──► AppLoggingGateway.AppendToSiteDebugLog
                                                                              └► LogFileWriter ─► /var/log/kazoweb/<id>.log
 Page HTML (iframe) ─ PageBridge.js ─"formhistory"─► BrowserWindow ─► FormHistory ─► form-history.json
```

| Class | Project | Role |
|---|---|---|
| `IAppWebDatabaseGateway`, `KazoWebDbNames`, `KazoWebDb*` contracts | `Shared/Kazo.Os.AppSdk/WebData` | contract between the site and the OS, in JSON |
| `InMemoryWebDatabase`, `KazoWebDbInMemoryBackend` | same | in-memory IndexedDB engine (hosts without a browser, tests) |
| `KazoWebDb`, `KazoWebDbStore<T>`, `KazoWebDbIndex<T>`, `KazoWebDbTransaction` | `Shared/Kazo.Os.AppSdk.Web/Data` | site API |
| `KazoWebLoggingOptions`, `IKazoWebDebugLogSink` | `Shared/Kazo.Os.AppSdk.Web/Logging` | debug log |
| `AppWebDatabaseGateway`, `IWebSiteDatabaseStore`, `InMemoryWebSiteDatabaseStore` | `Platform/Kazo.Os.Platform.Core/WebData` | trusted gateway, in-memory store |
| `WebSiteDatabaseInterop`, `wwwroot/webSiteDatabases.js` | `Infrastructure/Kazo.Os.Infrastructure.Browser` | real IndexedDB |
| `IndexedDbWebSiteDatabaseStore` | `OS/Kazo.Os.Ecosystem/WebData` | Ecosystem host store |
| `FormHistory` | `Apps/System/Kazo.Os.Apps.Browser/Storage` | form history |

## A site's database

### Declaring the schema

Like `IDBOpenDBRequest.onupgradeneeded`, each version describes what changes; versions follow
one another without gaps, and the OS applies the missing ones in order.

```csharp
public override void Configure(KazoWebApplicationBuilder builder)
{
    builder.Services.AddKazoWebDb(db => db
        .Version(1, v => v
            .CreateStore("notes", keyPath: "id", autoIncrement: true)
            .CreateIndex("notes", "byAuthor", "author")
            .CreateStore("meta"))                       // clés externes
        .Version(2, v => v.CreateIndex("notes", "byTag", "tags", multiEntry: true)));
    builder.Services.AddSingleton<NoteStore>();
    builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, MySiteJsonContext.Default);
    ...
}
```

`AddKazoWebDb` checks the versions when the site starts. The database opens on the first request.

### Reading and writing

```csharp
public sealed class NoteStore(KazoWebDb db)
{
    private KazoWebDbStore<Note> Notes => db.Store<Note>("notes");

    public Task<IReadOnlyList<Note>> ByAuthorAsync(string author) =>
        Notes.Index("byAuthor").GetAllAsync(author);

    public async Task<int> AddAsync(Note note) =>
        (int)(await Notes.AddAsync(note)).AsInt64();   // clé donnée par le générateur
}
```

| Method | IndexedDB |
|---|---|
| `GetAsync(key)`, `GetAllAsync(range?, count?)`, `GetAllKeysAsync`, `CountAsync` | `get`, `getAll`, `getAllKeys`, `count` |
| `AddAsync(value, key?)` | `add`: `ConstraintError` if the key exists |
| `PutAsync(value, key?)` | `put`: replaces |
| `DeleteAsync(key \| range)`, `ClearAsync()` | `delete`, `clear` |
| `Index(name).GetAsync/GetAllAsync/CountAsync` | `IDBIndex` |
| `KazoWebDbKeyRange.Only/LowerBound/UpperBound/Bound` | `IDBKeyRange` |
| `KazoWebDbKey.Array(...)` | array key |

Keys follow IndexedDB's order: numbers, then strings, then arrays.

**Generated key.** As in a browser, an object that carries `id: 0` is stored under key `0`.
To let the generator number it, the identifier is not written when it equals its default
value: `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` on `Id`
(see the Intranet's `Note`).

### Transactions

A transaction is a batch (ADR 0088 §2): writes are queued then sent together, and
succeed or fail together.

```csharp
KazoWebDbTransaction tx = db.BeginTransaction("notes", "meta");
tx.Store<Note>("notes").Add(first).Add(second);
tx.Store<string>("meta").Put("done", "seeded");
IReadOnlyList<KazoWebDbKey?> keys = await tx.CommitAsync();   // une clé par écriture
```

Errors are `KazoWebDbException`s whose `Name` repeats the `DOMException`:
`ConstraintError`, `NotFoundError`, `DataError`, `VersionError`, `InvalidStateError`,
`QuotaExceededError`.

### JSON

Values are serialized with the site's options (`builder.Json`), so by its generated
context: the stored type must appear in it (`[JsonSerializable(typeof(Note))]`). `string`, `int`,
`long`, `double` and `bool` work without it.

### Outside Kazo OS and in tests

- `KazoWebHost.CreateApplication` without an OS: the database is in memory (`kazoweb.in-memory`), specific to
  the application. The Intranet tests run this way without any configuration.
- To inspect a test's database, register the gateway before `AddKazoWebDb`:
  `services.AddSingleton<IAppWebDatabaseGateway>(new KazoWebDbInMemoryBackend("kazoweb.test"))`,
  then read `backend.Database.Describe()`.

### What the user sees

- **Diagnostics** ("IndexedDB Diagnostic", capability `system.storage.inspect` approved by an
  administrator): *Website databases* section with each site, its version, its stores, the
  number of records and an estimated size. *Clear this database* deletes it; the site
  recreates it empty at its next request.
- **Settings → Applications → Uninstall**: the confirmation dialog announces that the
  `kazoweb.<id>` database will be deleted. It is deleted after the uninstall. Reinstalling the site starts
  from an empty database.

## Debug log

```csharp
builder.Logging.EnableDebugLog();                       // LogLevel.Debug et au-dessus
// builder.Logging.EnableDebugLog(LogLevel.Information);
// builder.Logging.LogRequests = false;                 // sans les lignes de requêtes

public sealed class NotesController(NoteStore store, ILogger<NotesController> logger) : KazoController
{
    ...
    logger.LogInformation("Note {NoteId} created by {Author}", created.Id, created.Author);
}
```

```text
$ tail /var/log/kazoweb/org.kazoos.samples.intranet.log
2026-10-17T01:54:42.880Z info: Kazo.Os.AppSdk.Web.Hosting.Diagnostics[0] Request starting HTTP/1.1 POST http://intranet.hack/Notes/Create - application/x-www-form-urlencoded 87
2026-10-17T01:54:42.921Z info: Kazo.Os.Samples.Intranet.Controllers.NotesController[0] Note 4 created by anonymous
2026-10-17T01:54:42.925Z info: Kazo.Os.AppSdk.Web.Hosting.Diagnostics[0] Request finished HTTP/1.1 POST http://intranet.hack/Notes/Create - 302 0 - 45.1000ms
```

- Format: UTC time, ASP.NET Core short level (`trce`, `dbug`, `info`, `warn`, `fail`,
  `crit`), category, event identifier, message; an exception follows on the same line.
- Without `EnableDebugLog`, `ILogger<T>` is injectable but writes nothing.
- The file is written by the OS (`system:system`, `0644`, rotation 1 MiB × 5, ADR 0045).
- Only a `Web` app can write this log; any other receives `UnauthorizedAccessException`.
- A test registers `KazoWebDebugLogMemorySink` as `IKazoWebDebugLogSink` to read the lines.

## Autofill

In an HTML page (MVC views, static pages, simulated external sites):

1. When a form is submitted, the bridge remembers the `text`, `search`, `email`, `tel`, `url`,
   `number` fields that have a `name`. Never: passwords, hidden fields, `autocomplete="off"` (field
   or form), card numbers (13 to 19 digits valid per Luhn), values longer than 200
   characters.
2. When a field gets focus, the bridge asks for the values of its `name` (20 at most, most used
   first) and attaches them through a `<datalist>`.
3. `Ctrl+Shift+Delete` clears the history; a banner confirms it.

Forms of a Blazor site (ADR 0084) do not go through the bridge and do not benefit from it.

## Tests

| Suite | What it proves |
|---|---|
| `Kazo.Os.AppSdk.Web.Tests/Data/KazoWebDbTests` | key generator, unique and `multiEntry` indexes, key order, ranges, atomic transactions, migrations, `VersionError`, in-memory fallback |
| `Kazo.Os.AppSdk.Web.Tests/Logging/KazoWebDebugLogTests` | log silent by default, request lines, `ILogger<T>`, exceptions, minimum level |
| `Kazo.Os.Platform.Core.Tests/WebData/WebSiteDatabaseTests` | enforced name, non-`Web` apps refused, listing and clearing subject to `system.storage.inspect`, file `/var/log/kazoweb/<id>.log` |
| `Kazo.Os.Platform.Core.Tests/Apps/AppInstallationServiceTests` | uninstalling a site deletes its database, and only its own |
| `Kazo.Os.Apps.Browser.Tests/FormHistoryTests` | user's file, sort by usage, limits, cards refused, clearing |
| `Kazo.Os.UI.E2E.Tests/BrowserSiteDataTests` | in Chromium: Intranet note in IndexedDB, `<datalist>` offered, log line read by `grep`, database listed and cleared by Diagnostics |

## Key decisions

- The database goes through an OS gateway and not through the worker (ADR 0088 §1): the name is enforced
  by the OS, and Diagnostics as well as uninstallation need it.
- Batched transactions, because an IndexedDB transaction does not survive a round trip between
  the worker and the main thread.
- Form history is a user file, like the browser's cookies and
  `localStorage` (ADR 0073 §3).

## Tasks

- [x] ADR 0088.
- [x] `KazoWebDb*` contracts, in-memory engine, `IAppExecutionContext.WebDatabase` gateway.
- [x] Worker remote operations (`wdb.open`, `wdb.exec`, `log.site`).
- [x] `webSiteDatabases.js` module, Ecosystem IndexedDB store.
- [x] `KazoWebDb` API of the web SDK, in-memory fallback outside the OS.
- [x] `ILogger<T>` and `/var/log/kazoweb/<id>.log` log.
- [x] Diagnostics: listing and clearing; uninstall: deletion announced and done.
- [x] Browser form history (`<datalist>`, `Ctrl+Shift+Delete`).
- [x] Intranet: notes in `kazoweb.org.kazoos.samples.intranet`, `?author=` filter through the index, log enabled.
- [x] Unit tests and Chromium E2E.
