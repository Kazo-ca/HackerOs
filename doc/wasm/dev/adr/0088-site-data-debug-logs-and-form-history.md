# ADR 0088 : base d'un site, journaux de débogage et saisie semi-automatique

## Statut

Accepté le 2026-10-17, avec le chantier `web/12-site-data`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)), après la clôture
  constatée par l'ADR 0087 §4.
- **Réalise :** ADR 0073 §3 (base `hackweb.<id>`, journaux `/var/log/hackweb/<id>.log`, saisie
  semi-automatique du navigateur).
- **Ajuste :** ADR 0073 §3 sur un point, où la base est ouverte (§1 ci-dessous). Les autres
  décisions de l'ADR 0073 §3 restent telles quelles.
- **Complète :** ADR 0045 (journaux), ADR 0048 (désinstallation), ADR 0056 §11 (diagnostic du
  stockage), ADR 0070 (passerelles distantes), ADR 0075 §3 (pont de page).

## Contexte

L'ADR 0073 §3 a décidé, avec Benoit (16 octobre, 16:44Z), qu'aucune donnée d'un site ne va dans le
système de fichiers de l'OS, sauf ce qui appartient au navigateur : cookies, saisie
semi-automatique et journaux de débogage. Un site qui veut une base utilise l'IndexedDB du
navigateur hôte, par un helper qui impose son nom. L'ADR 0087 §4 constate qu'aucun chantier ne l'a
construit. Ce chantier le construit, et l'exemple Intranet s'en sert.

## Décision

### 1. La base d'un site passe par une passerelle de l'OS

L'ADR 0073 §3 prévoyait d'ouvrir la base depuis le worker web. Elle est ouverte par l'OS, sur le
thread principal, et le site l'atteint par une passerelle de son contexte d'exécution
(`IAppExecutionContext.WebDatabase`), comme le réseau ou le presse-papiers (ADR 0070 §4) :

- **le nom est imposé par l'OS**, pas par le SDK : la passerelle d'un processus ne connaît que
  `hackweb.<id de son app>`. Un site ne peut pas l'éviter en appelant `indexedDB.open` lui-même,
  puisque son code C# n'a pas d'accès JavaScript ;
- l'OS a de toute façon besoin de ces bases (liste du Diagnostic, effacement à la désinstallation) ;
  un seul module JavaScript (`webSiteDatabases.js` d'`Infrastructure.Browser`) les ouvre ;
- le site s'exécute de la même façon dans le worker web et sur le thread principal, et ses tests
  sans OS remplacent la passerelle par une base en mémoire.

Seule une app de genre `Web` reçoit cette passerelle ; les autres apps ont le système de fichiers.
Le module refuse tout nom qui ne commence pas par `hackweb.` : la base de l'OS
(`HackerOsIndexedDb`) reste hors d'atteinte.

### 2. Le modèle IndexedDB, transporté par lots

L'API reprend IndexedDB : versions et étapes de migration (créer ou supprimer un magasin ou un
index), magasins d'objets avec un chemin de clé simple (`id`, `author.name`) ou des clés externes,
`autoIncrement`, index `unique` et `multiEntry`, plages de clés, `get`, `getAll`, `getAllKeys`,
`count`, `add`, `put`, `delete`, `clear`. Les valeurs sont du JSON.

Une transaction IndexedDB se termine dès que son code attend autre chose qu'une requête de la
base ; à travers le worker, un aller-retour par requête la fermerait. Une transaction de
`HackWebDb` est donc un **lot** : le site empile ses écritures, `CommitAsync` les envoie ensemble et
elles réussissent ou échouent ensemble (une seule transaction `readwrite` côté navigateur). Une
lecture est une transaction `readonly` à elle seule.

Les erreurs gardent les noms des `DOMException` d'IndexedDB (`ConstraintError`, `NotFoundError`,
`DataError`, `VersionError`) dans `HackWebDbException.Name`.

### 3. API du SDK web

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

Les types sont lus et écrits avec les options JSON du site (`builder.Json`), donc avec son
contexte généré pour un publish trimé ; les valeurs simples (`string`, nombres, `bool`) n'ont pas
besoin d'y figurer. L'hôte d'un test peut enregistrer `HackWebDbInMemoryBackend` à la place de la
passerelle ; un site construit hors de HackerOS sans passerelle reçoit une base en mémoire
(`hackweb.in-memory`), si bien que ses tests unitaires tournent sans rien configurer.

### 4. Taille, liste et effacement

- Le Diagnostic (`system.storage.inspect`, capacité dangereuse qu'un administrateur approuve)
  liste les bases des sites : nom, app, version, magasins,
  nombre d'enregistrements et taille (octets UTF-8 du JSON des enregistrements, estimation comme
  celle que Chromium affiche par origine). Il peut en supprimer une ; le site la recrée vide, avec
  son schéma, à sa requête suivante.
- La désinstallation d'une app `Web` par `IAppInstallationService.UninstallAsync` supprime sa base,
  après la confirmation des Paramètres, qui l'annonce. C'est la seule donnée qu'une désinstallation
  efface (ADR 0048 garde le reste pour une réinstallation) : la base n'est pas dans les fichiers de
  l'utilisateur, il ne pourrait pas la retrouver. Une désinstallation par édition directe
  d'`apps.config` ne supprime rien.

### 5. Journaux de débogage d'un site

Un site active son journal dans `Configure` : `builder.Logging.EnableDebugLog()`. Il reçoit alors
`ILogger<T>` (`Microsoft.Extensions.Logging`, comme ASP.NET Core) et l'hôte écrit chaque requête
(`Request starting` / `Request finished … in … ms`) et chaque exception non gérée. Les lignes vont
dans `/var/log/hackweb/<id>.log` par une nouvelle méthode de la passerelle de journaux,
`AppendToSiteDebugLog`, réservée aux apps `Web` : le rédacteur de journaux de l'OS écrit le fichier
(ADR 0045), avec la même rotation (1 Mio × 5), en `system:system 0644`. Sans
`EnableDebugLog`, `ILogger<T>` existe mais n'écrit rien. Un retour à la ligne dans un message est
échappé (`\n`), comme pour les journaux de `hackwebd`, pour qu'une ligne ne puisse pas en imiter une
autre.

### 6. Saisie semi-automatique du navigateur

Le navigateur de l'OS retient ce qu'on tape dans les formulaires, comme l'historique de formulaires
de Firefox :

- à l'envoi d'un formulaire, le pont de page transmet les champs `text`, `search`, `email`, `tel`,
  `url` et `number` qui ont un nom et une valeur ; il ignore les mots de passe, les champs cachés,
  `autocomplete="off"` (sur le champ ou le formulaire) et un numéro de carte de paiement ;
- les valeurs sont gardées par nom de champ, pour tous les sites, dans
  `~/.local/share/hackeros/browser/form-history.json` (fichier de l'utilisateur, à côté du
  `localStorage`), au plus 100 valeurs par nom et 2 000 en tout, les moins utilisées partant d'abord ;
- quand un champ reçoit le focus, le pont demande les valeurs de son nom et les propose dans une
  `<datalist>`, la liste déroulante native du navigateur hôte ;
- `Ctrl+Maj+Suppr` efface l'historique de formulaires, comme « Effacer les données de navigation ».

Les formulaires d'un site Blazor sont dessinés dans le DOM de l'OS (ADR 0084), hors du pont : ils
n'ont pas cette saisie semi-automatique.

## Conséquences

- `IAppExecutionContext` gagne `WebDatabase` (défaut « non pris en charge ») ; le worker a deux
  opérations distantes (`wdb.open`, `wdb.exec`) et une notification (`log.site`).
- Les hôtes déclarent `IWebSiteDatabaseStore` : IndexedDB dans le navigateur, mémoire ailleurs.
- L'exemple Intranet garde ses notes dans `hackweb.org.hackeros.samples.intranet` : elles survivent
  à l'arrêt du site et au redémarrage de l'OS. Il active son journal de débogage.
- Un id d'app qui finirait par `.access` ou `.error` partagerait un nom de fichier avec les
  journaux de `hackwebd` (ADR 0080 §3) pour un site hébergé du même nom ; le catalogue ne
  l'interdit pas, la collision est seulement improbable.
- Guide : [`asp-hack-site-data.md`](../../framework/asp-hack-site-data.md).
