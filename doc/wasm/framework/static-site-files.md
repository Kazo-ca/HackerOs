# Fichiers de site statique (`.site`) et API `HackWebSiteFile`

Ce document décrit le format des fichiers `.site` qu'une app ou un outil écrit pour publier un dossier comme site web, et l'API du SDK qui les lit et les écrit. Le service `hackwebd` qui sert ces fichiers, ses journaux et Web Server Manager sont décrits dans [`dev/static-sites.md`](../dev/static-sites.md).

## Emplacements

- `/etc/hackweb/sites/<nom>.site` : sites système, tous ports, servis avec les droits de `www-data`.
- `~/.config/hackweb/sites/<nom>.site` : sites de l'utilisateur, ports 1024 et plus.

## Types du SDK

| Élément | Projet | Rôle |
|---|---|---|
| `HackWebSite`, `HackWebSiteDeclaration`, `HackWebSiteScope` | `Shared/HackerOs.AppSdk/Net/Hosting` | Contenu d'un `.site` et fichier lu (chemin, portée, propriétaire, erreurs). |
| `HackWebSiteFile` | idem | Emplacements, `Parse`, `Validate`, `Serialize`, `TryParseBinding`, `ReadAllAsync`. Partagé par le démon, le Manager et le résolveur. |

## Le fichier `.site`

```json
{
  "name": "docs",
  "root": "/home/benoit/www/docs",
  "bindings": ["http://docs.hack:8080/", "http://localhost:8081/"],
  "defaultDocuments": ["index.html", "index.htm", "default.htm"],
  "directoryBrowsing": false,
  "enabled": true
}
```

- Le fichier s'appelle `<name>.site` ; sinon le site est invalide.
- Seuls `name`, `root` et `bindings` sont obligatoires ; les commentaires `//`
  et les virgules finales sont tolérés (fichier édité à la main).
- `root` : chemin absolu, ni `/`, ni `..`.
- `bindings` : préfixes `http://hôte[:port]/chemin/` normalisés comme ceux de
  `HackHttpListener` (`+`, `*` acceptés). `https://` refusé (pas de
  certificats, ADR 0080). Un site d'utilisateur ne peut pas prendre un port
  < 1024 : l'erreur va dans `error.log`.
- Un site invalide ou désactivé n'est pas servi ; il reste listé (état
  « invalid » ou « stopped ») dans le Manager.

## Droits de lecture

- Droits : un site d'utilisateur est lu avec les droits de son propriétaire ;
  un site système applique ceux de `www-data` (bits « other » sur chaque
  dossier et le fichier) : un site système dans un dossier personnel `0700`
  répond `403`, comme Apache.

## Lire un `.site` avec le SDK

Pour un développeur d'app, le format est dans le SDK :

```csharp
IReadOnlyList<HackWebSiteDeclaration> sites = await HackWebSiteFile.ReadAllAsync();
HackWebSiteDeclaration parsed = HackWebSiteFile.Parse(path, json, HackWebSiteScope.User, "alice");
IReadOnlyList<string> errors = HackWebSiteFile.Validate(site, HackWebSiteScope.User);
```
