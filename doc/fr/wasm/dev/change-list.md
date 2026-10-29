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
fichier dans `changes/unreleased/`, sauf avec l'étiquette `no-changelog`. Pour le lancer avant de
pousser :

```bash
wasm2/HackerOs/build/tools/check-change-entry.sh origin/main HEAD
```

Dans une série, chaque PR de chantier vers `<série>/base` ajoute son entrée : la PR finale vers
`main` les apporte toutes.

## À la sortie d'une version

Les entrées de `unreleased/` sont regroupées dans `changes/<version>.json` dans la PR qui change
`build/Versions.props` (script `build/tools/collect-changes.sh`, à venir avec le chantier 4 de
l'ADR 0186). Jusque-là, on le fait à la main : créer
`{ "version": "x.y.z", "date": "aaaa-mm-jj", "entries": [ … ] }` avec les entrées, sans changer
leurs IDs, et supprimer les fichiers de `unreleased/`.
