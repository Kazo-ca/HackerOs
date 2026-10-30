> [English version](../../../en/wasm/dev/installable-server.md)

# Serveur local installable

Issue `TODO` #267, [ADR 0191](adr/0191-installable-local-server.md). Série
`todo/base`, chantier `todo/14-installable-server`.

## But

Le serveur HackerOS optionnel (`HackerOs.Server` : relais réseau HTTP, TCP et
UDP, comptes, appareils, synchronisation) s'installe sur l'ordinateur de
l'utilisateur comme un **service** qui démarre avec la machine, sans .NET
installé ni ligne de commande à connaître. L'OS reste utilisable sans lui
(GitHub Pages, PWA) ; il lui donne le vrai réseau et la synchronisation.

- Par défaut le serveur n'écoute que **sur cet ordinateur** :
  `http://localhost:5888`.
- Sur demande (`--lan` / `-Lan`), il écoute aussi **sur le réseau local en
  HTTPS** : `https://<adresse>:5889`, avec un certificat signé par une
  autorité de certification locale propre à cette installation.
- Rien n'est exposé à Internet.
- Un appareil se relie au serveur avec un **code d'appairage à usage unique**
  (pas de mot de passe à taper).

## Télécharger

Chaque version publiée (`release.yml`, [ADR 0173](adr/0173-automatic-releases-from-the-version-file.md))
porte une archive par système, au nom de la version de l'OS :

| Système | Archive |
|---|---|
| Windows x64 / ARM64 | `hackeros-server-<version>-win-x64.zip` / `win-arm64.zip` |
| macOS Apple Silicon / Intel | `hackeros-server-<version>-osx-arm64.tar.gz` / `osx-x64.tar.gz` |
| Linux x64 / ARM64 | `hackeros-server-<version>-linux-x64.tar.gz` / `linux-arm64.tar.gz` |
| Docker (amd64, arm64) | `ghcr.io/kazo-ca/hackeros-server:<version>` (et `latest`) |

Dans l'OS : **Paramètres > Connexion au serveur > Serveur local** liste ces
téléchargements pour la version en cours, l'image Docker, la page des versions
et ce guide.

Chaque archive est un programme autonome (self-contained, un seul fichier) qui
contient aussi l'OS publié : le serveur sert HackerOS lui-même
([`server-hosting.md`](server-hosting.md)).

## Installer

### Windows

Décompresser l'archive, puis dans PowerShell **en administrateur** :

```powershell
.\install.ps1                      # cet ordinateur seulement
.\install.ps1 -Lan 192.168.1.20    # aussi le réseau local en HTTPS
```

- Programme dans `C:\Program Files\HackerOS Server`, données dans
  `C:\ProgramData\HackerOS Server`.
- Service Windows `HackerOSServer` (démarrage automatique), compte
  `LocalService`.
- Avec `-Lan` : règle de pare-feu entrante sur le port HTTPS, **profil Privé
  seulement**.
- Options : `-Port`, `-HttpsPort`, `-Account`, `-InstallDir`, `-DataDir`,
  `-Reconfigure`.
- Le programme n'est pas signé : SmartScreen demande de confirmer
  (« Informations complémentaires », « Exécuter quand même »).

### Linux

```bash
sudo ./install.sh                       # cet ordinateur seulement
sudo ./install.sh --lan 192.168.1.20    # aussi le réseau local en HTTPS
```

- Programme dans `/opt/hackeros-server`, données dans
  `/var/lib/hackeros-server`.
- Unité systemd `hackeros-server` (`Type=notify`), utilisateur système
  `hackeros` sans shell.

### macOS

Même commande que Linux. Programme dans `/usr/local/hackeros-server`, données
dans `/usr/local/var/hackeros-server`, service launchd
`ca.kazo.hackeros-server`. Le script retire la quarantaine du téléchargement et
signe le programme ad hoc (pas de signature Apple).

Options de `install.sh` : `--lan <noms>` (adresses IP ou noms séparés par des
virgules), `--port`, `--https-port`, `--account`, `--prefix`, `--data`,
`--no-service` (installer et configurer sans service), `--reconfigure`.

### Docker

```bash
docker run -d --name hackeros-server -p 127.0.0.1:5888:8080 \
  -v hackeros-data:/data ghcr.io/kazo-ca/hackeros-server
docker exec hackeros-server ./HackerOs.Server pair admin
```

Le port publié est lié à `127.0.0.1` : le serveur reste sur cet ordinateur.
L'image n'a pas de HTTPS ; pour le réseau local, mettre un proxy inverse
HTTPS devant et ajouter son nom à `HACKEROS_AllowedHosts` et
`HACKEROS_Cors__AllowedOrigins__*`.

### Mise à jour et désinstallation

Relancer le script d'installation d'une version plus récente remplace le
programme et **garde** `hackeros-server.json` et les données
(`--reconfigure` / `-Reconfigure` réécrit la configuration).

`sudo ./uninstall.sh` ou `.\uninstall.ps1` retire le service et le programme ;
`--purge` / `-Purge` supprime aussi les données.

## Relier un appareil (appairage)

À l'installation, le script affiche un code du type `H9R4-JYYG` pour le compte
`admin` (créé s'il n'existe pas, sans mot de passe utilisable). Dans l'OS :

1. **Paramètres > Connexion au serveur > Serveur local**.
2. Taper l'adresse (`http://localhost:5888` par défaut, ou
   `https://192.168.1.20:5889` depuis un autre appareil) et appuyer sur
   **Tester** : l'OS indique si un serveur HackerOS répond et sa version.
3. Taper le code et appuyer sur **Appairer cet appareil**. L'appareil est
   connecté et une synchronisation démarre.

Pour un autre code (autre appareil, code expiré), sur la machine du serveur,
dans le dossier du programme :

```bash
sudo -u hackeros ./HackerOs.Server pair admin      # Linux
./HackerOs.Server pair admin                        # macOS (utilisateur qui a installé)
```

```powershell
.\HackerOs.Server.exe pair admin                    # Windows, PowerShell en administrateur
```

Un code :

- vaut **10 minutes** et sert **une seule fois** ;
- a 8 caractères parmi 30 (sans `0`, `O`, `1`, `I`, `L`, `U`) ; la casse, les
  espaces et le tiret sont ignorés ;
- n'est jamais écrit en clair : le serveur garde un fichier par code, nommé
  par l'empreinte SHA-256 du code, dans `<données>/pairing` ;
- après 10 codes faux en une minute, toute tentative est refusée (`429`)
  jusqu'à la fin de la minute.

L'API : `POST /api/auth/pair` avec `PairDeviceRequest(Code, DeviceName,
DeviceFingerprint)` répond la même `LoginResponse` qu'une connexion par mot de
passe, ou `401 INVALID_PAIRING_CODE`, ou `429`. Dans le SDK :
`HackServerConnection.PairAsync` et `HackServerConnection.TestAsync`
(capacité `server.connection.manage`, [`sdk-reference.md`](../framework/sdk-reference.md)).

## HTTPS sur le réseau local

Un navigateur n'appelle pas un serveur `http://` du réseau local depuis une
page `https://` (contenu mixte). Avec `--lan`, l'installation :

1. crée une autorité de certification locale (CA) propre à cette machine ;
2. signe avec elle un certificat de serveur pour les noms et adresses donnés ;
3. **supprime la clé privée de la CA** : elle ne peut plus rien signer d'autre ;
4. sert le certificat de la CA à
   `https://<adresse>:5889/api/hackeros/local-ca.crt` (et
   `http://localhost:5888/api/hackeros/local-ca.crt`).

Chaque autre appareil doit approuver ce certificat une fois :

| Appareil | Comment |
|---|---|
| Windows | Ouvrir le `.crt`, « Installer le certificat », magasin **Autorités de certification racines de confiance**. |
| macOS | Ouvrir le `.crt` dans Trousseaux d'accès, puis **Toujours approuver** dans ses réglages de confiance. |
| Linux | Copier dans `/usr/local/share/ca-certificates/` puis `sudo update-ca-certificates` ; Firefox et Chrome peuvent demander un import dans leurs propres réglages. |
| Android | Réglages > Sécurité > Chiffrement et identifiants > Installer un certificat > **Certificat CA**. |
| iOS / iPadOS | Ouvrir le `.crt`, installer le profil dans Réglages, puis Réglages > Général > Informations > **Réglages des certificats** : activer la confiance totale. |

L'ordinateur du serveur lui-même passe par `http://localhost:5888` et n'a rien
à approuver.

## Sécurité

- **Boucle locale par défaut** : sans `--lan`, seul `localhost` écoute.
- **`AllowedHosts`** : l'installation le limite à `localhost`, `127.0.0.1`,
  `[::1]` et aux noms donnés par `--lan`. Tout autre en-tête `Host` reçoit
  `400`, ce qui bloque le DNS rebinding (une page Internet qui ferait pointer
  son propre nom vers `127.0.0.1`).
- **CORS** : seules l'origine GitHub Pages `https://kazo-ca.github.io` et les
  adresses HTTPS du serveur sur le réseau local peuvent l'appeler depuis un
  navigateur.
- **L'OS ne sonde jamais `localhost` de lui-même** (ADR 0191 §3) :
  `ServerPresenceService` ne sonde que l'origine qui le sert, et le panneau
  Serveur local n'appelle l'adresse que sur **Tester** ou **Appairer**. Sinon
  Chrome afficherait sa question « Accès au réseau local » à chaque visiteur.
- Le service tourne avec un compte sans privilège (`LocalService`,
  `hackeros`) ; le dossier de données est en `750`.
- Programmes non signés (pas de certificat de signature de code) : c'est dit
  dans le `README.txt` de l'archive.

## Configuration

`hackeros-server.json`, à côté du programme, est lu **après**
`appsettings.json` et **avant** les variables d'environnement `HACKEROS_` :

```json
{
  "AllowedHosts": "localhost;127.0.0.1;[::1];192.168.1.20",
  "Kestrel": { "Endpoints": {
    "Local": { "Url": "http://localhost:5888" },
    "Lan": { "Url": "https://*:5889",
             "Certificate": { "Path": ".../certs/server.crt", "KeyPath": ".../certs/server.key" } }
  } },
  "Cors": { "AllowedOrigins": [ "https://kazo-ca.github.io", "https://192.168.1.20:5889" ] },
  "ConnectionStrings": { "HackerOsDb": "Data Source=.../hackeros.db" },
  "BlobStorage": { "Root": ".../blobs" },
  "ServerBackup": { "Root": ".../backups" },
  "HackerOs": {
    "LocalServer": { "CaCertificatePath": ".../certs/hackeros-local-ca.crt" },
    "Pairing": { "Root": ".../pairing" }
  }
}
```

Sous Windows le certificat est un `.pfx` (`Path` + `Password`). Lancé comme
service Windows, le serveur prend le dossier du programme comme racine de
contenu (sinon ce serait `C:\Windows\System32`).

## Dépannage

| Symptôme | Cause et solution |
|---|---|
| **Tester** : l'adresse ne répond pas | Service arrêté (`systemctl status hackeros-server`, `Get-Service HackerOSServer`, `launchctl print system/ca.kazo.hackeros-server`), mauvais port, ou pare-feu. |
| `400 Bad Request` | Le nom tapé n'est pas dans `AllowedHosts` : relancer l'installation avec `--lan <ce nom> --reconfigure`. |
| Erreur de certificat sur un autre appareil | La CA locale n'est pas approuvée sur cet appareil (voir le tableau plus haut), ou le nom tapé n'est pas dans le certificat. |
| « L'appairage a échoué : … 401 » | Code expiré, déjà utilisé ou mal tapé : en créer un autre avec `pair`. |
| `429` | Trop de codes faux : attendre une minute. |
| Port déjà pris | `--port` / `--https-port` (et `--reconfigure`). |

## Construction et publication

- `wasm2/HackerOs/build/publish-server.sh --rid <rid> --version <v> --output <dossier>`
  publie le serveur (self-contained, un seul fichier : `PublishSingleFile` est
  dans le `.csproj`), retire les `.pdb`/`.xml` et le proxy de débogage Blazor,
  ajoute les scripts de `Server/HackerOs.Server/packaging/` et produit
  l'archive.
- `release.yml` : le job `server` publie les 6 archives et les attache à la
  version GitHub ; le job `server-image` construit l'image multi-architecture
  depuis les publications Linux et la pousse sur `ghcr.io` (`:<version>`, et
  `:latest` hors préversion).

## Décisions clés

- **Codes en fichiers, pas en base** : la commande `pair` est un second
  processus à côté du service ; des fichiers suffisent, sans nouvelle table
  (le démarrage SQLite sans migration ne saurait pas l'ajouter à une base
  existante). Le déplacement du fichier (`File.Move`) garantit l'usage unique.
- **Liste de téléchargements sans système mis en avant** : l'ADR 0191 §6
  prévoyait de mettre en avant le système détecté, mais le SDK public n'a pas
  d'API pour l'agent utilisateur ; les six archives sont listées.
- Aucune nouvelle ADR : ces détails restent dans le cadre de l'ADR 0191.

## Tests

- `Tests/HackerOs.Server.Tests/LocalServerTests.cs` : codes (usage unique,
  expiration, forme tapée, limite de tentatives), ligne de commande, et de bout
  en bout : `pair` puis `POST /api/auth/pair`, `Host` inconnu refusé, CA servie,
  CORS GitHub Pages.
- `Tests/HackerOs.Platform.Core.Tests/Administration/LocalServerGatewayTests.cs` :
  passerelle d'appairage et de test, capacité requise.
- `Tests/HackerOs.Apps.Settings.Tests/LocalServerPanelTests.cs` : panneau
  (téléchargements, rien appelé seul, Tester, Appairer, erreur en français).
- Essai réel de `install.sh --no-service --lan ...` sous Linux. Les scripts
  PowerShell, macOS et l'image Docker n'ont pas pu être essayés dans le
  conteneur de développement.

## Liste des tâches

- [x] Codes d'appairage à usage unique et `POST /api/auth/pair`.
- [x] Commande `HackerOs.Server pair <compte>`.
- [x] Service Windows et systemd, `hackeros-server.json`, racine de contenu.
- [x] CA locale, certificat HTTPS, `GET /api/hackeros/local-ca.crt`.
- [x] `AllowedHosts` et origine CORS GitHub Pages.
- [x] Scripts d'installation et de désinstallation (Windows, Linux, macOS), `README.txt`.
- [x] `Dockerfile` et `build/publish-server.sh`.
- [x] Jobs `server` et `server-image` dans `release.yml`.
- [x] SDK : `HackServerConnection.PairAsync` et `TestAsync`.
- [x] Paramètres > Serveur local.
- [x] `index.html` servi par le serveur publié (voir [`pitfalls.md`](pitfalls.md)).
- [ ] Essai réel sous Windows, macOS et Docker.
