> [English version](../../../en/wasm/dev/change-list.md)

# Liste des changements : écrire une entrée

Chaque PR qui change ce que voit un utilisateur ou un auteur d'app ajoute une entrée à la liste
des changements ([ADR 0186](adr/0186-whats-new-change-list.md)). La liste sert à la page
Nouveautés de l'OS, aux notes de la GitHub Release, aux `PackageReleaseNotes` des paquets NuGet
et à `CHANGELOG.md`.

## Où

```
wasm2/HackerOs/changes/
├── unreleased/<guid>.json   un fichier par changement pas encore publié
└── <version>.json           les entrées d'une version publiée (ex. 2.1.0.json)
```

## Ajouter une entrée

1. Créer un GUID en minuscules : `python3 -c "import uuid; print(uuid.uuid4())"`,
   `[guid]::NewGuid()` (PowerShell) ou `uuidgen | tr A-Z a-z`.
2. Créer `wasm2/HackerOs/changes/unreleased/<guid>.json` :

```json
{
  "id": "<guid>",
  "category": "new",
  "audience": ["user"],
  "title": { "en": "Short English title", "fr": "Titre court en français" },
  "description": { "en": "One or two sentences.", "fr": "Une ou deux phrases." },
  "pr": 123,
  "links": ["doc/en/wasm/dev/adr/0186-whats-new-change-list.md"]
}
```

| Champ | Obligatoire | Valeurs |
| --- | --- | --- |
| `id` | oui | Le GUID, identique au nom du fichier. |
| `category` | oui | `new`, `improved`, `fixed`, `developer`. |
| `audience` | oui | `["user"]`, `["developer"]` ou `["user", "developer"]`. Une entrée `developer` a seulement `["developer"]`. |
| `title` | oui | `en` et `fr`, non vides. |
| `description` | non | `en` et `fr`, non vides. |
| `pr` | non | Numéro de la PR (ajouté après son ouverture si on veut). |
| `links` | non | URL `https` ou chemin d'un fichier du dépôt depuis la racine. |

3. Lancer le test : `dotnet test Tests/HackerOs.Ecosystem.Tests --filter ChangeEntriesTests`.

## Bien écrire

- Écrire pour la personne qui lit la page Nouveautés ou les notes de version, pas pour la
  personne qui relit le code : ce qu'elle peut faire de nouveau, ce qui marche mieux.
- Un titre court, une description d'une ou deux phrases.
- Une entrée par changement visible. Une série peut en avoir plusieurs.
- Les changements du SDK ou des outils de build d'apps vont dans une entrée `developer`.

## Quand ne pas en écrire

Une PR qui ne change rien de visible (refactorisation, tests, CI, build interne) porte
l'étiquette **`no-changelog`**. Une PR qui ne touche que `doc/`, `Tests/`, `docs/`, `changes/` ou
des fichiers `.md` n'en a pas besoin.

## Le contrôle de PR

Le workflow **Change list entry** (`.github/workflows/changelog-entry.yml`) tourne sur chaque PR
vers `main` et lance `wasm2/HackerOs/build/tools/check-change-entry.sh <base> <head>`. Il échoue
quand la PR touche `wasm2/HackerOs` (hors `Tests/`, `docs/`, `changes/` et `.md`) sans ajouter de
fichier dans `changes/unreleased/` (ou un nouveau `changes/<version>.json`, voir plus bas), sauf
avec l'étiquette `no-changelog`. Pour le lancer avant de
pousser :

```bash
wasm2/HackerOs/build/tools/check-change-entry.sh origin/main HEAD
```

Dans une série, chaque PR de chantier vers `<série>/base` ajoute son entrée : la PR finale vers
`main` les apporte toutes.

## Dans l'OS : `/whats-new.json`

Au build de `OS/HackerOs.Ecosystem`, l'outil `Tools/HackerOs.Tools.ChangeList` lit `changes/` et
écrit `obj/…/hackeros-whats-new/whats-new.json`, servi à la racine du site comme `/version.json`
(ADR 0186 §4.1). Il ne garde que les entrées dont le public contient `user`, groupées par
version, la plus récente en haut ; les entrées de `unreleased/` forment le premier groupe
(`"version": null`, `"since": "<HackerOsVersion>"`). Dans un groupe, les entrées sont triées par
catégorie (`new`, `improved`, `fixed`, `developer`), puis de la PR la plus récente à la plus
ancienne. Les chemins de `links` deviennent des URL GitHub et `pr` donne aussi `prUrl`.

Pour voir le fichier sans construire l'OS :

```bash
dotnet run --project wasm2/HackerOs/Tools/HackerOs.Tools.ChangeList -- \
  site --changes wasm2/HackerOs/changes --version 2.1.0 --output /tmp/whats-new.json
```

Côté OS :

- `WhatsNewService` (Platform.Core, `HackerOs.Platform.Core.WhatsNew`) lit le fichier une fois par
  page (un nouveau déploiement recharge la page) et garde, pour chaque compte, les IDs déjà vus
  dans `~/.local/state/hackeros/whats-new.json` : `{ "seen": [ "<id>", … ] }`. Un ID qui sort de
  la liste reste dans le fichier : une entrée ne redevient jamais nouvelle.
- **Nouveau compte** (ADR 0186 §5) : quand ce fichier n'existe pas encore (ou ne se lit pas), tout
  est marqué vu sauf les entrées de la dernière version publiée et celles de la prochaine version
  déjà déployée, et le fichier est écrit tout de suite. Sans liste servie (hors ligne), rien
  n'est écrit : la règle attend une liste.
- Les apps lisent la liste par le SDK, sans capacité : `HackWhatsNew.GetAsync()` (textes dans la
  langue de l'app, repli sur l'anglais), `MarkSeenAsync(ids)` et `WatchAsync` (voir
  [`sdk-reference.md`](../framework/sdk-reference.md)).

Ce que voit l'utilisateur (ADR 0186 §4.4 et §4.5) :

- **Paramètres ▸ Nouveautés** (`--page=whats-new`, aussi par le bouton **Quoi de neuf ?** de
  À propos) : un groupe par version, la prochaine en premier (« Depuis 2.1.0 (prochaine
  version) »), une étiquette de catégorie et un badge **Nouveau** sur ce qui n'a pas été vu, les
  liens « En savoir plus » et « Pull request ». Les badges restent pendant la visite ; les
  entrées deviennent vues quand la page se ferme (autre page choisie ou Paramètres fermé).
- **Notification après la connexion**, une fois par compte et par chargement de la page, s'il
  reste des entrées non vues : « 3 nouveautés depuis votre dernière visite », bouton **Voir** qui
  ouvre la page. Elle est dans le panneau de l'horloge. Le bouton d'une notification peut
  maintenant passer des arguments à l'app : `NotificationAction.LaunchApp(appId, label, arguments)`.
- **Point des notifications non lues** à côté de l'horloge (bureau) et sur la poignée du volet de
  notifications (mobile), avec le nombre dans l'infobulle ; ouvrir le panneau les marque lues
  ([ADR 0196](adr/0196-unread-notifications-dot.md), qui remplace la pastille du bouton Démarrer).
  Quand toutes les entrées sont vues, la notification des nouveautés se ferme seule.

## À la sortie d'une version

Dans la PR qui change `build/Versions.props`, lancer :

```bash
wasm2/HackerOs/build/tools/collect-changes.sh            # date du jour (UTC)
wasm2/HackerOs/build/tools/collect-changes.sh --date 2026-11-02
```

Le script (outil `Tools/HackerOs.Tools.ChangeList`, commande `collect`) :

1. déplace toutes les entrées de `unreleased/` dans `changes/<version>.json`, la version étant
   celle de `Versions.props`, sans changer leur contenu ni leurs IDs (les badges **Nouveau** déjà
   vus le restent), puis supprime les fichiers de `unreleased/` ;
2. régénère `CHANGELOG.md` (anglais) et `CHANGELOG-fr.md` (français) à la racine du dépôt.

Il refuse de travailler si `changes/<version>.json` existe déjà ou si `unreleased/` est vide.
`--changelog-only` régénère seulement les deux journaux. Le contrôle de PR accepte un nouveau
`changes/<version>.json` comme entrée.

Ensuite, à la fusion dans `main`, sans rien faire de plus :

- **GitHub Release** : `release.yml` écrit le texte avec la commande `notes --kind release` :
  toutes les entrées de la version, en anglais, groupées par catégorie, avec les liens vers les PR
  et la doc. Si `changes/<version>.json` manque, GitHub génère les notes comme avant, avec un
  avertissement.
- **NuGet** : `build/pack-local.sh` écrit `artifacts/package-release-notes.txt` (commande
  `notes --kind package`) et le passe à `dotnet pack` ; `Platform/Packaging.props` le met dans
  `PackageReleaseNotes` de chaque paquet `HackerOs.*` : les entrées `developer` de la version et le
  lien vers la Release. Avant la collecte (paquet local), les entrées de `unreleased/` servent.
- **Journaux** : `CHANGELOG.md` et `CHANGELOG-fr.md` ne listent que les versions publiées. Le test
  `ReleaseNotesTests.The_changelogs_of_the_repository_are_up_to_date` échoue s'ils ne
  correspondent plus à `changes/` : on ne les modifie jamais à la main.
