# ADR 0130 : `HackerOs.Server` sert l'OS en WebAssembly et ne garde que son API

## Statut

Accepté le 2026-10-18. Deux questions sont posées à Benoit (§2 et §4) ; ce qui
suit applique la réponse recommandée en attendant.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  décision 8 (réponse Q7 de Benoit : « Server = OS en WebAssembly + API ») ;
  mis en œuvre par `exec/8-server-wasm`.
- **Remplace :** l'[ADR 0027](0027-server-hosted-blazor-ui.md) (troisième hôte
  en Interactive Server) et l'[ADR 0036](0036-direct-service-injection-for-server-host.md)
  (injection directe des services du serveur dans l'interface).
- **Rend sans objet :** l'ADR 0066 §3 (mesures coupées par défaut sur
  l'hôte Server), l'ADR 0067 §6, l'ADR 0070 §6, l'ADR 0072 §7 (pas de worker
  sur cet hôte) et l'ADR 0108 §5 (flux du client direct sans WebSocket).

## Contexte

Aujourd'hui (`Server/HackerOs.Server/Program.cs`), le serveur rend toute
l'interface de l'OS **côté serveur** (`AddInteractiveServerComponents`,
`Components/App.razor`), un seul circuit à la fois, et l'OS y appelle les
services du serveur en direct (`DirectServerConnectionClients`). Le code des
apps tourne donc sur des threads du serveur : pas de Web Worker, pas de
mesure fiable, et un mode d'exécution différent des deux autres hôtes.

Benoit pensait que le serveur hébergeait déjà le client WebAssembly et
n'exécutait que ce qui doit l'être (relais, synchronisation,
authentification). C'est ce que cet ADR décide.

## Décision

### 1. Le serveur envoie le même OS que l'Ecosystem

- Le navigateur reçoit l'OS **en WebAssembly** depuis le serveur et
  l'exécute comme l'Ecosystem : mêmes workers, mêmes mesures, même
  `/proc/hackeros`, même mode hors ligne.
- Le serveur garde ses points d'API (identité, synchronisation, relais HTTP,
  TCP et UDP, administration) sans changement de contrat.
- L'OS parle au serveur **par HTTP et WebSocket**, comme l'Ecosystem connecté
  à un serveur (ADR 0028, 0102, 0108).

### 2. Comment le serveur sert l'OS (question posée à Benoit)

Appliqué en attendant (recommandé) : **les fichiers publiés de l'Ecosystem**,
tels quels. Le serveur référence `OS/HackerOs.Ecosystem` comme client
WebAssembly hébergé, sert `_framework/`, `index.html`, le manifeste PWA et le
service worker de l'Ecosystem (`MapStaticAssets`, repli sur `index.html`), et
n'a plus aucun composant Razor.

- Avantage : un seul démarrage à maintenir (`index.html` de l'Ecosystem),
  PWA et service worker identiques, isolation cross-origin de l'ADR 0131
  servie de la même façon.
- L'autre choix : une Blazor Web App en `InteractiveWebAssembly` avec son
  propre `App.razor`, comme `test/test`. Il garde un second démarrage à
  maintenir et un service worker différent, pour un prérendu que l'OS n'utilise
  pas (il dépend d'IndexedDB). Si Benoit le choisit, un ADR court remplace ce
  paragraphe.

### 3. Ce qui est retiré du serveur

`AddInteractiveServerComponents`, `MapRazorComponents<App>`,
`Components/App.razor`, `Components/Pages/Home.razor`,
`DirectServerConnectionClients`, `InProcessAssemblyTransport`,
`AspNetCoreEcosystemHostEnvironment`, les références aux projets des apps
système (elles viennent avec l'Ecosystem). Les `TryAddSingleton` de
`AddHackerOsEcosystem` posés pour l'ADR 0036 restent : ils ne coûtent rien.

### 4. L'OS reconnaît le serveur qui l'a servi (question posée à Benoit)

Appliqué en attendant (recommandé) : au démarrage, l'OS interroge
`GET /api/hackeros/host-info` sur sa propre origine (délai court, ignoré hors
ligne). Si le serveur répond, Paramètres › Serveur est **prérempli** avec
cette adresse et propose de se connecter ; l'utilisateur décide. L'OS ne
dépend jamais du serveur (AGENTS.md I.1).

L'autre choix : rien de spécial, l'utilisateur saisit l'adresse comme avec
l'Ecosystem. L'option « connexion obligatoire » n'est pas proposée : elle
contredirait la règle « rien ne doit exiger le serveur ».

### 5. Hôtes après le changement

| Hôte | Rôle |
|---|---|
| `OS/HackerOs.Ecosystem` | PWA autonome, hors ligne, sans serveur (inchangé). |
| `test/test` | Débogage interactif des composants WebAssembly (inchangé). |
| `Server/HackerOs.Server` | Sert l'OS de l'Ecosystem **et** l'API optionnelle. Plus de rendu côté serveur. |

## Conséquences

- `build/verify-hosts.sh` et `HackerOs.HostSmoke.Tests` : l'hôte Server a des
  workers et `/proc/hackeros/workers` ; `kill -9` d'une boucle y est vérifié.
- Les tests du serveur qui passaient par le client direct passent par HTTP.
- Docs : `AGENTS.md` (section des hôtes), [`project-overview.md`](../project-overview.md),
  [`building-and-testing.md`](../building-and-testing.md),
  [`old/hosting-model.md`](../old/hosting-model.md) (bandeau vers cet ADR),
  [`server-network-relay.md`](../server-network-relay.md),
  [`worker-processes.md`](../worker-processes.md).
- Plusieurs utilisateurs peuvent ouvrir l'OS depuis le même serveur : chacun a
  son OS dans son navigateur. L'API reste celle d'aujourd'hui (mono-locataire
  pour l'administration, ADR 0102).

## Références

- ADR 0027, 0028, 0036, 0066, 0067, 0070, 0072, 0102, 0108, 0131.
- Hébergement d'un client Blazor WebAssembly par ASP.NET Core.
