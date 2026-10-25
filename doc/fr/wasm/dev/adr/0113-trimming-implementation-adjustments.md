# ADR 0113 : ajustements de la publication trimmée

## Statut

Accepté le 2026-10-17 (chantier `v3/14-trimming`). Une question reste posée à
Benoit dans le fil du chantier (§1) ; ce qui suit applique la réponse
recommandée en attendant.

- **Ajuste :** ADR 0106 §1, §2 et §3 (publication Release trimmée de l'hôte
  Ecosystem).
- **Document :** [`trimmed-publish.md`](../trimmed-publish.md).

## Contexte

L'ADR 0106 prévoyait d'élaguer « nos » assemblies (celles marquées
`IsTrimmable`) en gardant entiers les paquets tiers, et de ne garder dans chaque
app que les types que l'OS atteint par leur nom (point d'entrée, `AppDefinition`,
composants intégrables, contrôleurs, vues, composants routables).

La mise en œuvre a montré trois choses :

1. **`TrimMode=partial` élague aussi le framework.** Les assemblies de .NET et
   d'ASP.NET Core portent l'attribut `IsTrimmable` : ce sont elles qui pèsent.
   Sur l'Ecosystem, mesuré avec `build/publish-trimmed.sh --compare` :

   | Publication | Fichiers `.wasm` | Taille | Brotli |
   |---|---|---|---|
   | non trimmée | 273 | 50,9 Mio | 13,5 Mio |
   | trimmée, assemblies HackerOS élaguées membre par membre | 128 | 25,1 Mio | 7,6 Mio |
   | trimmée, assemblies HackerOS entières (retenu) | 134 | 26,5 Mio | 8,0 Mio |

   Élaguer nos propres assemblies ne rapporte que 1,4 Mio (0,4 Mio en Brotli) :
   leur poids est surtout fait de ressources (icônes, `wwwroot` embarqué des
   sites, traductions).
2. **Élaguer nos assemblies casse le démarrage, sans aucun avertissement.** Le
   code passe ses propres records à l'interop JavaScript
   (`IJSRuntime.InvokeVoidAsync(..., params object[] args)`), qui les sérialise
   par réflexion. Ce paramètre n'est pas annoté pour le trimmer : l'analyse ne
   voit rien, et la publication supprime les noms des paramètres du
   constructeur et les accesseurs que seule la sérialisation lit. Premier
   symptôme : `IndexedDbMigrationPlan` ne se sérialise plus
   (`ConstructorContainsNullParameterNames`) et l'OS s'arrête sur « Local
   storage is unavailable ». Il y a près de 280 appels d'interop dans la
   solution ; les corriger un par un pour 0,4 Mio n'en vaut pas le risque.
3. **Les apps ne sont référencées par aucun code.** Sans racine, le trimmer
   supprime toutes les assemblies d'apps, et la publication échoue
   (`BLAZORSDK1001`, assemblies à charger paresseusement introuvables).

## Décision

1. **Toutes les assemblies HackerOS restent entières.** Une cible de
   `build/HackerOs.Apps.targets` (`_HackerOsRootHackerOsAssemblies`, hôtes
   seulement, quand `PublishTrimmed` vaut `true`) ajoute chaque assembly
   `HackerOs.*` de la publication en `TrimmerRootAssembly`. Le framework et les
   autres bibliothèques élaguables sont élagués ; les paquets non élaguables
   (MudBlazor, AngleSharp…) restent entiers (`TrimMode=partial`, inchangé). Ceci
   remplace « seules nos assemblies sont élaguées » de l'ADR 0106 §1. Passer à
   un élagage de nos assemblies demanderait d'abord de rendre toute l'interop
   JavaScript sûre (sérialisation générée) : un nouvel ADR, avec des mesures.
2. **Racines générées : des assemblies entières.** Le générateur de manifestes
   (`aggregate --trimmer-roots`) écrit `HackerOs.TrimmerRoots.xml` à côté de
   `BuiltInApplications.json` ; l'hôte le passe à ILLink
   (`TrimmerRootDescriptor`). Chaque assembly nommée par un manifeste (point
   d'entrée, entrées par plateforme, composants, racine Blazor d'un site) y est
   gardée entière (`preserve="all"`), pas seulement les types nommés. C'est ce
   qui garde une app d'un autre dépôt qui ne s'appelle pas `HackerOs.*` (§1 ne
   la couvre pas). La classe `AppDefinition` n'a pas besoin de racine : l'OS ne
   l'instancie jamais (le manifeste vient du JSON, le `WebHost` généré d'un site
   l'appelle par du code). Ceci remplace le détail de l'ADR 0106 §2.
3. **Pas de table générée pour les pages Blazor des sites.** L'ADR 0106 §3
   demandait de remplacer `GetExportedTypes` là où une table générée existe.
   Le seul `GetExportedTypes` restant (`HackSiteRouter`, pages `[Route]` d'un
   site Blazor) n'a pas de table : l'assembly du site étant gardée entière, il
   reste sûr, comme le routeur de Blazor l'est pour l'assembly principale.
   Les routes et vues MVC passaient déjà par les tables générées
   (ADR 0082, 0083, 0086). Les suppressions d'avertissements restantes citent
   maintenant cet ADR au lieu de « PublishTrimmed est désactivé ».
4. **Test de la publication.** `build/publish-trimmed.sh` publie, mesure, sert
   la publication et lance `Tests/HackerOs.TrimmedPublish.E2E.Tests` (connexion,
   chaque app fenêtrée du menu, une commande dans un worker, l'Intranet et le
   site Blazor, puis démarrage hors ligne). Le service worker n'est enregistré
   ni sur `localhost` ni sur `127.0.0.1` (`index.html`) : le test hors ligne
   passe par un second serveur sur `127.0.0.2`, adresse de bouclage que
   Chromium traite comme sûre, comme la suite PWA le fait déjà sans IPv6.
5. **Conversion WebCIL incrémentale.** Le SDK reconvertit une assembly en
   WebCIL selon les dates des fichiers : une publication non trimmée faite après
   une publication trimmée dans le même `obj/` peut reprendre les fichiers
   élagués (constaté : MudBlazor absent, CoreLib élaguée). Le script repart d'un
   état propre (`obj/.../linked`, `webcil`, `tmp-webcil`) avant chaque
   publication ; `building-and-testing.md` le signale aux développeurs.

## Conséquences

- La publication de l'Ecosystem passe de 50,9 à 26,5 Mio de WebAssembly
  (13,5 à 8,0 Mio en Brotli). Le premier chargement télécharge moins, et chaque
  worker démarre un runtime plus petit.
- Les commutateurs de fonctionnalité de la mesure des ressources (ADR 0066) ne
  suppriment pas de code à l'intérieur de `HackerOs.Platform.ResourceAccounting`,
  gardée entière ; avec les deux mesures coupées au build, l'assembly n'est
  toujours pas publiée.
- Le gabarit `hackeros-host` publie trimmé de la même façon (`PublishTrimmed`,
  `TrimMode=partial`) ; les gabarits d'apps partent avec `IsTrimmable=true`, et
  `HackerOs.App.Build` active l'analyse dans un projet d'app qui ne la règle pas
  lui-même (ADR 0106 §4, inchangé).
- `verify-hosts.sh` publie l'Ecosystem comme avant : il teste donc désormais la
  publication trimmée.
