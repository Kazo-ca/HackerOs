> [English version](../../../../en/wasm/dev/adr/0204-os-event-sounds.md)

# ADR 0204 : sons des évènements de l'OS

## Statut

Accepté le 2026-10-30. Chantier 9 du plan du Hack Store
([ADR 0190](0190-theme-packs.md) §4, point 3), issue `TODO`
[#256](https://github.com/Kazo-ca/HackerOs/issues/256), série `todo/base`.

- **Complète :** [ADR 0190](0190-theme-packs.md) (le dossier `sounds/` d'un pack
  `.hacktheme` vise ce système), [ADR 0044](0044-settings-as-files.md) (réglages
  en fichiers à deux couches), [ADR 0056](0056-sdk-apis-for-system-apps.md) §8
  (documents OS ouverts par Paramètres).

## Contexte

- L'OS ne jouait aucun son. Benoit veut des sons pour les évènements de l'OS
  (notification, erreur, ouverture de session…), en OGG ou MP3, un réglage pour
  les couper et le choix du jeu de sons dans Paramètres.
- Les packs du Store (ADR 0190) porteront des sons : il faut un format de jeu de
  sons que l'OS lit sur le disque.
- Sur Linux, les jeux de sons suivent la spécification freedesktop.org : un
  dossier par thème dans `/usr/share/sounds/` (ou `~/.local/share/sounds/`), un
  fichier `index.theme` et un fichier par nom de son (`dialog-error.oga`).
- Un navigateur ne joue un son qu'après un geste de l'utilisateur sur la page
  (clic, touche).

## Décision

### 1. Un jeu de sons est un dossier freedesktop.org

```text
/usr/share/sounds/<id>/index.theme        [Sound Theme] Name=, Name[fr]=, Comment=, Directories=
/usr/share/sounds/<id>/stereo/<nom>.oga   un fichier par évènement (.oga, .ogg ou .mp3)
~/.local/share/sounds/<id>/...            les jeux de l'utilisateur, même format
```

- `<id>` : lettres minuscules, chiffres, `-`, `_`, `.` (64 caractères au plus).
- `Directories=` liste les sous-dossiers cherchés dans l'ordre ; le dossier du
  jeu est cherché en dernier. Un chemin avec `/` ou `..` est ignoré.
- Un fichier de plus de **1 Mo** n'est pas joué (limite de l'ADR 0190 §3).
- Un jeu système passe avant un jeu de l'utilisateur du même nom : pas de
  masquage, comme l'ADR 0103.

### 2. Les évènements et leurs noms de sons

| Évènement | Nom de son freedesktop.org | Source |
| --- | --- | --- |
| Notification | `message-new-instant` | notification d'information ou de réussite |
| Avertissement | `dialog-warning` | notification d'avertissement, boîte de message avec l'icône avertissement |
| Erreur | `dialog-error` | notification d'erreur, boîte de message avec l'icône erreur |
| Ouverture de session | `desktop-login` | `SessionActivatedEvent` |
| Fermeture de session | `desktop-logout` | `SessionLoggedOutEvent` |

Un son absent du jeu choisi joue celui du jeu `hackeros` (comme `Inherits=` de
la spécification). Le même évènement deux fois en moins de 150 ms ne sonne
qu'une fois. Seules les notifications de l'utilisateur de la session sonnent.

### 3. Le réglage `sound.config`

Document OS à deux couches, `/etc/hackeros/sound.config` (livré par une
installation neuve) puis `~/.config/hackeros/sound.config` :

| Clé | Type | Défaut |
| --- | --- | --- |
| `enabled` | booléen | `true` |
| `theme` | identifiant de jeu | `hackeros` |
| `volume` | entier 0 à 100 | `60` |

Le service de sons relit le fichier dès qu'il change. Un changement de jeu ou de
volume fait pendant la session joue le son de notification, comme le panneau Son
de GNOME ; l'ouverture d'une autre session ne le fait pas.

### 4. Deux jeux intégrés, installés comme de vrais fichiers

`hackeros` (« HackerOS », bips de console, le défaut) et `soft` (« Doux »,
carillons) sont calculés par `build/tools/generate-system-sounds.py` (aucun
échantillon venu d'ailleurs), encodés en OGG Vorbis (environ 5 Ko par son),
embarqués dans `HackerOs.Platform.Core` et écrits par `FileSystemSeeder` dans
`/usr/share/sounds/<id>/` (root, `0644`), comme un paquet de distribution. Un
fichier existant n'est jamais remplacé. `~/.local/share/sounds/` est créé dans
chaque dossier personnel.

### 5. La lecture

- `OsSoundService` (Platform Core, une instance par session) choisit le fichier,
  lit ses octets avec les droits de l'utilisateur (son dossier personnel) ou du
  système, et les passe à la « carte son » `IOsSoundOutput`.
- `BrowserSoundOutput` (Platform Blazor) joue le son avec la Web Audio API
  (`osSounds.js`) : décodé une fois, gardé par chemin et révision du fichier,
  joué à travers un gain. Avant le premier geste sur la page, le son est perdu
  sans erreur.
- Un son ne fait jamais échouer ce qu'il accompagne.

### 6. Paramètres ▸ Sons

Panneau du groupe Personnalisation, avec le SDK public seulement : interrupteur
« Jouer les sons du système », volume, liste des jeux de `/usr/share/sounds` et
de `~/.local/share/sounds`, et tableau des fichiers joués par évènement.

### 7. Hors de ce chantier

- Aucune API du SDK pour qu'une app joue un son du système (question Q2 posée à
  Benoit, réponse recommandée : plus tard, issue `TODO` à part).
- L'installation des sons d'un pack `.hacktheme` (ADR 0190, issue #253).

## Conséquences

- Le dossier `sounds/` d'un pack devient un jeu de sons copié dans
  `~/.local/share/sounds/<packId>/`, sans autre système à créer.
- Environ 50 Ko de sons sont ajoutés à `HackerOs.Platform.Core` et au disque
  simulé.
- Une modification des sons intégrés n'atteint pas une installation existante
  (fichier déjà présent), comme pour les autres fichiers semés.

## Questions posées à Benoit

- **Q1** Sons activés par défaut ? A (recommandé) : oui, à 60 % ; B : coupés.
- **Q2** Les apps peuvent-elles jouer les sons du système ? A (recommandé) : plus
  tard, issue `TODO` à part ; B : API SDK dans ce chantier.

Le chantier suit les recommandations en attendant les réponses.

## Tâches

- [x] Format du jeu de sons, noms des évènements (`OsSoundEvents`, `SoundThemeIndex`).
- [x] `sound.config` et `OsSoundService`.
- [x] Jeux intégrés `hackeros` et `soft` dans `/usr/share/sounds`.
- [x] Lecture Web Audio (`BrowserSoundOutput`, `osSounds.js`).
- [x] Sons des notifications, des boîtes de message et de la session.
- [x] Paramètres ▸ Sons.
- [x] Tests, docs, `features.md`.
