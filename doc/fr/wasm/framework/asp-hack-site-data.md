> [English version](../../../en/wasm/framework/asp-hack-site-data.md)

# Données d'un site : base `KazoWebDb`, journal de débogage, saisie semi-automatique

Chantier `web/12-site-data` de la phase web ([`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md)). Décisions :
[ADR 0088](../dev/adr/0088-site-data-debug-logs-and-form-history.md), qui réalise l'ADR 0073 §3.
Guide général des sites : [`asp-hack-developer-guide.md`](asp-hack-developer-guide.md).

## Objet

Un site ASP.Hack n'a pas de capacité `filesystem.*` : ses données ne vont pas dans les fichiers de
l'OS. Ce chantier lui donne trois choses :

| Brique | Où vivent les données | Qui les voit |
|---|---|---|
| **`KazoWebDb`** : une base IndexedDB par site | base `hackweb.<id de l'app>` du navigateur hôte | le site ; le Diagnostic (liste, taille, effacement) |
| **Journal de débogage** (`ILogger<T>`) | `/var/log/hackweb/<id>.log`, fichier de l'OS | `cat`, `tail`, `grep` dans un terminal |
| **Saisie semi-automatique** du navigateur | `~/.local/share/hackeros/browser/form-history.json` | le navigateur de l'OS, pour tous les sites |

## Architecture

```text
 Site (worker web ou thread principal)          OS (thread principal)                 Navigateur hôte
 ─────────────────────────────────────          ────────────────────                  ───────────────
 KazoWebDb ─► IAppWebDatabaseGateway ──wdb.open/wdb.exec──► AppWebDatabaseGateway ─► IWebSiteDatabaseStore
   (SDK web)   (RemoteWebDatabaseGateway                     (nom imposé :            IndexedDbWebSiteDatabaseStore
                dans le worker)                               hackweb.<id>, Web seul)   └► webSiteDatabases.js ─► IndexedDB
 ILogger<T> ─► KazoWebDebugLogProvider ─► IAppLoggingGateway ──log.site──► AppLoggingGateway.AppendToSiteDebugLog
                                                                              └► LogFileWriter ─► /var/log/hackweb/<id>.log
 Page HTML (iframe) ─ PageBridge.js ─"formhistory"─► BrowserWindow ─► FormHistory ─► form-history.json
```

| Classe | Projet | Rôle |
|---|---|---|
| `IAppWebDatabaseGateway`, `KazoWebDbNames`, contrats `KazoWebDb*` | `Shared/Kazo.Os.AppSdk/WebData` | contrat entre le site et l'OS, en JSON |
| `InMemoryWebDatabase`, `KazoWebDbInMemoryBackend` | idem | moteur IndexedDB en mémoire (hôtes sans navigateur, tests) |
| `KazoWebDb`, `KazoWebDbStore<T>`, `KazoWebDbIndex<T>`, `KazoWebDbTransaction` | `Shared/Kazo.Os.AppSdk.Web/Data` | API du site |
| `KazoWebLoggingOptions`, `IHackWebDebugLogSink` | `Shared/Kazo.Os.AppSdk.Web/Logging` | journal de débogage |
| `AppWebDatabaseGateway`, `IWebSiteDatabaseStore`, `InMemoryWebSiteDatabaseStore` | `Platform/Kazo.Os.Platform.Core/WebData` | passerelle de confiance, magasin en mémoire |
| `WebSiteDatabaseInterop`, `wwwroot/webSiteDatabases.js` | `Infrastructure/Kazo.Os.Infrastructure.Browser` | IndexedDB réel |
| `IndexedDbWebSiteDatabaseStore` | `OS/Kazo.Os.Ecosystem/WebData` | magasin de l'hôte Ecosystem |
| `FormHistory` | `Apps/System/Kazo.Os.Apps.Browser/Storage` | historique de formulaires |

## La base d'un site

### Déclarer le schéma

Comme `IDBOpenDBRequest.onupgradeneeded`, chaque version décrit ce qui change ; les versions se
suivent sans trou, et l'OS applique celles qui manquent dans l'ordre.

```csharp
public override void Configure(KazoWebApplicationBuilder builder)
{
    builder.Services.AddHackWebDb(db => db
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

`AddHackWebDb` vérifie les versions au démarrage du site. La base s'ouvre à la première requête.

### Lire et écrire

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

| Méthode | IndexedDB |
|---|---|
| `GetAsync(key)`, `GetAllAsync(range?, count?)`, `GetAllKeysAsync`, `CountAsync` | `get`, `getAll`, `getAllKeys`, `count` |
| `AddAsync(value, key?)` | `add` : `ConstraintError` si la clé existe |
| `PutAsync(value, key?)` | `put` : remplace |
| `DeleteAsync(key \| range)`, `ClearAsync()` | `delete`, `clear` |
| `Index(name).GetAsync/GetAllAsync/CountAsync` | `IDBIndex` |
| `KazoWebDbKeyRange.Only/LowerBound/UpperBound/Bound` | `IDBKeyRange` |
| `KazoWebDbKey.Array(...)` | clé tableau |

Les clés suivent l'ordre d'IndexedDB : nombres, puis chaînes, puis tableaux.

**Clé générée.** Comme dans un navigateur, un objet qui porte `id: 0` est rangé sous la clé `0`.
Pour laisser le générateur numéroter, l'identifiant n'est pas écrit quand il vaut sa valeur par
défaut : `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` sur `Id`
(voir `Note` de l'Intranet).

### Transactions

Une transaction est un lot (ADR 0088 §2) : les écritures sont empilées puis envoyées ensemble, et
réussissent ou échouent ensemble.

```csharp
KazoWebDbTransaction tx = db.BeginTransaction("notes", "meta");
tx.Store<Note>("notes").Add(first).Add(second);
tx.Store<string>("meta").Put("done", "seeded");
IReadOnlyList<KazoWebDbKey?> keys = await tx.CommitAsync();   // une clé par écriture
```

Les erreurs sont des `KazoWebDbException` dont `Name` reprend la `DOMException` :
`ConstraintError`, `NotFoundError`, `DataError`, `VersionError`, `InvalidStateError`,
`QuotaExceededError`.

### JSON

Les valeurs sont sérialisées avec les options du site (`builder.Json`), donc par son contexte
généré : le type rangé doit y figurer (`[JsonSerializable(typeof(Note))]`). `string`, `int`,
`long`, `double` et `bool` fonctionnent sans lui.

### Hors de Kazo OS et dans les tests

- `KazoWebHost.CreateApplication` sans OS : la base est en mémoire (`hackweb.in-memory`), propre à
  l'application. Les tests de l'Intranet tournent ainsi sans rien configurer.
- Pour inspecter la base d'un test, enregistrer la passerelle avant `AddHackWebDb` :
  `services.AddSingleton<IAppWebDatabaseGateway>(new KazoWebDbInMemoryBackend("hackweb.test"))`,
  puis lire `backend.Database.Describe()`.

### Ce que voit l'utilisateur

- **Diagnostic** (« IndexedDB Diagnostic », capacité `system.storage.inspect` approuvée par un
  administrateur) : rubrique *Website databases* avec chaque site, sa version, ses magasins, le
  nombre d'enregistrements et une taille estimée. *Clear this database* la supprime ; le site la
  recrée vide à sa requête suivante.
- **Paramètres → Applications → Désinstaller** : la boîte de confirmation annonce que la base
  `hackweb.<id>` sera supprimée. Elle l'est après la désinstallation. Réinstaller le site repart
  d'une base vide.

## Journal de débogage

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
$ tail /var/log/hackweb/org.hackeros.samples.intranet.log
2026-10-17T01:54:42.880Z info: Kazo.Os.AppSdk.Web.Hosting.Diagnostics[0] Request starting HTTP/1.1 POST http://intranet.hack/Notes/Create - application/x-www-form-urlencoded 87
2026-10-17T01:54:42.921Z info: Kazo.Os.Samples.Intranet.Controllers.NotesController[0] Note 4 created by anonymous
2026-10-17T01:54:42.925Z info: Kazo.Os.AppSdk.Web.Hosting.Diagnostics[0] Request finished HTTP/1.1 POST http://intranet.hack/Notes/Create - 302 0 - 45.1000ms
```

- Format : heure UTC, niveau court d'ASP.NET Core (`trce`, `dbug`, `info`, `warn`, `fail`,
  `crit`), catégorie, identifiant d'événement, message ; une exception suit sur la même ligne.
- Sans `EnableDebugLog`, `ILogger<T>` est injectable mais n'écrit rien.
- Le fichier est écrit par l'OS (`system:system`, `0644`, rotation 1 Mio × 5, ADR 0045).
- Seule une app `Web` peut écrire ce journal ; une autre reçoit `UnauthorizedAccessException`.
- Un test enregistre `KazoWebDebugLogMemorySink` comme `IHackWebDebugLogSink` pour lire les lignes.

## Saisie semi-automatique

Dans une page HTML (vues MVC, pages statiques, sites externes simulés) :

1. À l'envoi d'un formulaire, le pont retient les champs `text`, `search`, `email`, `tel`, `url`,
   `number` qui ont un `name`. Jamais : mots de passe, champs cachés, `autocomplete="off"` (champ
   ou formulaire), numéros de carte (13 à 19 chiffres valides selon Luhn), valeurs de plus de 200
   caractères.
2. Au focus d'un champ, le pont demande les valeurs de son `name` (20 au plus, les plus utilisées
   d'abord) et les attache par une `<datalist>`.
3. `Ctrl+Maj+Suppr` efface l'historique ; un bandeau le confirme.

Les formulaires d'un site Blazor (ADR 0084) ne passent pas par le pont et n'en profitent pas.

## Tests

| Suite | Ce qu'elle prouve |
|---|---|
| `Kazo.Os.AppSdk.Web.Tests/Data/KazoWebDbTests` | générateur de clés, index uniques et `multiEntry`, ordre des clés, plages, transactions atomiques, migrations, `VersionError`, repli en mémoire |
| `Kazo.Os.AppSdk.Web.Tests/Logging/KazoWebDebugLogTests` | journal muet par défaut, lignes de requêtes, `ILogger<T>`, exceptions, niveau minimal |
| `Kazo.Os.Platform.Core.Tests/WebData/WebSiteDatabaseTests` | nom imposé, apps non `Web` refusées, liste et effacement soumis à `system.storage.inspect`, fichier `/var/log/hackweb/<id>.log` |
| `Kazo.Os.Platform.Core.Tests/Apps/AppInstallationServiceTests` | la désinstallation d'un site supprime sa base, et seulement la sienne |
| `Kazo.Os.Apps.Browser.Tests/FormHistoryTests` | fichier de l'utilisateur, tri par usage, limites, cartes refusées, effacement |
| `Kazo.Os.UI.E2E.Tests/BrowserSiteDataTests` | dans Chromium : note de l'Intranet dans IndexedDB, `<datalist>` proposée, ligne du journal lue par `grep`, base listée et effacée par le Diagnostic |

## Décisions clés

- La base passe par une passerelle de l'OS et non par le worker (ADR 0088 §1) : le nom est imposé
  par l'OS, et le Diagnostic comme la désinstallation en ont besoin.
- Transactions par lots, parce qu'une transaction IndexedDB ne survit pas à un aller-retour entre
  le worker et le thread principal.
- L'historique de formulaires est un fichier de l'utilisateur, comme les cookies et le
  `localStorage` du navigateur (ADR 0073 §3).

## Tâches

- [x] ADR 0088.
- [x] Contrats `KazoWebDb*`, moteur en mémoire, passerelle `IAppExecutionContext.WebDatabase`.
- [x] Opérations distantes du worker (`wdb.open`, `wdb.exec`, `log.site`).
- [x] Module `webSiteDatabases.js`, magasin IndexedDB de l'Ecosystem.
- [x] API `KazoWebDb` du SDK web, repli en mémoire hors de l'OS.
- [x] `ILogger<T>` et journal `/var/log/hackweb/<id>.log`.
- [x] Diagnostic : liste et effacement ; désinstallation : suppression annoncée et faite.
- [x] Historique de formulaires du navigateur (`<datalist>`, `Ctrl+Maj+Suppr`).
- [x] Intranet : notes dans `hackweb.org.hackeros.samples.intranet`, filtre `?author=` par l'index, journal activé.
- [x] Tests unitaires et E2E Chromium.
