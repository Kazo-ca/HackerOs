> [English version](../../../en/wasm/framework/time-zone.md)

# Heure locale et fuseau horaire

L'OS garde son heure en UTC (l'horloge de la simulation, `IAppExecutionContext.Clock`)
et l'affiche dans le fuseau de l'utilisateur. Décision : [ADR 0193](../dev/adr/0193-local-time-and-time-zone.md).

## Le réglage

| Couche | Fichier | Clé |
| --- | --- | --- |
| Machine (aussi l'écran de connexion) | `/etc/hackeros/datetime.config` | `timeZone` |
| Utilisateur (gagne) | `~/.config/hackeros/datetime.config` | `timeZone` |

`timeZone` vaut `auto` (le fuseau de l'appareil, donné par le navigateur) ou un
identifiant IANA (`America/Toronto`, `Europe/Paris`, `UTC`). Un nom inconnu compte
comme `auto`. Paramètres ▸ Date et heure (`--page=datetime`) écrit la couche
utilisateur ; l'horloge suit tout de suite.

## Le fuseau d'un processus

1. la variable `TZ` du processus, comme sous Linux (`TZ=Asia/Tokyo date`,
   `export TZ=UTC`, préfixe `:` accepté) ;
2. sinon le fuseau de la session (`SessionTimeZoneService`), lu à chaque accès ;
   un processus dans un web worker reçoit celui du moment de son démarrage ;
3. sinon le fuseau de l'appareil.

## Dans une app

```csharp
using HackerOs.AppSdk.Globalization;

TimeZoneInfo zone = AppContext.TimeZone;                // ou HackTimeZone.Current
DateTimeOffset now = HackTimeZone.Now;                  // heure de l'OS dans ce fuseau
string modified = HackTimeZone.ToLocal(entry.ModifiedAtUtc).ToString("g", AppContext.FormatCulture);
string offset = HackTimeZone.FormatOffset(zone, now);   // "UTC-04:00"
```

- N'utilisez pas `DateTimeOffset.ToLocalTime()` ni `TimeZoneInfo.Local` : ils
  ignorent le choix de l'utilisateur.
- Le format (12 h ou 24 h, ordre jour/mois) vient de `FormatCulture` (`LC_TIME`,
  [`localization.md`](localization.md)).
- `HackTimeZone.Available` liste les fuseaux IANA `Région/Ville` et `UTC`, triés ;
  `TryFind` et `Resolve` lisent un nom ; `GetId` donne l'identifiant IANA.
- Aucune capacité n'est nécessaire.

## Dans le terminal

- `date` : heure locale (`Thu Oct 29 10:05:09 -04 2026`) ; `-u` UTC ; `-I` date
  ISO 8601 ; `-R` RFC 5322 ; `+FORMAT` (`%F %T %Z %z %a %b %j %s`…).
- `ls -l` et `dmesg -T` affichent l'heure dans le fuseau du processus.
- Les fichiers `/var/log/*` restent en UTC (`2026-10-29T14:05:09.000Z`).

## Plateforme

- `SessionTimeZoneService` (Platform Core) : `TimeZone`, `Setting`, `ToLocal`,
  `Changed`, initialisé au démarrage avec les préférences d'accessibilité.
- `ITaskbarStatusSource.TimeZone` : le fuseau de l'horloge de la barre (UTC par
  défaut pour un hôte qui n'en donne pas).
