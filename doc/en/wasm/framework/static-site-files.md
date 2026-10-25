> [Version française](../../../fr/wasm/framework/static-site-files.md)

# Static site files (`.site`) and the `HackWebSiteFile` API

This document describes the format of the `.site` files that an app or a tool writes to publish a folder as a website, and the SDK API that reads and writes them. The `hackwebd` service that serves these files, its logs and Web Server Manager are described in [`dev/static-sites.md`](../dev/static-sites.md).

## Locations

- `/etc/hackweb/sites/<name>.site`: system sites, any port, served with the rights of `www-data`.
- `~/.config/hackweb/sites/<name>.site`: user sites, ports 1024 and above.

## SDK types

| Item | Project | Role |
|---|---|---|
| `HackWebSite`, `HackWebSiteDeclaration`, `HackWebSiteScope` | `Shared/HackerOs.AppSdk/Net/Hosting` | Content of a `.site` and the file read (path, scope, owner, errors). |
| `HackWebSiteFile` | same | Locations, `Parse`, `Validate`, `Serialize`, `TryParseBinding`, `ReadAllAsync`. Shared by the daemon, the Manager and the resolver. |

## The `.site` file

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

- The file is named `<name>.site`; otherwise the site is invalid.
- Only `name`, `root` and `bindings` are required; `//` comments
  and trailing commas are tolerated (hand-edited file).
- `root`: absolute path, neither `/` nor `..`.
- `bindings`: `http://host[:port]/path/` prefixes normalized like those of
  `HackHttpListener` (`+`, `*` accepted). `https://` refused (no
  certificates, ADR 0080). A user site cannot take a port
  < 1024: the error goes to `error.log`.
- An invalid or disabled site is not served; it stays listed (state
  "invalid" or "stopped") in the Manager.

## Read permissions

- Rights: a user site is read with the rights of its owner;
  a system site applies those of `www-data` ("other" bits on every
  folder and on the file): a system site in a `0700` home folder
  answers `403`, like Apache.

## Reading a `.site` with the SDK

For an app developer, the format is in the SDK:

```csharp
IReadOnlyList<HackWebSiteDeclaration> sites = await HackWebSiteFile.ReadAllAsync();
HackWebSiteDeclaration parsed = HackWebSiteFile.Parse(path, json, HackWebSiteScope.User, "alice");
IReadOnlyList<string> errors = HackWebSiteFile.Validate(site, HackWebSiteScope.User);
```
