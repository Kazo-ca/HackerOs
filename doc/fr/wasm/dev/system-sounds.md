> [English version](../../../en/wasm/dev/system-sounds.md)

# Sons du système

L'OS joue un son pour ses évènements : notification, avertissement, erreur,
ouverture et fermeture de session. Décision : [ADR 0204](adr/0204-os-event-sounds.md),
issue [#256](https://github.com/Kazo-ca/HackerOs/issues/256).

## Pour l'utilisateur

- **Paramètres ▸ Sons** : interrupteur « Jouer les sons du système », volume
  (0 à 100 %), choix du jeu de sons. Un nouveau choix joue tout de suite le son
  de notification du jeu.
- Le navigateur ne joue un son qu'après un premier clic ou une première touche
  sur la page : un son avant ce geste (une session invité ouverte toute seule)
  est perdu.
- Sur une installation neuve, le premier son d'ouverture de session joue
  quelques secondes après l'arrivée du bureau, une fois les jeux intégrés écrits.
- Jeux intégrés : **HackerOS** (`hackeros`, bips de console, le défaut) et
  **Doux** (`soft`, carillons).

## Le réglage `sound.config`

`/etc/hackeros/sound.config` (machine, livré par une installation neuve) puis
`~/.config/hackeros/sound.config` (utilisateur) :

```ini
enabled=true     # false coupe tous les sons
theme=hackeros   # nom d'un dossier de /usr/share/sounds ou ~/.local/share/sounds
volume=60        # 0 à 100
```

Une valeur invalide vaut la valeur par défaut ; le volume est ramené entre 0 et
100. Un administrateur peut verrouiller une clé comme pour les autres documents
OS (ADR 0044).

## Créer un jeu de sons

Un jeu est un dossier au format freedesktop.org, dans
`~/.local/share/sounds/<id>/` (pour soi) ou `/usr/share/sounds/<id>/` (pour tous,
administrateur) :

```text
~/.local/share/sounds/retro/
├── index.theme
└── stereo/
    ├── message-new-instant.ogg   notification
    ├── dialog-warning.ogg        avertissement
    ├── dialog-error.mp3          erreur
    ├── desktop-login.oga         ouverture de session
    └── desktop-logout.oga        fermeture de session
```

```ini
[Sound Theme]
Name=Retro
Name[fr]=Rétro
Comment=Old beeps
Comment[fr]=Vieux bips
Directories=stereo
```

- `<id>` : minuscules, chiffres, `-`, `_`, `.`.
- Formats : `.oga`, `.ogg` (Ogg Vorbis ou Opus) et `.mp3`, **1 Mo** au plus par
  fichier. Un fichier plus gros est ignoré.
- Un son absent est pris dans le jeu `hackeros`.
- Un dossier utilisateur qui porte le nom d'un jeu système est ignoré.

Exemple au terminal :

```sh
mkdir -p ~/.local/share/sounds/retro
cp -r /usr/share/sounds/soft/* ~/.local/share/sounds/retro/
nano ~/.local/share/sounds/retro/index.theme
```

## Architecture

| Pièce | Projet | Rôle |
| --- | --- | --- |
| `OsSoundEvent`, `OsSoundEvents`, `SoundThemeIndex` | Simulation Abstractions | Évènements, noms de sons, chemins, limite de 1 Mo, lecture de `index.theme`. |
| `SoundSettingsDocuments` | Platform Core | Document `sound.config` (schéma, valeurs par défaut, `SoundPreferences`). |
| `BuiltInSoundThemes` | Platform Core | Jeux intégrés, embarqués (`Sounds/Themes/<id>/*.oga`), écrits dans `/usr/share/sounds` par `FileSystemSeeder.InstallSoundThemesAsync`, que `OsSoundService` lance une fois en arrière-plan après la première ouverture de session (pas pendant la connexion, qu'elle ralentissait de 2 s). `/var/lib/hackeros/installed/sound-themes.list` note les fichiers installés, comme les fichiers `.list` de dpkg : un démarrage dont la liste correspond saute l'installation. |
| `OsSoundService` | Platform Core | Lit et surveille `sound.config`, écoute les notifications (`INotificationFeed`) et les sessions (`IEventBus`), choisit le fichier, joue par `IOsSoundOutput`. Ne joue pas le son d'une notification pendant « Ne pas déranger » (`DoNotDisturb`, ADR 0216). |
| `IOsSoundOutput` / `BrowserSoundOutput` | Platform Core / Platform Blazor | « Carte son » : Web Audio dans `wwwroot/osSounds.js`, tampons décodés gardés par chemin et révision. |
| `DialogCoordinator` | Platform Blazor | Joue avertissement ou erreur pour une boîte de message avec cette icône. |
| `SoundPanel` | App Paramètres | Page Paramètres ▸ Sons, SDK public seulement. |

Ordre de recherche d'un son : `/usr/share/sounds/<thème>`,
`/usr/share/hackeros/theme-packs/<thème>/sounds`, `~/.local/share/sounds/<thème>`,
puis `~/.local/share/hackeros/theme-packs/<thème>/sounds` (le jeu de sons d'un
pack du Hack Store porte l'identifiant du pack, [ADR 0213](adr/0213-theme-packs-build.md)) ; dans le dossier trouvé, chaque dossier de
`Directories=` puis le dossier lui-même, pour `<nom>.oga`, `.ogg`, `.mp3` ; sinon
le jeu `hackeros`. Le même évènement deux fois en moins de 150 ms sonne une
fois.

## Refaire les sons intégrés

Les sons sont calculés, sans échantillon venu d'ailleurs :

```sh
cd wasm2/HackerOs
python3 build/tools/generate-system-sounds.py /tmp/sounds
for f in /tmp/sounds/*/*.wav; do ffmpeg -y -i "$f" -c:a libvorbis -q:a 2 -ar 22050 -ac 1 "${f%.wav}.oga"; done
cp /tmp/sounds/hackeros/*.oga Platform/HackerOs.Platform.Core/Sounds/Themes/hackeros/
cp /tmp/sounds/soft/*.oga Platform/HackerOs.Platform.Core/Sounds/Themes/soft/
```

Une installation existante garde ses anciens fichiers (`FileSystemSeeder`
n'écrase rien).

## Tests

`Tests/HackerOs.Platform.Core.Tests/Sounds/` : format `index.theme` et noms,
fichiers installés, son d'ouverture de session, sons coupés, sévérité des
notifications, jeu de l'utilisateur et repli, limite de 1 Mo, pas de masquage,
changement de jeu, répétition, volume borné.

## Tâches

Voir l'[ADR 0204](adr/0204-os-event-sounds.md) §Tâches.
