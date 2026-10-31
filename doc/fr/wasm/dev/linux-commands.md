> [English version](../../../en/wasm/dev/linux-commands.md)

# Commandes Linux du shell

Commandes ajoutées ou revues par l'[ADR 0064](adr/0064-posix-shell-scripts-jobs-and-app-commands.md)
pour que le terminal se comporte comme un shell Linux. Elles vivent dans
`Apps/Commands/HackerOs.Commands/` (un dossier par commande, ADR 0051), ne
dépendent que du SDK public (ADR 0055) et sont des apps système. Le langage du
shell lui-même (tubes, redirections, scripts, jobs, apps lancées par leur nom)
est décrit dans [`shell-interpreter.md`](shell-interpreter.md) ; les
commandes qui agissent sur le shell (`cd`, `export`, `sudo`…) dans
[`shell-commands.md`](shell-commands.md).

```text
alice@hackeros:~$ cut -d, -f1 users.csv | sort | uniq -c | sort -rn | head -3
alice@hackeros:~$ zip -r backup.zip projets && unzip -l backup.zip
alice@hackeros:~$ chmod +x deploy.sh && ./deploy.sh
alice@hackeros:~$ sleep 30 & jobs; kill %1
alice@hackeros:~$ watch -n 1 'ps | wc -l'
```

## Principes

- **Mimer le métal** : messages, options et codes de sortie de coreutils,
  procps, iproute2, net-tools, Info-ZIP et systemd ; chaque valeur affichée
  vient d'une vraie source de l'OS (`/proc`, `HackDriveInfo`,
  `HackServiceController`, réseau simulé) ou est documentée comme simulée.
- Les fichiers passent par l'API `Hack*` (ou la passerelle de fichiers pour
  les commandes plus anciennes) : chemins relatifs au répertoire de la
  commande, permissions et capacités de la commande.
- Une commande plein écran (`top`, `htop`, `watch`, `nano`) n'a l'écran
  alternatif que si sa sortie est le terminal ; dans un tube ou redirigée,
  elle produit du texte (`top -b`).
- `Common/` : `CommandIO` (fichiers ou entrée standard, tailles `-h`),
  `Wildcard`, `SystemSnapshot` (`top`, `htop`), `NetworkInfo` (`ip`,
  `netstat`).
- **Langue** ([`localization.md`](../framework/localization.md), ADR 0104) : chaque
  commande écrit ses messages (erreurs, usage, invites, résumés) dans la langue
  de **son** processus (`LANG=fr_FR.UTF-8 mkdir` → `mkdir : opérande
  manquant`), avec un dossier `Resources/<Commande>/Strings*.resx` par
  commande et les textes `errno` partagés de `Common/CommandText`. Restent en
  anglais, comme sous Linux, les sorties lues par des scripts : colonnes et
  états de `ps`, `top`, `netstat`, `systemctl list-units` et du bloc
  `systemctl status`, lignes d'enregistrement de `nslookup`, lignes de
  progression de `zip`/`unzip` (`adding:`, `inflating:`), ligne `rtt` de
  `ping`. Les invites de `nano` acceptent `o`/`n` en français en plus de
  `y`/`n`. Description et usage de chaque commande sont traduits dans
  `Resources/AppManifest.fr.resx` (clés `<appId>.Description` et
  `<appId>.Terminal.Usage`, sans `Name` : les noms de commandes ne changent
  pas).

## Texte

| Commande | Options | Notes |
|---|---|---|
| `sort` | `-r -n -h -V -f -b -u -c`, `-k N[,M]`, `-t SEP`, `-o FICHIER` | Tri stable avec comparaison finale octet par octet, comme GNU sort ; options combinables (`-rn`). |
| `head` | `-n N`, `-N`, `-n -N` (tout sauf les N dernières), `-c N`, `-q`, `-v` | Lit l'entrée standard au fil de l'eau : `yes \| head` se termine. |
| `tail` | `-n N`, `-N`, `-n +N`, `-c N`, `-f`, `-q`, `-v` | `-f` suit le fichier (relu toutes les 500 ms) jusqu'à Ctrl+C ; signale `file truncated`. |
| `cut` | `-f LISTE` avec `-d SEP`, `-s`, `--output-delimiter`, `-c`/`-b LISTE`, `--complement` | LISTE : `N`, `N-M`, `N-`, `-M`, séparés par des virgules. |
| `tee` | `-a` | Copie l'entrée vers la sortie et chaque fichier ; un fichier inaccessible n'arrête pas les autres (code 1). |
| `uniq` | `-c -d -u -i`, `[ENTRÉE [SORTIE]]` | Lignes adjacentes. |
| `printf` | `%s %b %c %d %i %u %o %x %X %e %f %g %%`, drapeaux, largeur, précision, `*` | Format réutilisé tant qu'il reste des arguments ; échappements `\n \t \0NNN \xHH \c`. |
| `seq` | `[PREMIER [PAS]] DERNIER`, `-s SEP`, `-w` | Entiers ou décimaux. |
| `basename`, `dirname` | `basename NOM [SUFFIXE]`, `-a`, `-s` | Texte seulement, comme coreutils. |

## Fichiers

| Commande | Options | Notes |
|---|---|---|
| `mkdir` | `-p`/`--parents`, `-v`/`--verbose`, `--` | Comme coreutils : sans `-p`, le parent doit exister et un dossier existant donne « File exists » ; avec `-p`, chaque composant manquant est créé (un `mkdir(2)` par composant, avec la révision du parent), un dossier existant n'est pas une erreur et un fichier sur le chemin donne « Not a directory ». `-v` affiche `mkdir: created directory '…'` pour chaque dossier créé. Mode `0755`. |
| `chmod` | `MODE` octal ou symbolique (`+x`, `u+x`, `go-w`, `a=r`, `u=rwx,g=rx,o=`, `g+u`, `X`), `-R`, `-v`, `-c`, `-f` | Sans lettre de classe, le masque `022` s'applique (`+w` = propriétaire seul) ; les bits spéciaux (4ᵉ chiffre) sont ignorés. |
| `ln` | `-s`, `-f`, `-n`, `-v`, `CIBLE [LIEN]`, `CIBLE… DOSSIER` | Liens symboliques par `HackFile.CreateSymbolicLinkAsync` ; le texte de la cible est gardé tel quel. Sans `-s` : `Operation not supported` (pas de liens physiques dans le système de fichiers HackerOS) et le conseil d'utiliser `-s`. |
| `locate` | `-i -b -c -e`, `-l N` | Pas de base `updatedb` : parcours en direct avec les droits de l'utilisateur, `/proc`, `/dev`, `/sys`, `/run` exclus ; un motif sans joker est cherché partout dans le chemin. Code 1 sans résultat. |
| `du` | `-a -s -h -c -b`, `-d N` | Blocs de 4 Kio comme ext4 (un par dossier) ; affichage en Kio. |
| `grep` | `-i -n -v`, `-r`/`-R` (récursif, dossier courant sans fichier), options combinables (`-rn`) | Préfixe le nom du fichier quand il y en a plusieurs ou avec `-r` ; les liens symboliques rencontrés ne sont pas suivis. |
| `df` | `-h -T -a` | Disques de `HackDriveInfo` (`/dev/hda` sur `/`) : utilisé et quota réels du navigateur depuis l'ADR 0099 §2 ; `-a` ajoute les pseudo-systèmes de `/proc/mounts`. |
| `zip` | `-r -q -j -d -0…-9` | Vraie archive deflate lisible par tout outil zip ; met à jour une archive existante ; les modes Unix sont stockés dans les attributs externes. |
| `unzip` | `-l -t -p -o -n -q -j`, `-d DOSSIER`, `[MOTIFS]` | Restaure les modes (`+x`) ; refuse une entrée qui sortirait du dossier (`../`, « zip slip ») ; ne remplace pas un fichier sans `-o`. |
| `xdg-open` | `FICHIER`, `DOSSIER` ou `URI` (un seul argument) | Comme freedesktop (ADR 0096 §3) : un chemin s'ouvre avec son association (un dossier dans le gestionnaire de fichiers, `inode/directory`), une URI avec le gestionnaire de son schéma (`x-scheme-handler/<schéma>`, le navigateur pour `http`/`https`, le fichier pour `file://`). Rend la main dès l'envoi. Codes de `xdg-open` : 0 envoyé, 1 ligne de commande, 3 aucune app, 4 fichier absent ou échec ; plusieurs apps sans défaut donnent 4 et la liste des candidates (un terminal n'a pas de sélecteur et l'OS ne devine pas). `apps.launch`. |
| `xdg-mime` | `query default TYPE`, `query filetype FICHIER`, `default APP TYPE…` | Comme freedesktop (ADR 0121 §5) : `query default` affiche l'app qui ouvre ce type pour cet utilisateur (rien sans défaut) ; `default` écrit le choix de l'utilisateur dans `~/.config/hackeros/file-associations.json` (un `.desktop` final est ignoré ; l'app doit déclarer le type) ; `query filetype` donne `inode/directory`, le type de média déclaré pour l'extension, l'extension, ou `application/octet-stream`. Seule l'action `open`. Codes : 0, 1 ligne de commande ou cible invalide, 2 fichier absent, 4 refus. `file-associations.read`, `filesystem.user-home.write`. |

## Processus et services

| Commande | Options | Notes |
|---|---|---|
| `top` | `-b`, `-n N`, `-d SECONDES`, `-u USER`, `-o CHAMP` | Plein écran rafraîchi toutes les 3 s ; touches `q`, `M`, `P`, `N`, `T`. CPU depuis deux lectures de `/proc/stat`, mémoire depuis `/proc/meminfo`, processus par `HackProcess`. |
| `htop` | `-d DIXIÈMES`, `-u USER`, `-t`, `-C` | Barres par cœur, mémoire et swap en couleur, liste défilante avec sélection, arbre (`t`), tri (`P`/`M`/`T`/`N`), recherche (`/`), utilisateur (`u`), `k` tue le processus choisi (`process.manage`), `q` quitte. |
| `nice` | `-n N`, `-N`, `--adjustment=N` | Sans commande : affiche la priorité du shell. Sinon lance la commande avec `nice + N` (10 par défaut, borné à -20..19). Baisser la valeur demande root : `sudo nice -n -5 cmd` lance la commande en root (ADR 0136), `sudo renice` change un processus déjà lancé. Les commandes qui pilotent le shell marchent aussi (`nice -n 5 nice` affiche 5). Voir [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md). |
| `renice` | `[-n] N`, `-p PID…` | Change la priorité de processus existants ; monter est permis sur ses processus, baisser ou toucher ceux d'un autre utilisateur demande root (`Permission denied` / `Operation not permitted`). `-g` et `-u` sont refusés. `process.manage` pour un processus qui n'est ni le sien ni un de ses enfants (ADR 0140). |
| `ps` (priorité) | `-l`, `-o CHAMPS` | `-l` affiche `F S UID PID PPID C PRI NI TIME CMD` (`PRI` = 80 + `NI`, comme procps) ; `-o` choisit parmi `pid`, `ppid`, `user`, `uid`, `ni`, `pri`, `stat`, `s`, `f`, `c`, `time`, `comm`, `args` ; un champ inconnu sort avec 1. `top` montre `PR` et `htop` `PRI` (20 + `NI`, comme ces outils), plus `NI`. |
| `kill` | `-s SIG`, `-SIG`, `-N`, `-l`, `PID` ou `%job` | `KILL` (9) tue tout de suite, sans prévenir ; les autres signaux demandent l'arrêt (`StopRequested`, raison `Terminated`, ADR 0069) puis forcent après 5 s ; `kill -0 PID` teste l'existence ; `%n` passe par le shell (`shell.control`). |
| `jobs` | `-l -p -r -s` | Jobs du shell appelant ; les jobs terminés sont montrés une fois. |
| `systemctl` (`service`) | `list-units`, `list-unit-files`, `status`, `start`, `stop`, `restart`, `enable`, `disable`, `mask`, `unmask`, `is-active`, `is-enabled`, `is-failed`, `reset-failed` | Les apps `Service` du catalogue vues comme des unités `nom.service` ; automatique = `enabled`, manuel = `disabled`, désactivé = `masked`. États façon systemd (ADR 0097) : `status` affiche `Active: active (running)`, `activating (start)`, `deactivating (stop-sigterm)`, `failed (Result: exit-code|exception|killed|timeout) since …`, `Main PID`, la ligne `Status: "…"` et `Health:` quand le service les a donnés ; `is-active` imprime l'état et sort avec 0 seulement pour `active` (3 sinon) ; `is-failed` sort avec 0 pour `failed` ; `reset-failed [UNIT]` efface l'échec (toutes les unités sans argument). Un service système (démarré au boot) ne se pilote qu'en administrateur. Agir sur le service d'une autre app demande `services.manage`. `service NOM ACTION` est accepté. |
| `watch` | `-n SECONDES`, `-t`, `-g`, `-e` | Relance la ligne par `IShellSession.RunAsync` (sous-shell : tubes et variables marchent) et affiche sa dernière sortie en plein écran ; `q` ou Ctrl+C quitte. |
| `prlimit` | `--pid PID`, `--cpu`, `--rss`, `--nofile`, `--nproc` (`=souple:dure`, aussi `-t -m -n -u`), `--noheadings` | Comme util-linux : affiche ou change les limites d'un processus (ADR 0125, [`process-limits.md`](process-limits.md)) ; sans `--pid`, son propre processus. Lit `/proc/<pid>/limits` (`process.list`, `filesystem.system.read`) ; changer un autre processus demande `process.manage`, monter une limite dure demande `sudo`. Le built-in `ulimit` du shell fait de même pour le shell. |
| `dmesg` | `-T`, `-x`, `-l NIVEAU[,NIVEAU…]`, `-k`, `-h` | Comme util-linux : lit le tampon du noyau de la session dans `/dev/kmsg` (OOM killer, garde du navigateur ; [`oom-killer.md`](oom-killer.md)) ; `-T` heure locale (fuseau du processus, ADR 0193), `-x` niveau en clair. |
| `date` | `-u`, `-I`, `-R`, `+FORMAT`, `--help` | Comme GNU `date` dans la locale C : heure de l'OS dans le fuseau du processus (`TZ`, sinon Paramètres ▸ Date et heure ; ADR 0193, [`time-zone.md`](../framework/time-zone.md)) ; `-u` UTC, `-I` date ISO 8601, `-R` RFC 5322, `+FORMAT` avec `%Y %y %m %d %e %H %I %M %S %p %Z %z %a %A %b %h %B %j %s %F %T %D %R %n %t %%`. Régler l'horloge est refusé (code 1). |
| `sleep` | `N[smhd]…`, décimaux | Ctrl+C l'interrompt (130). |
| `stress` | `-c N`, `-m N` (`--vm`), `--vm-bytes B`, `--vm-keep`, `-t SECONDES`, `--yield` | Comme `stress(1)` : N boucles de calcul sans rendre la main (`--yield` : avec points d'arrêt) ; `--vm` : N travailleurs qui allouent `B` octets (`256M` par défaut) par blocs de 1 Mio et touchent chaque page, pour tester l'OOM killer ([`oom-killer.md`](oom-killer.md)). Tourne dans un Web Worker : le bureau reste fluide, le cœur passe « not-responding », `kill -9` termine le worker ([`worker-processes.md`](worker-processes.md)). |
| `whoami` | — | `root` sous `sudo`. |
| `which` | `NOM…` | `/usr/bin/NOM` pour une commande installée, `/bin/sh` pour le shell ; code 1 sinon. |

## Sécurité

| Commande | Options | Notes |
|---|---|---|
| `ausearch` | `-f CHEMIN`, `-ua`/`-ui`/`-ul UTILISATEUR`, `-c COMM`, `--success yes\|no` (`-sv`), `-if FICHIER`, `-i` | Comme l'outil d'`auditd` : cherche dans `/var/log/audit.log` et ses archives (ADR 0100 §3, [`sensitive-settings.md`](sensitive-settings.md)). Le journal est `0600 root` : `sudo ausearch -f /etc/hosts`. `-f` accepte le chemin, un fichier dessous ou un nom de fichier ; `-ul` cherche l'humain d'origine (`auid`). Code 1 et `<no matches>` sans résultat. |

## Administration

| Commande | Options | Notes |
|---|---|---|
| `localectl` | `status` (défaut), `list-locales`, `set-locale [VARIABLE=]LOCALE…` | Locale du système dans `/etc/locale.conf` (ADR 0104) : `status` affiche `System Locale: LANG=…` ; `list-locales` liste les 8 locales connues (`C.UTF-8`, `en_CA/GB/US`, `fr_BE/CA/CH/FR`, toutes `.UTF-8`) ; `set-locale` remplace le fichier, seulement en root (`sudo localectl set-locale LANG=fr_CA.UTF-8`), sinon `Access denied (try sudo)` et code 1. Prend effet à l'écran de connexion au prochain démarrage et, pour les utilisateurs sans `~/.config/locale.conf`, à leur prochaine connexion ([`localization.md`](../framework/localization.md)). |
| `hackbackup` | `create`, `restore [--merge\|--replace]`, `-h` | Sauvegarde de toute la machine (ADR 0099 §1) : doit tourner en root (`sudo hackbackup create`), sinon `must be run as root` et code 1. `create` télécharge `hackeros-AAAAMMJJ-HHMMSSZ.hosbak` sur l'ordinateur hôte ; `restore` ouvre le sélecteur de fichiers du navigateur (Entrée compte comme geste), fusion par défaut, puis la machine redémarre ; sélecteur fermé ou archive refusée : code 1 ; usage invalide : code 2 ([`system-backup.md`](system-backup.md)). |
| `hackpkg` | `install <appId\|dépôt>[@version] [--user\|--system]`, `remove <appId> [--user\|--system]`, `update [<appId>]`, `list`, `info <appId\|dépôt>`, `sources [add <source> [--trusted] \| remove <source>]`, `policy [block\|unblock app\|category\|capability <valeur>]`, `-h` | Apps installées depuis leur dépôt GitHub (ADR 0182, ADR 0189, [`app-installation.md`](app-installation.md#apps-installées-depuis-un-dépôt-github)). `<dépôt>` : `github:owner/repo`, `owner/repo`, `https://github.com/owner/repo` ou `catalog:<nom>/<appId>` ; un `<appId>` seul est cherché dans les catalogues de `[catalogs]`, l'officiel d'abord (ADR 0212). Sans option, une app d'une source de confiance s'installe pour soi (`~/.local/share/hackeros/apps`), sans administrateur ; `--system` l'installe pour tous. Une installation pour tous, une source non fiable ou une app de la liste noire demande à chaque fois, même sous `sudo`, une connexion administrateur puis la fenêtre de validation. `update` garde chaque copie là où elle est ; `list` affiche `system` ou `user`. `sources add|remove` et `policy block|unblock` modifient `/etc/hackeros/app-sources.config` et `/etc/hackeros/app-policy.config` (un administrateur, rien sous `sudo`). Après une installation : « restart HackerOS to finish ». `list` marque `(restart pending)` une app pas encore au catalogue. Paquet refusé : son motif et code 1 ; usage invalide : code 2. |
| `passwd` | `[NOM]`, `-d NOM`, `-S [NOM]`, `-h` | Change le mot de passe (ADR 0120, [`sensitive-settings.md`](sensitive-settings.md)) : `Current password:`, `New password:`, `Retype new password:`, lus écho coupé ; `passwd: password updated successfully`. Mauvais mot de passe courant : `Authentication token manipulation error`, code 10 ; confirmation différente : `Sorry, passwords do not match.`, code 10 ; moins de 8 caractères : `BAD PASSWORD`, code 1. Un autre compte ou `-d` exige `sudo` (`You may not view or modify password information for NOM.`), sans demander l'ancien mot de passe ; le trousseau du compte est mis de côté. `sudo passwd` sans nom refuse (pas de mot de passe root). `-S` affiche `NOM P\|NP\|L AAAA-MM-JJ 0 99999 7 -1`. Sur le fil principal (saisie du terminal). |

Plus de place : une écriture refusée affiche `No space left on device`
(`ENOSPC`), un fichier de plus de 16 Mio `File too large` (`EFBIG`).

## Réseau

| Commande | Options | Notes |
|---|---|---|
| `curl` | `-I -i -v -L -s -f`, `-o FICHIER`, `-O`, `-H "Nom: valeur"`, `-A`, `-X`, `-d`/`--data-raw`/`--data-binary` (`@fichier`), `--data-urlencode`, `--json` | `-o`, `-O`, `-H`, `-A`, `-s` ajoutés par l'ADR 0064 ; corps bruts, `-i`, `--json`, `--data-*` et `-f` facultatif depuis `web/2-http-bodies` (ADR 0074 §1) : sans `-f`, une erreur HTTP affiche le corps et sort avec 0, comme le vrai `curl` ([`http-bodies.md`](../framework/http-bodies.md)). |
| `wget` | `-O FICHIER` (`-O -` = sortie), `-P DOSSIER`, `-q`, `-S`, `--spider` | Ne remplace jamais : `fichier.1`, `fichier.2`… ; progression sur la sortie d'erreur. |
| `ip` (`ifconfig`) | `addr`, `link`, `route`, `neigh`, `-br`, `-s`, `dev NOM` | `lo` 127.0.0.1/8 et `eth0` 192.168.1.100/24 derrière le routeur 192.168.1.1 du réseau simulé, MAC stable dérivée du nom de la machine ; compteurs réels de `/proc/net/dev`. |
| `netstat` (`ss`) | `-t -u -l -n -a -p`, `-i`, `-r`, `-s` | Les ports à l'écoute viennent de la table des ports de l'OS (`HackIPGlobalProperties`, depuis `web/3`) : démons simulés de `localhost` et sockets des apps ; `-p` montre `PID/programme` pour root ou le même utilisateur, sinon `-` et la note de net-tools ; `-i` et `-s` lisent `/proc/net/dev`. |
| `nslookup` | `<nom>` ou `<adresse>` | Interroge `hackdns` (`web/3`) : indique la source (`/etc/hosts`, loopback, app web, site hébergé ou réseau simulé) ; `NXDOMAIN` et code 1 si le nom est inconnu ([`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)). |

## Scripts

| Commande | Notes |
|---|---|
| `test`, `[` | `-e -f -d -s -r -w -x -L -h`, `-n -z`, `= == != < >`, `-eq -ne -lt -le -gt -ge`, `-nt -ot -ef`, `! -a -o ( )` ; `[` exige `]` (le shell lie `[` à `test`). Code 2 sur erreur d'usage. |
| `true`, `false` | Codes 0 et 1. |
| `read` | `-r -p -s -n -t -d`, `NOM…` (sinon `REPLY`) ; découpe sur `IFS`, le dernier nom prend le reste ; affecte des variables du shell appelant (`shell.control`), donc perdues dans un tube comme dans bash ; code 1 en fin d'entrée, 142 au délai dépassé. |

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.Commands.cs` :
  `test`/`[`, `read` avec `while read`, `printf`, `seq`, `cut | sort | uniq -c`,
  `sort -k -t -n -r`, `tail -n +N`, `head -c`, `tee`, `chmod +x` puis
  `./script`, modes symboliques, `ln -s` et refus des liens physiques,
  `zip`/`unzip` avec les modes, `whoami`, `basename`, `dirname`, `which`,
  `du`, `locate`, `jobs` et `kill %1`, `kill PID` et `kill -9 PID` sur un vrai
  processus, `grep -r`, `ip`, `top -b`.
- `Tests/HackerOs.Commands.Wave5.Tests` : `chmod`, `kill`, `sort`, `head`,
  `tail`, `mkdir` (`-p`, `-v`, dossier existant, fichier sur le chemin, `..`), `hackbackup` et `hackpkg` avec les passerelles factices.
- `Tests/HackerOs.Commands.Tests/XdgOpenCommandTests.cs` : chemin relatif, dossier, fichier absent, aucune app, plusieurs apps, URI, schéma inconnu, ligne de commande, refus (`v3/4`). Le scénario de parité (`CommandParityTests`) compare la commande dans un Web Worker et sur le fil principal.
- `Tests/HackerOs.Ecosystem.Tests` : 94 apps intégrées dont 75 commandes (`stress` ajoutée par `rebuild/5-worker-processes`, `nslookup` par `web/3`, `ausearch` par `v3/8-settings-security`, `xdg-open` par `v3/4`, `hackbackup` par `v3/7`, `localectl` par `v3/12`, `xdg-mime` par `v3/4b`, `passwd` par `v3/8`, `prlimit` par `exec/2`, `nice` et `renice` par `exec/4`, `dmesg` par `exec/3`, `hackpkg` par l'ADR 0182).
- `Tests/HackerOs.Commands.Tests` : filtres d'`ausearch` ; `LocalizedCommandsTests` et `LocalizedCommandsMzTests` exécutent des commandes avec `LANG=fr_FR.UTF-8` (`mkdir`, `rm`, `seq`, `sleep`, `printf`, `test`, `sudo`, `xdg-open`…), vérifient les mêmes clés en anglais et en français et la description et l'usage français des 75 commandes générés dans `localization/`. `NanoCommandTests` dessine les barres de `nano` en français, `LocalizationShellTests` (WorkerHost) passe par le vrai shell.

## Liste des tâches

- [x] `sort`, `head`, `tail` (`-f`), `kill` (`%job`, signaux), `curl` (`-o`, `-O`, `-H`) revus.
- [x] `chmod` symbolique et récursif (`chmod +x`).
- [x] `cut`, `tee`, `uniq`, `printf`, `seq`, `basename`, `dirname`.
- [x] `ln -s` et `HackFile.CreateSymbolicLinkAsync`.
- [x] `locate`, `du`, `df`, `zip`, `unzip`.
- [x] `top`, `htop` (couleurs dans les cadres plein écran), `watch`, `jobs`, `sleep`, `whoami`, `which`.
- [x] `mkdir -p` et `-v` (`rebuild/5-ui-a11y-mkdir`, 2026-10-16).
- [x] `systemctl`/`service`.
- [x] `wget`, `ip`/`ifconfig`, `netstat`/`ss`.
- [x] `test`/`[`, `true`, `false`, `read`.
- [x] Messages des commandes en anglais et en français, description et usage traduits (`v3/12`).
- [x] `ausearch` (`v3/8-settings-security`, ADR 0100).
- [x] `xdg-open` (`v3/4-intents`, ADR 0096).
- [x] `xdg-mime` (`v3/4b`, ADR 0121).
- [x] Tests et documentation.
- [x] ADR 0069 (`rebuild/5-graceful-stop`) : `find`, `grep` (et `grep -r`), `du`, `zip`, `unzip`, `locate` appellent `HackProcess.CheckpointAsync` dans leurs boucles ; `kill` envoie `Terminated`.
- [x] `hackbackup` et messages `ENOSPC`/`EFBIG` (`v3/7-storage-backup`, ADR 0099).
- [x] `hackpkg` : installer des apps depuis leur dépôt GitHub (ADR 0182).
- [x] `hackpkg` : `--user`, `--system`, `sources add/remove`, `policy`, retrait de `--allow-untrusted` (ADR 0189, ADR 0208).
- [x] `prlimit` et built-in `ulimit` ; `cat`, `grep` et `wc` disent « Too many open files » sur `EMFILE` (`exec/2-process-limits`, ADR 0125 et 0139).
- [x] `dmesg` et `stress --vm`/`--vm-bytes`/`--vm-keep` (`exec/3-oom-killer`, ADR 0126 et 0142, [`oom-killer.md`](oom-killer.md)).
- [x] `nice`, `renice`, `ps -l`/`-o`, colonnes `PR`/`NI` de `top` et `htop` (`exec/4-not-responding-ui`, ADR 0127, [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)).
