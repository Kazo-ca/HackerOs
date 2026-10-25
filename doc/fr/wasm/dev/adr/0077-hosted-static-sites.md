> [English version](../../../../en/wasm/dev/adr/0077-hosted-static-sites.md)

# ADR 0077 : sites statiques hébergés, service `hackwebd` et Web Server Manager

## Statut

Accepté le 2026-10-16 (proposé le même jour, puis validé par Benoit avec le
questionnaire de la section 4 du plan).

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Dépend de :** ADR 0074 (ports, résolveur), ADR 0075 (rendu HTML).
- **Suit :** ADR 0044 (paramètres en fichiers).

## Contexte

Benoit veut pouvoir déclarer un site web statique à partir du système de
fichiers et le gérer avec une app façon IIS, appuyée sur un service d'arrière-plan
émulé qui ne fait que piloter l'OS par le SDK. Les fichiers locaux sont servis
sur `localhost`.

## Décision

### 1. Déclarations en fichiers

Un site hébergé est un fichier `/etc/hackweb/sites/<nom>.site` (JSON), modifiable
par l'administrateur, comme `applicationHost.config` d'IIS ou
`sites-available` de nginx :

```json
{
  "name": "docs",
  "root": "/home/benoit/www/docs",
  "bindings": ["http://localhost:8080/", "http://docs.hack/"],
  "defaultDocuments": ["index.html", "index.htm"],
  "directoryBrowsing": false,
  "enabled": true
}
```

Un nom autre que `localhost` dans `bindings` est publié par `hackdns`
(ADR 0074 §4).

Qui peut déclarer un site :

| Où | Qui | Ports |
|---|---|---|
| `/etc/hackweb/sites/` | l'administrateur (approbation) | tous, y compris < 1024 |
| `~/.config/hackweb/sites/` | tout utilisateur, pour lui-même | 1024 et plus seulement |

C'est la différence entre Apache configuré par l'administrateur et un
`python -m http.server` lancé par un utilisateur. `hackwebd` lit les deux
emplacements ; un site d'utilisateur qui demande un port < 1024 est refusé et
l'erreur va dans `/var/log/hackweb/error.log`.

### 2. Le service `hackwebd`

App système de genre `Service` (worker dédié), démarrée au boot :

- lit `/etc/hackweb/sites/`, surveille le dossier (`HackFileSystemWatcher`) et
  applique les changements à chaud ;
- ouvre un `HackHttpListener` par liaison ; sert les fichiers avec les droits du
  compte de service `www-data` (lecture seule sur `root`) : types MIME,
  documents par défaut, liste du dossier si permise, `304` par `ETag` et
  `Last-Modified`, `404` et `403` ;
- journalise les accès dans `/var/log/hackweb/<nom>.access.log` et les erreurs
  dans `/var/log/hackweb/error.log` (ADR 0045) ;
- n'utilise que le SDK public : ni Platform Core, ni service interne.

### 3. L'app Web Server Manager

App à fenêtre qui pilote `hackwebd` par les fichiers de §1 et
`HackServiceController` (comme `systemctl`) : liste des sites et de leur état, création depuis un
dossier (sélecteur de dossier), liaisons, documents par défaut, démarrer/arrêter
un site, redémarrer le service, ouvrir le site dans le navigateur, consulter ses
journaux. Un site d'utilisateur s'écrit sans approbation dans
`~/.config/hackweb/sites/` ; écrire dans `/etc/hackweb` exige une capacité dangereuse
(`filesystem.system.write`, approbation administrateur).

L'explorateur gagne « Publier ce dossier comme site » qui ouvre le Manager
prérempli.

### 4. Sites des projets

Les sites des apps `Web` (ADR 0073) ne passent pas par `hackwebd` : ils sont
servis par leur propre processus. Le Manager les liste en lecture seule
(« externes »), avec leurs URL.

## Conséquences

- Nouvelle app système `hackwebd` et nouvelle app « Web Server Manager »
  (catalogue, `IsSystemApp`).
- Le compte de service `www-data` est ajouté aux comptes système de l'OS (il
  n'existe pas encore).
- `systemctl status hackwebd` affiche le service comme les autres.
