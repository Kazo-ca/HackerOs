# OS servi par `HackerOs.Server` et connexion automatique

Chantier `exec/8-server-wasm` (série `exec`), ADR
[0130](adr/0130-server-serves-webassembly-os-and-api.md),
[0134](adr/0134-automatic-server-connection-and-status-icon.md) et
[0137](adr/0137-server-hosting-implementation-adjustments.md).

## But

- `HackerOs.Server` sert **le même OS que la PWA** : les fichiers publiés de
  `OS/HackerOs.Ecosystem`, tels quels. L'OS tourne dans le navigateur en
  WebAssembly, avec ses Web Workers, ses mesures et `/proc/hackeros`. Le
  serveur ne garde que son API (comptes, synchronisation, relais réseau,
  administration).
- L'OS servi par un serveur **s'y connecte tout seul**, sans question, et une
  icône de la zone de notification montre l'état. Il continue de marcher sans
  serveur (AGENTS.md I.1).

## Architecture

```text
 Navigateur                                        HackerOs.Server
 ──────────                                        ───────────────
 index.html, _framework/, apps  ◄── MapStaticAssets ── fichiers publiés de l'Ecosystem
 GET hackeros-host.json         ◄── middleware ──────── {"isServer":true, ...}
 ServerPresenceService
   GET /api/hackeros/host-info  ◄── VersionEndpoints ── version de l'API
   comptes, synchro, relais     ◄── API (HTTP, WebSocket)
 ServerStatusIndicator (barre des tâches, zone de notification)
```

| Partie | Fichier |
|---|---|
| Pipeline du serveur | `Server/HackerOs.Server/Program.cs` |
| Réécriture des pages vers `index.html` | `Server/HackerOs.Server/Endpoints/OsPageRewrite.cs` |
| `host-info` et `hackeros-host.json` | `Server/HackerOs.Server/Endpoints/VersionEndpoints.cs` |
| Contrat | `Server/HackerOs.Server.Contracts/Hosting/HostInfoContracts.cs` |
| Fichier « pas de serveur » de la PWA | `OS/HackerOs.Ecosystem/wwwroot/hackeros-host.json` |
| Détection et vérification | `Platform/HackerOs.Platform.Core/ServerConnection/HostInfoClient.cs` |
| État et reprise | `Platform/HackerOs.Platform.Core/ServerConnection/ServerPresenceService.cs` |
| Icône et panneau | `Platform/HackerOs.Platform.Blazor/Shell/ServerStatusIndicator.razor` (`.razor.cs`, `.razor.css`) |
| Zone de notification | `Platform/HackerOs.Taskbar.Blazor/Taskbar.razor` (`NotificationAreaContent`) |
| Composition | `OS/HackerOs.Ecosystem.Host/EcosystemServiceCollectionExtensions.cs`, `HackerOsBrowserHostExtensions.cs` |

## Démarrer le serveur

```bash
dotnet run --project Server/HackerOs.Server   # http://localhost:5000 par défaut
```

Le navigateur ouvre l'adresse du serveur et reçoit l'OS. Un intergiciel posé
juste après `builder.Build()` ajoute `Cross-Origin-Opener-Policy` et
`Cross-Origin-Embedder-Policy: credentialless` à toutes les réponses, selon
`HackerOs:CrossOriginIsolation` de l'`appsettings.json` du serveur (`auto` par
défaut ; `off` n'ajoute rien) : la page est isolée (ADR 0131, 0143). En production,
`dotnet publish Server/HackerOs.Server` copie les fichiers de l'Ecosystem dans
son `wwwroot`. Toute adresse de page (`/`, un lien profond) reçoit
`index.html` ; `/api/...`, `/health`, `/openapi`, `/scalar` et les fichiers ne
sont jamais réécrits.

## Connexion automatique

1. Au démarrage du bureau, l'OS lit `/etc/hackeros/server.config`. Si
   `auto-connect` n'est pas `false` (ou `no`, `off`, `0`), il lit
   `hackeros-host.json` sur sa propre origine : la PWA répond « pas de
   serveur », `HackerOs.Server` répond « serveur ».
2. Le serveur retenu est celui du compte lié à l'appareil (Paramètres, ADR
   0028), sinon celui qui a servi l'OS.
3. L'OS appelle `GET /api/hackeros/host-info` : réponse en API 1.x =
   connecté ; autre version = **Erreur** ; pas de réponse = **Hors ligne**,
   nouvel essai après 5, 10, 20, 40 puis 60 s. Connecté, il revérifie toutes
   les 30 s.

```ini
# /etc/hackeros/server.config (facultatif, administrateur)
[server]
auto-connect=false
```

## Icône d'état

| État | Icône | Sens |
|---|---|---|
| Connecté | nuage coché, vert | le serveur répond, l'appareil a un compte |
| Connecté, non identifié | nuage, bleu | le serveur répond, pas encore de compte |
| Connexion… | cercle qui tourne | vérification en cours |
| Hors ligne | nuage barré, orange | serveur injoignable, l'OS continue |
| Erreur | nuage avec alerte, rouge | version d'API incompatible ou réponse invalide |

Sans serveur, l'icône n'apparaît pas. Un clic ouvre le panneau : état,
adresse, compte, dernière synchronisation, détail d'une erreur, et les boutons
« S'identifier » (nom du compte et mot de passe, « Se connecter » ou « Créer
un compte »), « Reconnecter » et « Paramètres du serveur ». Échap ferme le
panneau ; le changement d'état est annoncé (`aria-live`).

## Comptes depuis plusieurs appareils

Le sel PBKDF2 du client est dérivé du nom du compte et la connexion enregistre
un nouvel appareil : on peut créer le compte dans un navigateur et s'y
connecter depuis un autre (ADR 0137 §4). Un compte créé avant ce changement
ne peut plus se connecter par mot de passe.

## Décisions clés

- Fichiers publiés de l'Ecosystem servis tels quels, sans second démarrage à
  maintenir (ADR 0130 §2, E1 confirmé par Benoit).
- Connexion sans question, icône dans la zone de notification (ADR 0134, E2
  de Benoit).
- Détection par un fichier qui existe partout, pour ne jamais écrire
  d'erreur dans la console d'une PWA sans serveur (ADR 0137 §1).

## Tests

- `Tests/HackerOs.Server.Tests/ServerOsHostingIntegrationTests.cs` : page de
  l'OS sur `/` et un lien profond, 404 d'une route d'API inconnue, détection
  sur les deux adresses, compte créé puis utilisé depuis un deuxième appareil,
  mauvais mot de passe refusé.
- `Tests/HackerOs.Platform.Core.Tests/ServerConnection/ServerPresenceServiceTests.cs` :
  connexion sans compte et sans question, hôte statique sans icône, serveur lié
  à la main, hors ligne puis reprise avec délai croissant, API incompatible,
  « S'identifier », `auto-connect=false`, sel identique d'un appareil à l'autre.
- `build/verify-hosts.sh` (`Tests/HackerOs.HostSmoke.Tests`) : parcours
  `server` avec workers, mesures et icône ; aucune icône sur les autres.

## Liste des tâches

- [x] Servir les fichiers publiés de l'Ecosystem depuis `HackerOs.Server`.
- [x] Retirer Interactive Server, clients directs et références aux apps.
- [x] Réécrire les pages de l'OS vers `index.html` dans tous les environnements.
- [x] `host-info` et `hackeros-host.json` (serveur et PWA).
- [x] `ServerPresenceService` : détection, vérification, reprise, `auto-connect`.
- [x] Icône et panneau dans la zone de notification de la barre des tâches.
- [x] Sel du client stable et appareil enregistré à la connexion.
- [x] Tests du serveur, de Platform Core et parcours des trois hôtes.
- [x] Docs, ADR 0137 et index.
