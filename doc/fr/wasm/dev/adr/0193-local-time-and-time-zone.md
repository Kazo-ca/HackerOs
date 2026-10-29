> [English version](../../../../en/wasm/dev/adr/0193-local-time-and-time-zone.md)

# ADR 0193 : heure locale et fuseau horaire

## Statut

Accepté le 2026-10-29. Décisions 3 et 4 de Benoit du 2026-10-29 (fil « Améliorer
fenêtres et barre des tâches »), chantier 2 de l'issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242).

- **Complète :** [ADR 0104](0104-localization.md) (langue et formats de la
  session), [ADR 0105](0105-accessibility-preferences.md) (même modèle de réglage
  vivant à deux couches), [ADR 0056](0056-sdk-apis-for-system-apps.md) §8
  (documents OS ouverts par Paramètres).
- **Ne change pas :** [ADR 0045](0045-logs-as-files.md) (les fichiers de
  journal restent en UTC, §6).

## Contexte

- L'horloge de la barre des tâches affiche `ISimulationClock.UtcNow` sans
  conversion : c'est l'heure UTC. Le calendrier du panneau de l'horloge aussi.
- L'OS n'a pas de fuseau horaire. Ailleurs, le code mélange `TimeZoneInfo.Local`
  (`dmesg`, corbeille), `ToLocalTime()` (File Explorer, Paramètres, Code Editor)
  et UTC (`ls -l`).
- Dans le navigateur, .NET WebAssembly donne à `TimeZoneInfo.Local` le fuseau du
  navigateur (`Intl.DateTimeFormat().resolvedOptions().timeZone`), avec les
  données des fuseaux IANA.
- Benoit a décidé : par défaut le fuseau du navigateur, un réglage « Fuseau
  horaire » (Automatique ou un choix dans une liste) dans Paramètres ▸ Date et
  heure, enregistré par utilisateur, et un seul service « heure locale » pour tout
  l'OS, exposé dans le SDK.

## Décision

### 1. Le fuseau d'une session

Le fuseau est un identifiant IANA (`America/Toronto`, `Europe/Paris`, `UTC`) ou
`auto`. `auto` veut dire le fuseau de l'appareil, donné par le navigateur
(`TimeZoneInfo.Local`). Un identifiant inconnu compte comme `auto`.

### 2. Le réglage : `datetime.config` à deux couches

Le réglage est la clé `timeZone` du document OS `datetime`, comme
`accessibility.config` :

- `/etc/hackeros/datetime.config` : le défaut de la machine, aussi utilisé par
  l'écran de connexion ;
- `~/.config/hackeros/datetime.config` : le choix de l'utilisateur, qui gagne.

Sans fichier, la valeur est `auto`. Le document est dans la liste de
`HackSettings.OpenSystemAsync`, pour que Paramètres l'écrive avec le SDK public.

### 3. Le service de session

`SessionTimeZoneService` (Platform Core) lit et surveille le document. Il donne
le fuseau effectif et lève `Changed` quand il change. Un changement s'applique
tout de suite (pas à la prochaine connexion, comme sous GNOME) : l'horloge, le
calendrier et les apps qui relisent l'heure passent au nouveau fuseau.

### 4. Le SDK : `IAppExecutionContext.TimeZone` et `HackTimeZone`

- `IAppExecutionContext.TimeZone` donne le fuseau du processus.
- La variable d'environnement `TZ` d'un processus gagne, comme sous Linux :
  `TZ=UTC ls -l` ou `export TZ=Asia/Tokyo` dans le terminal. Le préfixe POSIX `:`
  est accepté (`TZ=:Europe/Paris`).
- Sans `TZ`, un processus de l'OS suit le service de session (§3), donc le
  changement fait dans Paramètres. Un processus dans un web worker reçoit le
  fuseau de la session à son démarrage et le garde.
- `HackTimeZone` (espace `HackerOs.AppSdk.Globalization`) donne `Current`,
  `Now`, `ToLocal(...)`, `TryFind(...)`, `Resolve(...)`, `FormatOffset(...)` et
  la liste `Available` des fuseaux IANA. Les apps n'utilisent plus
  `ToLocalTime()` ni `TimeZoneInfo.Local`.

### 5. Ce qui affiche l'heure locale

- l'horloge de la barre des tâches (sur chaque écran) et le calendrier du panneau
  de l'horloge ;
- la nouvelle commande `date` : `date` (heure locale), `date -u` (UTC),
  `date -I` (date ISO 8601), `date -R` (RFC 5322) et `date +FORMAT` (`%Y %m %d
  %H %M %S %Z %z %a %b %A %B %j %s %F %T %D %R %n %t %%`) ;
- `ls -l` (date de modification) et `dmesg` (heure des lignes) ;
- File Explorer (Propriétés), Paramètres (synchronisation, clés d'accès), Code
  Editor (journal) et la corbeille (`DeletionDate` de `.trashinfo`).

Les écrans de la plateforme (indicateur du serveur, conflits de synchronisation)
utilisent le service de session.

### 6. Les journaux restent en UTC

Les fichiers `/var/log/*` gardent leur horodatage UTC avec `Z` (ADR 0045) : ils
sont lus par des programmes et doivent rester triables et non ambigus, comme le
journal de systemd qui enregistre en UTC. L'affichage, lui, est local : `dmesg`
convertit chaque ligne au fuseau du processus.

### 7. Paramètres ▸ Date et heure

Une page « Date et heure » (groupe Personnalisation, `--page=datetime`) montre
l'heure locale, l'heure UTC et le fuseau effectif avec son décalage, puis le
choix : « Automatique (fuseau de l'appareil : …) » ou un fuseau de la liste
`HackTimeZone.Available`, filtrable par une recherche. Le choix est écrit dans la
couche utilisateur et s'applique tout de suite.

## Conséquences

- L'horloge affiche l'heure de l'utilisateur. Les dates sont cohérentes dans tout
  l'OS.
- `ITaskbarStatusSource` gagne `TimeZone` (défaut UTC, donc les autres hôtes ne
  changent pas) ; la plateforme y met le fuseau de la session.
- Ajouts au SDK dans `PublicAPI.Unshipped.txt` (prochaine version mineure) :
  `IAppExecutionContext.TimeZone` (membre par défaut, donc compatible) et
  `HackTimeZone`.
- Pas encore fait : `timedatectl`, un réglage 12 h / 24 h séparé de la langue
  (le format reste celui de `LC_TIME`).

## Options écartées

- **Le fuseau dans l'environnement de connexion seulement** (`TZ` lu à la
  connexion depuis un fichier) : simple et très Linux, mais un changement dans
  Paramètres n'aurait touché l'horloge qu'à la connexion suivante.
- **Des journaux en heure locale** : lisibles à l'œil, mais ambigus au
  changement d'heure et difficiles à trier entre fuseaux.
