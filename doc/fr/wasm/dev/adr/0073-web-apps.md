> [English version](../../../../en/wasm/dev/adr/0073-web-apps.md)

# ADR 0073 : apps web (genre `Web`), sites externes et sites hébergés

## Statut

Accepté le 2026-10-16 (proposé le même jour, puis validé par Benoit avec le
questionnaire de la section 4 du plan).

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** ADR 0047 (manifeste en classe C#), ADR 0049 (capacités),
  ADR 0070 (hôte d'exécution).

## Contexte

Benoit veut des sites web internes : des projets C# façon ASP.NET MVC, des sites
Blazor interactifs et des sites statiques. Aujourd'hui un site simulé est un
contrôleur enregistré au démarrage de l'hôte, sans processus ni manifeste
(`ISimulatedWebsiteController`). Il faut dire ce qu'est un site du point de vue
du modèle d'app, d'où viennent ses fichiers et à quoi il a droit.

## Décision

### 1. Nouveau genre d'app `Web`

`AppKind` gagne `Web` (`"web"` dans `manifest.json`). La définition dérive de
`WebAppDefinition` (bibliothèque `HackerOs.AppSdk.Web`) :

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

- `Urls` est obligatoire et généré dans le manifeste (champ `web.urls`) ; le
  résolveur de l'OS le lit (ADR 0074). Un nom pris par deux apps est une erreur
  de catalogue (`catalog.web.url-conflict`).
- Un site peut être MVC seul, Blazor seul, ou les deux : les routes MVC passent
  d'abord, le reste va au composant Blazor.
- Hôte d'exécution (ADR 0070) : le code serveur de **tous** les sites (MVC,
  routes, vues) tourne dans **un seul worker partagé**, le worker web, comme un
  pool d'applications IIS. Chaque site reste un processus distinct (PID, ligne
  du gestionnaire de tâches, arrêt propre par `SIGTERM`), mais une boucle infinie
  dans un site bloque tous les sites, pas l'OS ; « Force kill » d'un site qui ne
  répond pas termine le worker web et donc tous les sites, qui redémarrent à la
  requête suivante (activation à la demande, ADR 0074 §3). Le worker web
  démarre avec le premier site appelé. La partie Blazor interactive d'un site
  tourne sur le thread principal, où le navigateur la dessine (§4).

### 2. Deux sortes de sites, selon d'où viennent les fichiers

| Sorte | Origine | Adresse | Fichiers |
|---|---|---|---|
| **Externe** | un projet C# (app `Web` installée) | les `Urls` de son manifeste (`intranet.hack`) | `wwwroot` du projet, embarqué dans l'app, **lecture seule** ; l'utilisateur le consulte comme un site distant |
| **Hébergé** | des fichiers de l'OS, déclarés par l'utilisateur (ADR 0077) | `http://localhost:<port>/`, plus un nom de `/etc/hosts` s'il le veut | dossier du système de fichiers, modifiable |

Les sites enregistrés statiquement (HackMail, HackerSearch...) restent pris en
charge tels quels, comme sites externes sans processus.

### 3. Capacités d'un site

- Un site n'a **aucune** capacité `filesystem.*` : ses données ne sont pas dans
  le système de fichiers de l'OS. Le générateur refuse une `WebAppDefinition`
  qui en demande (`HOSAPP0050`). Il voit seulement ses ressources embarquées.
- S'il a besoin d'une base de données, il utilise la base locale du navigateur
  hôte (IndexedDB, comme le système de fichiers de l'OS lui-même), par un helper
  du SDK web qui impose un nom de base dérivé de l'identifiant de l'app
  (`hackweb.<id>`) : un site ne peut ni choisir ni ouvrir la base d'un autre.
  L'API est calquée sur IndexedDB (magasins d'objets, index, transactions,
  versions), avec un helper typé par-dessus (`HackWebDb`,
  `store.GetAsync<Note>(id)`, `PutAsync`, `DeleteAsync`, requêtes par index).
  IndexedDB étant disponible dans un Web Worker, la base s'ouvre depuis le
  worker web.
- La base d'un site est effacée avec l'app à la désinstallation, après
  confirmation. L'app Diagnostic liste les bases des sites (nom, taille) et
  permet de les vider ; elles n'apparaissent pas dans les fichiers de l'OS.
- Seules les données propres au navigateur vont dans le système de fichiers de
  l'OS : cookies, saisie semi-automatique, et les journaux d'un site quand le
  site les active pour le débogage (`/var/log/hackweb/<id>.log`).
- `network.listen` lui est accordé d'office sur ses propres `Urls` ;
  `network.simulated.read`/`write` sont normales ; `network.real.access` reste
  dangereuse.

### 4. Site Blazor interactif

Un site Blazor ressemble à une app à fenêtre, mais son composant racine est
dessiné dans la zone de page du navigateur HackerOS au lieu d'une fenêtre. Il
navigue par l'URL de l'onglet (`NavigationManager` propre au site, relié à la
barre d'adresse). Il ne reçoit pas les services de fenêtre (`WindowAppBase`) ni
aucune capacité `filesystem.*`. Le composant est isolé par le CSS scopé ; le navigateur ne
lui transmet aucun service de l'OS hors du SDK permis.

### 5. Ouverture vers la compilation dans l'OS

Le catalogue ne suppose pas qu'une app `Web` vient de la solution : un futur
compilateur dans l'OS produira la même `WebAppDefinition` et le même manifeste,
installés par `IAppInstallationService`.

## Conséquences

- Le générateur de manifestes, le schéma et `AppCatalog` connaissent `Web` et
  `web.urls`.
- Le navigateur devient l'hôte d'affichage des sites Blazor.
- Les apps existantes ne changent pas.
