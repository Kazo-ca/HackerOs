# Publication Release trimmée

## Objet

La publication Release de l'hôte Ecosystem (`OS/HackerOs.Ecosystem`) est
élaguée par ILLink (chantier `v3/14-trimming`, ADR 0106 et 0113) : le code du
framework .NET que HackerOS n'utilise pas est retiré des fichiers
WebAssembly. Le premier chargement télécharge moins, et chaque Web Worker
(ADR 0070) démarre un runtime plus petit.

| Publication de l'Ecosystem | Fichiers `.wasm` | Taille | Brotli |
|---|---|---|---|
| avant (`PublishTrimmed=false`) | 273 | 50,9 Mio | 13,5 Mio |
| après (trimmée) | 134 | 26,5 Mio | 8,0 Mio |

Mesuré le 2026-10-17 avec `build/publish-trimmed.sh --compare` (fichiers des
satellites `fr/` compris). L'AOT est hors périmètre.

## Architecture

```
Ecosystem.csproj            PublishTrimmed=true, TrimMode=partial
        │
        ├─ CollectHackerOsBuiltInApplications (build/HackerOs.Apps.targets)
        │     ManifestGenerator aggregate --trimmer-roots
        │       → obj/.../BuiltInApplications.json
        │       → obj/.../HackerOs.TrimmerRoots.xml  ──► TrimmerRootDescriptor
        │
        └─ _HackerOsRootHackerOsAssemblies (avant PrepareForILLink)
              chaque HackerOs.*.dll publiée ──► TrimmerRootAssembly
                                    │
                              ILLink (publish)
            framework et bibliothèques élaguables : élagués
            assemblies HackerOS et apps : entières
            paquets non élaguables (MudBlazor…) : entiers (partial)
```

- **Pourquoi des racines.** L'OS n'appelle jamais le code d'une app
  directement : il charge son assembly à la demande
  (`BlazorWebAssemblyLazyLoad`) et trouve son point d'entrée, ses composants
  intégrables et la racine Blazor d'un site par les noms du manifeste
  (`Assembly.GetType`, `Activator.CreateInstance`, chargement par nom dans les
  workers). Sans racine, ILLink supprime ces assemblies.
- **Pourquoi des assemblies entières.** Le code de HackerOS passe ses records à
  l'interop JavaScript, qui les sérialise par réflexion sans que l'analyse de
  trimming le voie. Un élagage membre par membre casse le démarrage (stockage
  « unavailable ») pour seulement 0,4 Mio de Brotli gagné (ADR 0113 §1).
- **`HackerOs.TrimmerRoots.xml`** (`TrimmerRootDescriptorWriter` du générateur
  de manifestes) : une ligne `<assembly fullname="…" preserve="all" />` par
  assembly que nomme un manifeste (point d'entrée, entrées par plateforme,
  composants, `web.blazor`). Il couvre les apps installées en paquets, même
  quand leur nom ne commence pas par `HackerOs.`.
- **Suppressions d'avertissements.** Les attributs `UnconditionalSuppressMessage`
  restants (découverte des apps, workers, composants intégrables, routeur des
  sites Blazor, liaison de modèle MVC) citent la racine qui les rend sûrs.

## Utilisation

### Publier et tester

```bash
wasm2/HackerOs/build/publish-trimmed.sh            # publie, mesure, sert, teste
wasm2/HackerOs/build/publish-trimmed.sh --compare  # publie aussi sans trimming et compare
wasm2/HackerOs/build/publish-trimmed.sh --skip-build
```

Le script publie dans `artifacts/publish-trimmed/publish`, échoue sur toute
alerte `IL…`, sert la publication sur `127.0.0.1` et `127.0.0.2`, puis lance
`Tests/HackerOs.TrimmedPublish.E2E.Tests` :

| Test | Ce qu'il vérifie |
|---|---|
| `Trimmed_publish_runs_every_app_a_worker_command_and_both_sample_sites` | création du profil ; chaque app fenêtrée du menu Démarrer (lue dans `BuiltInApplications.json`, approbation administrateur comprise) ouvre sa fenêtre sans erreur ; `seq 30 \| grep 1 \| wc -l` et `/proc/hackeros/workers` dans le terminal ; l'Intranet (vue `.cshtml`, script, API REST, tableau Blazor) et le site Blazor d'exemple (compteur, lien, API) dans le navigateur |
| `Trimmed_publish_boots_offline_after_the_first_load` | sur `127.0.0.2` (le service worker n'est pas enregistré sur `127.0.0.1`) : mise en cache de la version, rechargement sans réseau, connexion, lancement d'une app jamais chargée |
| `The_calculator_rendered_in_a_worker_works_like_on_the_page` | essai de l'ADR 0133, réglage activé par une réécriture d'`appsettings.json` : la Calculatrice dans un worker calcule à la souris et au clavier, garde son CSS, `Tab`, axe, `Alt+F7`/`Alt+Q`, worker dédié libéré à la fermeture ([`remote-window-rendering.md`](remote-window-rendering.md)) |
| `Measures_the_calculator_on_the_page_and_in_a_worker` | mesures de l'ADR 0133 (ouverture, latence, mémoire, lots), écrites dans `remote-window-measurements.json` du dossier des artefacts |

Le script exige que les quatre tests passent (`EXPECTED_TESTS`).

Aucune erreur de console n'est admise. Sans `HACKEROS_TRIMMED_URL`, la suite est
ignorée (un `dotnet test` de la solution ne la lance pas).

Ce test fait partie de la porte de fusion locale de `v3/14` et des chantiers
suivants (ADR 0106 §5). `build/verify-hosts.sh` publie aussi l'Ecosystem en
Release : son test de démarrage porte donc lui aussi sur la publication trimmée.

### Un hôte d'un autre dépôt

Le gabarit `hackeros-host` publie trimmé de la même façon. Les cibles de
`HackerOs.App.Build` génèrent les racines et gardent les paquets `HackerOs.*`
entiers ; les gabarits d'apps partent avec `IsTrimmable=true`, et le paquet
active l'analyse de trimming dans un projet d'app qui ne la règle pas.

### Piège : publications successives

La conversion WebCIL du SDK est incrémentale par dates de fichiers. Une
publication non trimmée (`-p:PublishTrimmed=false`) faite après une publication
trimmée dans le même dossier `obj/` peut reprendre les assemblies élaguées.
Avant de comparer, supprimer `OS/HackerOs.Ecosystem/obj/Release/net10.0/linked`,
`webcil` et `tmp-webcil` (le script le fait).

## Décisions clés

- `TrimMode=partial` (ADR 0106 §1) : les paquets qui ne se déclarent pas
  élaguables restent entiers.
- Assemblies HackerOS et apps gardées entières ; seul le framework est élagué
  (ADR 0113 §1-2).
- Pas de table générée pour les pages `[Route]` d'un site Blazor : l'assembly du
  site est entière (ADR 0113 §3).
- Test hors ligne sur `127.0.0.2` plutôt que `[::1]` (ADR 0113 §4).

## Tâches

- [x] `PublishTrimmed=true` et `TrimMode=partial` dans l'Ecosystem et le gabarit d'hôte.
- [x] `HackerOs.TrimmerRoots.xml` généré par l'agrégation et passé à ILLink.
- [x] Assemblies `HackerOs.*` gardées entières à la publication d'un hôte.
- [x] Justifications des suppressions d'avertissements mises à jour.
- [x] `HackerOs.App.Build` : analyse de trimming dans les projets d'apps ; gabarits `IsTrimmable=true`.
- [x] `build/publish-trimmed.sh` et `Tests/HackerOs.TrimmedPublish.E2E.Tests`.
- [x] Tailles avant et après mesurées (ici et dans `building-and-testing.md`).
- [x] ADR 0113.
