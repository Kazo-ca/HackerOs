> [English version](../../../../en/wasm/dev/adr/0186-whats-new-change-list.md)

# ADR 0186 : liste des changements et nouveautés depuis la dernière visite

## Statut

Accepté le 2026-10-29. Décisions prises avec Benoit dans le fil « Afficher les nouveautés
depuis la dernière visite » (12 questions posées le 2026-10-28, réponses le 2026-10-29 :
Q2 = C, Q8 = B, les autres selon la recommandation). Plan :
https://claude.ai/artifact/Q9BdEs6HxUCjiD1os1TPNq. Suivi : issue TODO
[#252](https://github.com/Kazo-ca/HackerOs/issues/252).

Mis en œuvre par chantiers (§9). Le chantier 1 (format, test, entrées de 2.1.0 et de la
prochaine version) et le chantier 5 (contrôle de PR) arrivent avec cette ADR.

- **Complète :** [ADR 0173](0173-automatic-releases-from-the-version-file.md) (publication
  automatique à partir de `Versions.props`) et
  [ADR 0174](0174-announce-a-new-version-to-open-sessions.md) (`/version.json` et bandeau de
  nouvelle version).

## Contexte

Benoit veut voir dans l'OS ce qui a changé depuis sa dernière visite, groupé par version, avec un
badge **Nouveau**, et réutiliser la même liste pour les notes des publications (GitHub Release,
NuGet).

État au moment de la décision (main `0673428f`) :

- La seule version est `HackerOsVersion` dans `wasm2/HackerOs/build/Versions.props` (2.1.0).
- `release.yml` publie des notes générées automatiquement par GitHub (titres de PR).
- `/version.json` sert seulement au bandeau « redémarrer pour mettre à jour » (ADR 0174).
- Pages déploie `main` à chaque fusion, alors que la version change plus rarement : une
  fonctionnalité est en ligne avant d'avoir un numéro de version.
- Plusieurs fils travaillent en parallèle et fusionnent souvent.
- L'interface de l'OS est bilingue (`Strings.resx` et `Strings.fr.resx`).

## Décision

### 1. Un fichier par changement (Q1 : A)

- Chaque changement est un petit fichier `wasm2/HackerOs/changes/unreleased/<id>.json`. Deux PR
  ouvertes en même temps n'écrivent jamais dans le même fichier : pas de conflit à la fusion.
- À la sortie d'une version, toutes les entrées de `unreleased/` sont regroupées dans
  `wasm2/HackerOs/changes/<version>.json`, avec la date de la version, dans la PR qui change
  `Versions.props` (script `build/tools/collect-changes.sh`, chantier 4, comme
  `ship-public-api.sh` pour l'API publique).
- L'ID d'une entrée ne change jamais, même quand elle passe dans un fichier de version.

### 2. Format d'une entrée (Q2 : C, Q3 à Q5 : A)

```json
{
  "id": "44aa95c2-364a-41b2-8610-a7ad3a8f70fa",
  "category": "new",
  "audience": ["user"],
  "title": { "en": "Mount a computer folder as a network drive", "fr": "Monter un dossier de l'ordinateur comme lecteur réseau" },
  "description": { "en": "…", "fr": "…" },
  "pr": 238,
  "links": ["doc/en/wasm/dev/adr/0181-host-folder-mounted-as-a-network-drive.md"]
}
```

- `id` : un **GUID** en minuscules (choix de Benoit). Le nom du fichier dans `unreleased/` est
  l'ID. Comme un GUID n'est pas lisible, le titre anglais sert de repère dans les revues.
- `category` : `new` (nouveau), `improved` (amélioré), `fixed` (corrigé) ou `developer`
  (développeurs / SDK).
- `audience` : `user`, `developer`, ou les deux. Une entrée `developer` n'a que le public
  `developer`. L'OS montre les entrées `user`, NuGet les entrées `developer`, la GitHub Release
  et `CHANGELOG.md` montrent tout.
- `title` (obligatoire) et `description` (facultative) ont chacun un texte `en` et un texte `fr`.
  L'OS affiche la langue de l'utilisateur, avec repli sur l'anglais. GitHub et NuGet reçoivent
  l'anglais.
- `pr` (numéro de PR) et `links` (URL `https` ou chemin d'un fichier du dépôt, à partir de la
  racine) sont facultatifs.
- Aucune autre propriété n'est permise.

Un fichier de version a la forme :

```json
{ "version": "2.1.0", "date": "2026-10-26", "entries": [ … ] }
```

Le test `ChangeEntriesTests` (`Tests/HackerOs.Ecosystem.Tests`) vérifie tout cela : schéma, GUID
valide et unique dans tout `changes/`, nom de fichier égal à l'ID ou à la version, catégorie et
public connus, textes non vides dans les deux langues, liens vers des fichiers qui existent, et
aucun fichier de version plus récent que `HackerOsVersion`.

### 3. Changements déployés avant leur version (Q6 : A)

Les entrées de `unreleased/` sont affichées dès leur déploiement, sous un groupe « Depuis 2.1.0
(prochaine version) ». À la sortie de la version, elles sont simplement rangées sous le nouveau
numéro. Le badge **Nouveau** est conservé, car l'état « vu » suit l'ID et non la version.

### 4. Côté OS (Q7, Q9, Q10 : A)

1. **`/whats-new.json`** : le build de `OS/HackerOs.Ecosystem` assemble `changes/*.json` et
   `changes/unreleased/` en un seul fichier servi à la racine du site, comme `/version.json`
   (ADR 0174). Seules les entrées dont le public contient `user` y vont.
2. **Service des nouveautés** (Platform.Core) : il lit ce fichier et l'état du compte, puis
   calcule les entrées non vues.
3. **État par compte**, dans un vrai fichier : `~/.local/state/hackeros/whats-new.json`, qui
   contient la liste des IDs vus.
4. **Fin du badge** : quand la page des nouveautés a été ouverte, les entrées affichées gardent
   leur badge pendant la visite et deviennent « vues » à la fermeture de la page.
5. **Découverte** : après la connexion, s'il reste des entrées non vues, une notification
   (« 3 nouveautés depuis votre dernière visite », bouton **Voir**). La liste est une page
   Paramètres ▸ À propos ▸ Nouveautés : un groupe par version, la plus récente en haut, les
   entrées triées par catégorie. Une pastille reste sur le menu Démarrer tant qu'il reste des
   entrées non vues.

### 5. Nouveau compte (Q8 : B)

Sans règle, un nouveau compte verrait tout l'historique en « Nouveau ». Choix de Benoit : un
nouveau compte voit les nouveautés de la **dernière version**. Au premier démarrage du compte, le
service marque comme vu tout ce qui existe, **sauf** les entrées de la dernière version publiée
et celles de la prochaine version déjà déployées (`unreleased/`).

La part « prochaine version déjà déployée » est notre lecture de la réponse ; elle est soumise à
Benoit et sera corrigée ici s'il préfère seulement la version publiée.

### 6. Contrôle de PR (Q12 : A)

- Le workflow **Change list entry** (`.github/workflows/changelog-entry.yml`) tourne sur toute PR
  vers `main`. Il échoue si la PR touche le produit (`wasm2/HackerOs` hors `Tests/`, `docs/`,
  `changes/` et fichiers `.md`) sans **ajouter** de fichier dans `changes/unreleased/`, sauf si la
  PR porte l'étiquette **`no-changelog`**. La règle est dans
  `wasm2/HackerOs/build/tools/check-change-entry.sh`, qu'on peut lancer à la main.
- `no-changelog` est pour ce qui ne se voit ni pour un utilisateur ni pour un auteur d'app :
  refactorisation, tests, build, CI.
- Les PR de chantier vers une branche `<série>/base` ajoutent déjà leur entrée, pour que la PR
  finale vers `main` passe le contrôle. Le contrôle ne tourne pas sur ces PR.
- Rendre le contrôle obligatoire se règle dans la protection de branche de `main` (GitHub).

### 7. Publications (Q11 : A)

- `release.yml` construit le texte de la GitHub Release à partir de `changes/<version>.json`
  (anglais, toutes les entrées, groupées par catégorie), au lieu des notes de GitHub.
- `pack-local.sh` remplit `PackageReleaseNotes` de chaque paquet `HackerOs.*` avec les entrées
  `developer` de la version, plus le lien vers la Release.
- `CHANGELOG.md` (anglais) et `CHANGELOG-fr.md` (français) sont générés à la racine du dépôt,
  chacun avec un lien vers l'autre en première ligne, comme les readme.

### 8. Historique et apps du Hack Store (Q12 : A, Q13)

- On remplit seulement **2.1.0** et **la prochaine version**. 2.0.3 et 2.1.0 ont été publiées le
  même soir (première exécution de `release.yml`) : `changes/2.1.0.json` décrit donc ce qui est
  arrivé depuis la série du bureau (SDK 2.0). Rien n'est reconstruit pour les versions plus
  anciennes.
- Le format ne dépend pas de l'OS : une app du Hack Store pourra un jour fournir sa propre liste
  dans le même format. Rien n'est construit pour ça en v1.

### 9. Chantiers

1. ADR 0186 + format : dossier `changes/`, `ChangeEntriesTests`, entrées de 2.1.0 et de la
   prochaine version. **Fait avec cette ADR.**
2. Fichier servi + service : `/whats-new.json` au build, service des nouveautés, état par compte,
   règle du nouveau compte. **Fait** (`whats-new/2-served-file-and-service` : `Tools/HackerOs.Tools.ChangeList`,
   `WhatsNewService`, SDK `HackWhatsNew`).
3. Interface : page Paramètres ▸ À propos ▸ Nouveautés, notification après connexion, pastille du
   menu Démarrer, textes en anglais et en français.
4. Publications : `collect-changes.sh`, texte de la Release dans `release.yml`,
   `PackageReleaseNotes`, `CHANGELOG.md` et `CHANGELOG-fr.md`.
5. Contrôle de PR : workflow **Change list entry** et étiquette `no-changelog`. **Fait avec cette
   ADR.**

Chaque chantier met à jour la doc dans les deux langues et les index README.

## Conséquences

- Toute PR qui change le produit écrit une entrée en anglais et en français. C'est un petit
  coût par PR, mais les notes de version et la page des nouveautés n'ont plus à être écrites à
  part.
- Les entrées sont écrites pour les lecteurs, pas pour les relecteurs du code : une phrase
  courte qui dit ce que la personne peut faire de nouveau.
- Tant que les chantiers 2 à 4 ne sont pas faits, les entrées ne sont lues que par le test et
  par les humains ; `release.yml` garde les notes générées par GitHub.
- Guide pratique : [`change-list.md`](../change-list.md).
