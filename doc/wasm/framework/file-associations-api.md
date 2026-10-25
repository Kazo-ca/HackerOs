# Associations de fichiers par utilisateur (API et formats)

Ce document décrit ce qu'une app ou un tiers voit des associations de fichiers : le format des deux fichiers, l'ordre de résolution, l'API `HackApps` et les capacités. La mise en œuvre (couches, index, panneau des Paramètres, « Ouvrir avec », `xdg-mime`, tests) est dans [`../dev/user-file-associations.md`](../dev/user-file-associations.md).

## Fichiers et format

| Où | Fichier | Qui l'écrit |
|---|---|---|
| Machine | `/etc/hackeros/file-associations.json` | un administrateur (`sudo nano`) |
| Utilisateur | `~/.config/hackeros/file-associations.json` | l'utilisateur : Paramètres, « Ouvrir avec », `xdg-mime default`, SDK, `nano` |

Les deux fichiers ont le même schéma :

```json
{"schemaVersion":1,"associations":[
  {"extension":".md","appId":"org.hackeros.code-editor","actions":["open"]},
  {"mediaType":"x-scheme-handler/https","appId":"com.example.browser","actions":["open"]}
]}
```
## Ordre de résolution

1. L'app demandée par l'intent (`PreferredAppId`).
2. Le choix de **l'utilisateur qui exécute l'intent** (le principal ; `sudo -u
   bob xdg-open` lit le fichier de `bob`).
3. Le défaut de la machine.
4. La seule app qui déclare le type, sinon le sélecteur (ou le code 4 de
   `xdg-open` dans un terminal).

Une entrée qui nomme une app inconnue ou désinstallée est sautée : un choix
périmé retombe sur le défaut de la machine. Un fichier utilisateur invalide est
ignoré en entier et jamais réécrit. `open` et `edit` se choisissent
séparément. L'ordre vaut pour `OpenFile`, `EditFile`, `OpenUri`, `RevealFile`
(`inode/directory`) et « Ouvrir avec » (le défaut en tête).
## API du SDK

| Membre | Rôle | Capacités |
|---|---|---|
| `HackApps.GetDefaultHandlersAsync()` | Lignes `HackFileAssociation(Target, Action, AppId, Layer)` : celles de l'utilisateur, puis celles de la machine. La première ligne d'une cible et d'une action est celle utilisée. | `file-associations.read` |
| `HackApps.SetDefaultHandlerAsync(target, appId, action = "open")` | Écrit le choix de l'utilisateur. L'app doit déclarer la cible et l'action. | `file-associations.read`, `filesystem.user-home.write` |
| `HackApps.ResetDefaultHandlerAsync(target, action = "open")` | Retire le choix de l'utilisateur. | idem |

`HackDefaultHandlerStatus` : `Changed`, `InvalidTarget`, `InvalidAction`,
`AppNotFound`, `AppCannotHandle`, `FileInvalid`, `WriteFailed`. Une cible est
`.ext` ou `type/sous-type`, mise en minuscules. Une passerelle antérieure à
l'ADR 0121 lève `NotSupportedException`.

```csharp
HackDefaultHandlerStatus status = await HackApps.SetDefaultHandlerAsync(".md", "org.hackeros.code-editor");
await HackApps.SetDefaultHandlerAsync("x-scheme-handler/https", "com.example.browser");
await HackApps.ResetDefaultHandlerAsync(".md");
```

## Capacités

- Pas de nouvelle capacité : écrire son fichier demande
  `filesystem.user-home.write`, ce que l'app pourrait faire de toute façon.
