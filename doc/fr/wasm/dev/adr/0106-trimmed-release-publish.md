> [English version](../../../../en/wasm/dev/adr/0106-trimmed-release-publish.md)

# ADR 0106 : publication Release trimmée de l'hôte Ecosystem

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/14-trimming`).

- **Complète :** ADR 0047 (manifestes générés, chargement paresseux des apps),
  ADR 0070 (processus dans des workers), ADR 0082 (table de routes ASP.Hack
  générée), ADR 0084 (sites Blazor).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/14-trimming` ; manque R12. AOT hors périmètre.

## Contexte

- `HackerOs.Ecosystem.csproj:12` a `PublishTrimmed=False`. Pourtant
  `Directory.Build.props` met `IsTrimmable=true` et `EnableTrimAnalyzer=true`
  sur tous les projets hors tests (sauf le serveur et le support E2E), et
  `TreatWarningsAsErrors` est actif : l'analyse tourne, le trimming jamais.
- Les apps intégrées sont chargées paresseusement (`BlazorWebAssemblyLazyLoad`,
  `build/HackerOs.Apps.targets:310-333`) puis instanciées par réflexion :
  `Activator.CreateInstance` du point d'entrée (`AppLifecycleOrchestrator.cs:136-144`),
  type cherché par son nom (`AppEntryPointDiscovery.cs:99`), chargement par nom
  dans les workers (`WorkerAssemblyLoader.cs:38-47`, `WorkerProcessHost.cs:295-300`),
  types exportés des sites (`HackSiteRouter.cs:231`), liaison de modèle MVC
  (`HackModelBinder.cs`). Plusieurs avertissements de trimming y sont supprimés
  par attribut.
- Ces chemins ne cassent qu'en Release trimmée, jamais en Debug.

## Décision

1. **`PublishTrimmed=True` et `TrimMode=partial`** pour l'Ecosystem : seules les
   assemblies marquées `IsTrimmable` (les nôtres) sont élaguées ; les paquets
   tiers (MudBlazor, Monaco, etc.) restent entiers. Passer en `full` demandera un
   nouvel ADR, avec des mesures.
2. **Racines générées, pas de suppression aveugle.** Les cibles de l'hôte
   génèrent, depuis `BuiltInApplications.json`, un descripteur ILLink
   (`TrimmerRootDescriptor`) qui garde pour chaque app son point d'entrée, sa
   classe `AppDefinition`, ses composants intégrables (ADR 0063) et, pour un
   site, ses contrôleurs, vues et composants routables. Les types ainsi
   construits par réflexion sont donc toujours présents.
3. Là où une table générée existe déjà (routes et vues ASP.Hack, ADR 0082,
   0083, 0086), le code l'utilise au lieu de parcourir `GetExportedTypes`. Une
   suppression d'avertissement qui reste porte un commentaire qui dit quelle
   racine la rend sûre ; une suppression sans justification est un défaut.
4. Une app d'un autre dépôt (paquet, ADR 0098) est élaguée à la publication de
   l'hôte qui l'inclut : le paquet `HackerOs.App.Build` active l'analyse de
   trimming dans les projets d'app, et les gabarits partent avec
   `IsTrimmable=true`.
5. **Test** : `build/publish-trimmed.sh` publie l'Ecosystem en Release trimmée
   et une suite Playwright la sert et vérifie le démarrage, la connexion, le
   lancement de chaque app système, une commande dans un worker, un site
   ASP.Hack (exemple Intranet) et un site Blazor, hors ligne après le premier
   chargement. Cette suite fait partie de la porte de fusion locale de `v3/14`
   et des chantiers suivants.
6. Le serveur (`IsTrimmable=false`) et l'hôte de test ne sont pas élagués.

## Conséquences

- `v3/14` corrige les avertissements de trimming de tous les projets livrés,
  ajoute le descripteur généré, le script et la suite E2E, et note la taille de
  la publication avant et après dans `building-and-testing.md`.
- Le chantier vient après les chantiers qui ajoutent des API (plan, §4), pour ne
  pas refaire les racines à chaque ajout.
- `building-and-testing.md` et `project-overview.md` sont mis à jour.
