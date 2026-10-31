> [Version française](../../../fr/wasm/dev/linux-commands.md)

# Shell Linux commands

Commands added or reviewed by [ADR 0064](adr/0064-posix-shell-scripts-jobs-and-app-commands.md)
so that the terminal behaves like a Linux shell. They live in
`Apps/Commands/HackerOs.Commands/` (one folder per command, ADR 0051), depend
only on the public SDK (ADR 0055) and are system apps. The shell language
itself (pipes, redirections, scripts, jobs, apps launched by name) is
described in [`shell-interpreter.md`](shell-interpreter.md); the commands that
act on the shell (`cd`, `export`, `sudo`…) in
[`shell-commands.md`](shell-commands.md).

```text
alice@hackeros:~$ cut -d, -f1 users.csv | sort | uniq -c | sort -rn | head -3
alice@hackeros:~$ zip -r backup.zip projets && unzip -l backup.zip
alice@hackeros:~$ chmod +x deploy.sh && ./deploy.sh
alice@hackeros:~$ sleep 30 & jobs; kill %1
alice@hackeros:~$ watch -n 1 'ps | wc -l'
```

## Principles

- **Mimic the real thing**: messages, options and exit codes of coreutils,
  procps, iproute2, net-tools, Info-ZIP and systemd; each displayed value
  comes from a real OS source (`/proc`, `HackDriveInfo`,
  `HackServiceController`, simulated network) or is documented as simulated.
- Files go through the `Hack*` API (or the file gateway for older commands):
  paths relative to the command's directory, the command's permissions and
  capabilities.
- A full-screen command (`top`, `htop`, `watch`, `nano`) only gets the
  alternate screen if its output is the terminal; in a pipe or redirected, it
  produces text (`top -b`).
- `Common/`: `CommandIO` (files or standard input, `-h` sizes), `Wildcard`,
  `SystemSnapshot` (`top`, `htop`), `NetworkInfo` (`ip`, `netstat`).
- **Language** ([`localization.md`](../framework/localization.md), ADR 0104):
  each command writes its messages (errors, usage, prompts, summaries) in the
  language of **its** process (`LANG=fr_FR.UTF-8 mkdir` → `mkdir : opérande
  manquant`), with a `Resources/<Command>/Strings*.resx` folder per command
  and the shared `errno` texts of `Common/CommandText`. Remaining in English,
  as under Linux, are the outputs read by scripts: columns and states of `ps`,
  `top`, `netstat`, `systemctl list-units` and the `systemctl status` block,
  record lines of `nslookup`, progress lines of `zip`/`unzip` (`adding:`,
  `inflating:`), the `rtt` line of `ping`. The `nano` prompts accept `o`/`n`
  in French in addition to `y`/`n`. The description and usage of each command
  are translated in `Resources/AppManifest.fr.resx` (keys
  `<appId>.Description` and `<appId>.Terminal.Usage`, with no `Name`: command
  names do not change).

## Text

| Command | Options | Notes |
|---|---|---|
| `sort` | `-r -n -h -V -f -b -u -c`, `-k N[,M]`, `-t SEP`, `-o FICHIER` | Stable sort with a final byte-by-byte comparison, like GNU sort; combinable options (`-rn`). |
| `head` | `-n N`, `-N`, `-n -N` (all but the last N), `-c N`, `-q`, `-v` | Reads standard input as it comes: `yes \| head` terminates. |
| `tail` | `-n N`, `-N`, `-n +N`, `-c N`, `-f`, `-q`, `-v` | `-f` follows the file (reread every 500 ms) until Ctrl+C; reports `file truncated`. |
| `cut` | `-f LISTE` with `-d SEP`, `-s`, `--output-delimiter`, `-c`/`-b LISTE`, `--complement` | LIST: `N`, `N-M`, `N-`, `-M`, comma-separated. |
| `tee` | `-a` | Copies the input to the output and to each file; an inaccessible file does not stop the others (code 1). |
| `uniq` | `-c -d -u -i`, `[ENTRÉE [SORTIE]]` | Adjacent lines. |
| `printf` | `%s %b %c %d %i %u %o %x %X %e %f %g %%`, flags, width, precision, `*` | Format reused as long as arguments remain; escapes `\n \t \0NNN \xHH \c`. |
| `seq` | `[PREMIER [PAS]] DERNIER`, `-s SEP`, `-w` | Integers or decimals. |
| `basename`, `dirname` | `basename NOM [SUFFIXE]`, `-a`, `-s` | Text only, like coreutils. |

## Files

| Command | Options | Notes |
|---|---|---|
| `mkdir` | `-p`/`--parents`, `-v`/`--verbose`, `--` | Like coreutils: without `-p`, the parent must exist and an existing folder gives "File exists"; with `-p`, each missing component is created (one `mkdir(2)` per component, with the parent's revision), an existing folder is not an error and a file on the path gives "Not a directory". `-v` prints `mkdir: created directory '…'` for each folder created. Mode `0755`. |
| `chmod` | octal or symbolic `MODE` (`+x`, `u+x`, `go-w`, `a=r`, `u=rwx,g=rx,o=`, `g+u`, `X`), `-R`, `-v`, `-c`, `-f` | Without a class letter, the `022` mask applies (`+w` = owner only); special bits (4th digit) are ignored. |
| `ln` | `-s`, `-f`, `-n`, `-v`, `CIBLE [LIEN]`, `CIBLE… DOSSIER` | Symbolic links through `HackFile.CreateSymbolicLinkAsync`; the target text is kept as is. Without `-s`: `Operation not supported` (no hard links in the HackerOS filesystem) and the advice to use `-s`. |
| `locate` | `-i -b -c -e`, `-l N` | No `updatedb` database: live walk with the user's rights, `/proc`, `/dev`, `/sys`, `/run` excluded; a pattern without wildcards is searched anywhere in the path. Code 1 with no result. |
| `du` | `-a -s -h -c -b`, `-d N` | 4 KiB blocks like ext4 (one per folder); display in KiB. |
| `grep` | `-i -n -v`, `-r`/`-R` (recursive, current folder when no file), combinable options (`-rn`) | Prefixes the file name when there are several or with `-r`; symbolic links encountered are not followed. |
| `df` | `-h -T -a` | `HackDriveInfo` disks (`/dev/hda` on `/`): real browser usage and quota since ADR 0099 §2; `-a` adds the pseudo-filesystems of `/proc/mounts`. |
| `zip` | `-r -q -j -d -0…-9` | Real deflate archive readable by any zip tool; updates an existing archive; Unix modes are stored in the external attributes. |
| `unzip` | `-l -t -p -o -n -q -j`, `-d DOSSIER`, `[MOTIFS]` | Restores modes (`+x`); refuses an entry that would escape the folder (`../`, "zip slip"); does not replace a file without `-o`. |
| `xdg-open` | `FICHIER`, `DOSSIER` or `URI` (a single argument) | Like freedesktop (ADR 0096 §3): a path opens with its association (a folder in the file manager, `inode/directory`), a URI with the handler of its scheme (`x-scheme-handler/<scheme>`, the browser for `http`/`https`, the file for `file://`). Returns as soon as it is sent. `xdg-open` codes: 0 sent, 1 command line, 3 no app, 4 missing file or failure; several apps with no default give 4 and the list of candidates (a terminal has no picker and the OS does not guess). `apps.launch`. |
| `xdg-mime` | `query default TYPE`, `query filetype FICHIER`, `default APP TYPE…` | Like freedesktop (ADR 0121 §5): `query default` prints the app that opens this type for this user (nothing without a default); `default` writes the user's choice to `~/.config/hackeros/file-associations.json` (a trailing `.desktop` is ignored; the app must declare the type); `query filetype` gives `inode/directory`, the media type declared for the extension, the extension, or `application/octet-stream`. Only the `open` action. Codes: 0, 1 command line or invalid target, 2 missing file, 4 refusal. `file-associations.read`, `filesystem.user-home.write`. |

## Processes and services

| Command | Options | Notes |
|---|---|---|
| `top` | `-b`, `-n N`, `-d SECONDES`, `-u USER`, `-o CHAMP` | Full screen refreshed every 3 s; keys `q`, `M`, `P`, `N`, `T`. CPU from two reads of `/proc/stat`, memory from `/proc/meminfo`, processes through `HackProcess`. |
| `htop` | `-d DIXIÈMES`, `-u USER`, `-t`, `-C` | Per-core bars, colored memory and swap, scrolling list with selection, tree (`t`), sort (`P`/`M`/`T`/`N`), search (`/`), user (`u`), `k` kills the chosen process (`process.manage`), `q` quits. |
| `nice` | `-n N`, `-N`, `--adjustment=N` | Without a command: prints the shell's priority. Otherwise launches the command with `nice + N` (10 by default, bounded to -20..19). Lowering the value requires root: `sudo nice -n -5 cmd` launches the command as root (ADR 0136), `sudo renice` changes an already launched process. Commands that drive the shell also work (`nice -n 5 nice` prints 5). See [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md). |
| `renice` | `[-n] N`, `-p PID…` | Changes the priority of existing processes; raising is allowed on one's own processes, lowering or touching those of another user requires root (`Permission denied` / `Operation not permitted`). `-g` and `-u` are refused. `process.manage` for a process that is neither one's own nor a child (ADR 0140). |
| `ps` (priority) | `-l`, `-o CHAMPS` | `-l` prints `F S UID PID PPID C PRI NI TIME CMD` (`PRI` = 80 + `NI`, like procps); `-o` chooses among `pid`, `ppid`, `user`, `uid`, `ni`, `pri`, `stat`, `s`, `f`, `c`, `time`, `comm`, `args`; an unknown field exits with 1. `top` shows `PR` and `htop` `PRI` (20 + `NI`, like those tools), plus `NI`. |
| `kill` | `-s SIG`, `-SIG`, `-N`, `-l`, `PID` or `%job` | `KILL` (9) kills immediately, without warning; other signals request a stop (`StopRequested`, reason `Terminated`, ADR 0069) then force after 5 s; `kill -0 PID` tests existence; `%n` goes through the shell (`shell.control`). |
| `jobs` | `-l -p -r -s` | Jobs of the calling shell; finished jobs are shown once. |
| `systemctl` (`service`) | `list-units`, `list-unit-files`, `status`, `start`, `stop`, `restart`, `enable`, `disable`, `mask`, `unmask`, `is-active`, `is-enabled`, `is-failed`, `reset-failed` | The catalog's `Service` apps seen as `name.service` units; automatic = `enabled`, manual = `disabled`, disabled = `masked`. systemd-style states (ADR 0097): `status` prints `Active: active (running)`, `activating (start)`, `deactivating (stop-sigterm)`, `failed (Result: exit-code|exception|killed|timeout) since …`, `Main PID`, the `Status: "…"` line and `Health:` when the service provided them; `is-active` prints the state and exits with 0 only for `active` (3 otherwise); `is-failed` exits with 0 for `failed`; `reset-failed [UNIT]` clears the failure (all units without an argument). A system service (started at boot) can only be driven as an administrator. Acting on another app's service requires `services.manage`. `service NOM ACTION` is accepted. |
| `watch` | `-n SECONDES`, `-t`, `-g`, `-e` | Reruns the line through `IShellSession.RunAsync` (subshell: pipes and variables work) and shows its last output full screen; `q` or Ctrl+C quits. |
| `prlimit` | `--pid PID`, `--cpu`, `--rss`, `--nofile`, `--nproc` (`=soft:hard`, also `-t -m -n -u`), `--noheadings` | Like util-linux: prints or changes a process's limits (ADR 0125, [`process-limits.md`](process-limits.md)); without `--pid`, its own process. Reads `/proc/<pid>/limits` (`process.list`, `filesystem.system.read`); changing another process requires `process.manage`, raising a hard limit requires `sudo`. The shell's `ulimit` built-in does the same for the shell. |
| `dmesg` | `-T`, `-x`, `-l NIVEAU[,NIVEAU…]`, `-k`, `-h` | Like util-linux: reads the session's kernel buffer in `/dev/kmsg` (OOM killer, browser guard; [`oom-killer.md`](oom-killer.md)); `-T` local time (the process's zone, ADR 0193), `-x` level in clear text. |
| `date` | `-u`, `-I`, `-R`, `+FORMAT`, `--help` | Like GNU `date` in the C locale: the OS's time in the process's zone (`TZ`, else Settings ▸ Date and time; ADR 0193, [`time-zone.md`](../framework/time-zone.md)); `-u` UTC, `-I` ISO 8601 date, `-R` RFC 5322, `+FORMAT` with `%Y %y %m %d %e %H %I %M %S %p %Z %z %a %A %b %h %B %j %s %F %T %D %R %n %t %%`. Setting the clock is refused (code 1). |
| `sleep` | `N[smhd]…`, decimals | Ctrl+C interrupts it (130). |
| `stress` | `-c N`, `-m N` (`--vm`), `--vm-bytes B`, `--vm-keep`, `-t SECONDES`, `--yield` | Like `stress(1)`: N compute loops that never yield (`--yield`: with checkpoints); `--vm`: N workers that allocate `B` bytes (`256M` by default) in 1 MiB blocks and touch each page, to test the OOM killer ([`oom-killer.md`](oom-killer.md)). Runs in a Web Worker: the desktop stays fluid, the core goes "not-responding", `kill -9` terminates the worker ([`worker-processes.md`](worker-processes.md)). |
| `whoami` | none | `root` under `sudo`. |
| `which` | `NOM…` | `/usr/bin/NOM` for an installed command, `/bin/sh` for the shell; code 1 otherwise. |

## Security

| Command | Options | Notes |
|---|---|---|
| `ausearch` | `-f CHEMIN`, `-ua`/`-ui`/`-ul UTILISATEUR`, `-c COMM`, `--success yes\|no` (`-sv`), `-if FICHIER`, `-i` | Like the `auditd` tool: searches `/var/log/audit.log` and its archives (ADR 0100 §3, [`sensitive-settings.md`](sensitive-settings.md)). The log is `0600 root`: `sudo ausearch -f /etc/hosts`. `-f` accepts the path, a file below it or a file name; `-ul` searches the original human (`auid`). Code 1 and `<no matches>` with no result. |

## Administration

| Command | Options | Notes |
|---|---|---|
| `localectl` | `status` (default), `list-locales`, `set-locale [VARIABLE=]LOCALE…` | System locale in `/etc/locale.conf` (ADR 0104): `status` prints `System Locale: LANG=…`; `list-locales` lists the 8 known locales (`C.UTF-8`, `en_CA/GB/US`, `fr_BE/CA/CH/FR`, all `.UTF-8`); `set-locale` replaces the file, only as root (`sudo localectl set-locale LANG=fr_CA.UTF-8`), otherwise `Access denied (try sudo)` and code 1. Takes effect on the login screen at next startup and, for users without `~/.config/locale.conf`, at their next login ([`localization.md`](../framework/localization.md)). |
| `hackbackup` | `create`, `restore [--merge\|--replace]`, `-h` | Backup of the whole machine (ADR 0099 §1): must run as root (`sudo hackbackup create`), otherwise `must be run as root` and code 1. `create` downloads `hackeros-AAAAMMJJ-HHMMSSZ.hosbak` to the host computer; `restore` opens the browser's file picker (Enter counts as a gesture), merge by default, then the machine restarts; picker closed or archive refused: code 1; invalid usage: code 2 ([`system-backup.md`](system-backup.md)). |
| `hackpkg` | `install <appId\|repo>[@version] [--user\|--system]`, `remove <appId> [--user\|--system]`, `update [<appId>]`, `list`, `info <appId\|repo>`, `sources [add <source> [--trusted] \| remove <source>]`, `policy [block\|unblock app\|category\|capability <value>]`, `-h` | Apps installed from their GitHub repository (ADR 0182, ADR 0189, [`app-installation.md`](app-installation.md#apps-installed-from-a-github-repository)). `<repo>`: `github:owner/repo`, `owner/repo`, `https://github.com/owner/repo` or `catalog:<name>/<appId>`; an `<appId>` alone is looked up in the `[catalogs]` catalogs, the official one first (ADR 0212). Without an option, an app from a trusted source is installed for oneself (`~/.local/share/hackeros/apps`), without an administrator; `--system` installs it for everyone. An install for everyone, an untrusted source or an app of the block list asks every time, even under `sudo`, for an administrator sign-in then the approval window. `update` keeps each copy where it is; `list` shows `system` or `user`. `sources add|remove` and `policy block|unblock` edit `/etc/hackeros/app-sources.config` and `/etc/hackeros/app-policy.config` (an administrator, nothing under `sudo`). After an install: "restart HackerOS to finish". `list` marks `(restart pending)` an app not yet in the catalog. Refused package: its reason and code 1; invalid usage: code 2. |
| `passwd` | `[NOM]`, `-d NOM`, `-S [NOM]`, `-h` | Changes the password (ADR 0120, [`sensitive-settings.md`](sensitive-settings.md)): `Current password:`, `New password:`, `Retype new password:`, read with echo off; `passwd: password updated successfully`. Wrong current password: `Authentication token manipulation error`, code 10; different confirmation: `Sorry, passwords do not match.`, code 10; fewer than 8 characters: `BAD PASSWORD`, code 1. Another account or `-d` requires `sudo` (`You may not view or modify password information for NOM.`), without asking for the old password; the account's keyring is set aside. `sudo passwd` without a name refuses (no root password). `-S` prints `NOM P\|NP\|L AAAA-MM-JJ 0 99999 7 -1`. On the main thread (terminal input). |

Out of space: a refused write prints `No space left on device`
(`ENOSPC`), a file over 16 MiB `File too large` (`EFBIG`).

## Network

| Command | Options | Notes |
|---|---|---|
| `curl` | `-I -i -v -L -s -f`, `-o FICHIER`, `-O`, `-H "Nom: valeur"`, `-A`, `-X`, `-d`/`--data-raw`/`--data-binary` (`@fichier`), `--data-urlencode`, `--json` | `-o`, `-O`, `-H`, `-A`, `-s` added by ADR 0064; raw bodies, `-i`, `--json`, `--data-*` and optional `-f` since `web/2-http-bodies` (ADR 0074 §1): without `-f`, an HTTP error prints the body and exits with 0, like the real `curl` ([`http-bodies.md`](../framework/http-bodies.md)). |
| `wget` | `-O FICHIER` (`-O -` = output), `-P DOSSIER`, `-q`, `-S`, `--spider` | Never replaces: `fichier.1`, `fichier.2`…; progress on the error output. |
| `ip` (`ifconfig`) | `addr`, `link`, `route`, `neigh`, `-br`, `-s`, `dev NOM` | `lo` 127.0.0.1/8 and `eth0` 192.168.1.100/24 behind the simulated network's router 192.168.1.1, stable MAC derived from the machine name; real counters from `/proc/net/dev`. |
| `netstat` (`ss`) | `-t -u -l -n -a -p`, `-i`, `-r`, `-s` | Listening ports come from the OS port table (`HackIPGlobalProperties`, since `web/3`): simulated `localhost` daemons and app sockets; `-p` shows `PID/program` for root or the same user, otherwise `-` and the net-tools note; `-i` and `-s` read `/proc/net/dev`. |
| `nslookup` | `<name>` or `<address>` | Queries `hackdns` (`web/3`): indicates the source (`/etc/hosts`, loopback, web app, hosted site or simulated network); `NXDOMAIN` and code 1 if the name is unknown ([`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)). |

## Scripts

| Command | Notes |
|---|---|
| `test`, `[` | `-e -f -d -s -r -w -x -L -h`, `-n -z`, `= == != < >`, `-eq -ne -lt -le -gt -ge`, `-nt -ot -ef`, `! -a -o ( )`; `[` requires `]` (the shell binds `[` to `test`). Code 2 on a usage error. |
| `true`, `false` | Codes 0 and 1. |
| `read` | `-r -p -s -n -t -d`, `NOM…` (otherwise `REPLY`); splits on `IFS`, the last name takes the rest; assigns variables of the calling shell (`shell.control`), hence lost in a pipe as in bash; code 1 at end of input, 142 on timeout. |

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.Commands.cs`:
  `test`/`[`, `read` with `while read`, `printf`, `seq`, `cut | sort | uniq -c`,
  `sort -k -t -n -r`, `tail -n +N`, `head -c`, `tee`, `chmod +x` then
  `./script`, symbolic modes, `ln -s` and refusal of hard links,
  `zip`/`unzip` with modes, `whoami`, `basename`, `dirname`, `which`,
  `du`, `locate`, `jobs` and `kill %1`, `kill PID` and `kill -9 PID` on a real
  process, `grep -r`, `ip`, `top -b`.
- `Tests/HackerOs.Commands.Wave5.Tests`: `chmod`, `kill`, `sort`, `head`,
  `tail`, `mkdir` (`-p`, `-v`, existing folder, file on the path, `..`), `hackbackup` and `hackpkg` with fake gateways.
- `Tests/HackerOs.Commands.Tests/XdgOpenCommandTests.cs`: relative path, folder, missing file, no app, several apps, URI, unknown scheme, command line, refusal (`v3/4`). The parity scenario (`CommandParityTests`) compares the command in a Web Worker and on the main thread.
- `Tests/HackerOs.Ecosystem.Tests`: 94 built-in apps including 75 commands (`stress` added by `rebuild/5-worker-processes`, `nslookup` by `web/3`, `ausearch` by `v3/8-settings-security`, `xdg-open` by `v3/4`, `hackbackup` by `v3/7`, `localectl` by `v3/12`, `xdg-mime` by `v3/4b`, `passwd` by `v3/8`, `prlimit` by `exec/2`, `nice` and `renice` by `exec/4`, `dmesg` by `exec/3`, `hackpkg` by ADR 0182).
- `Tests/HackerOs.Commands.Tests`: `ausearch` filters; `LocalizedCommandsTests` and `LocalizedCommandsMzTests` run commands with `LANG=fr_FR.UTF-8` (`mkdir`, `rm`, `seq`, `sleep`, `printf`, `test`, `sudo`, `xdg-open`…), verify the same keys in English and French and the French description and usage of the 75 commands generated in `localization/`. `NanoCommandTests` draws the `nano` bars in French, `LocalizationShellTests` (WorkerHost) goes through the real shell.

## Task list

- [x] `sort`, `head`, `tail` (`-f`), `kill` (`%job`, signals), `curl` (`-o`, `-O`, `-H`) reviewed.
- [x] Symbolic and recursive `chmod` (`chmod +x`).
- [x] `cut`, `tee`, `uniq`, `printf`, `seq`, `basename`, `dirname`.
- [x] `ln -s` and `HackFile.CreateSymbolicLinkAsync`.
- [x] `locate`, `du`, `df`, `zip`, `unzip`.
- [x] `top`, `htop` (colors in full-screen frames), `watch`, `jobs`, `sleep`, `whoami`, `which`.
- [x] `mkdir -p` and `-v` (`rebuild/5-ui-a11y-mkdir`, 2026-10-16).
- [x] `systemctl`/`service`.
- [x] `wget`, `ip`/`ifconfig`, `netstat`/`ss`.
- [x] `test`/`[`, `true`, `false`, `read`.
- [x] Command messages in English and French, description and usage translated (`v3/12`).
- [x] `ausearch` (`v3/8-settings-security`, ADR 0100).
- [x] `xdg-open` (`v3/4-intents`, ADR 0096).
- [x] `xdg-mime` (`v3/4b`, ADR 0121).
- [x] Tests and documentation.
- [x] ADR 0069 (`rebuild/5-graceful-stop`): `find`, `grep` (and `grep -r`), `du`, `zip`, `unzip`, `locate` call `HackProcess.CheckpointAsync` in their loops; `kill` sends `Terminated`.
- [x] `hackbackup` and `ENOSPC`/`EFBIG` messages (`v3/7-storage-backup`, ADR 0099).
- [x] `hackpkg`: install apps from their GitHub repository (ADR 0182).
- [x] `hackpkg`: `--user`, `--system`, `sources add/remove`, `policy`, `--allow-untrusted` removed (ADR 0189, ADR 0208).
- [x] `prlimit` and `ulimit` built-in; `cat`, `grep` and `wc` say "Too many open files" on `EMFILE` (`exec/2-process-limits`, ADR 0125 and 0139).
- [x] `dmesg` and `stress --vm`/`--vm-bytes`/`--vm-keep` (`exec/3-oom-killer`, ADR 0126 and 0142, [`oom-killer.md`](oom-killer.md)).
- [x] `nice`, `renice`, `ps -l`/`-o`, `PR`/`NI` columns of `top` and `htop` (`exec/4-not-responding-ui`, ADR 0127, [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)).
