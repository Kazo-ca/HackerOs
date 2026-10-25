# ADR 0100 : réglages sensibles (fichiers privés, masquage, trousseau chiffré) et audit des écritures protégées

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/8-settings-security`).
Le §2 suit la réponse de Benoit du 2026-10-17 : un trousseau par utilisateur,
chiffré par une clé tirée de son mot de passe.

- **Complète :** [ADR 0044](0044-settings-as-files.md) (réglages en fichiers,
  accès par les permissions), [ADR 0045](0045-logs-as-files.md) (journaux en
  fichiers, masquage par nom de clé), ADR 0042 (élévation).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/8-settings-security` ; manques R16, R17 ; décision différée n° 9.

## Contexte

- `SettingSensitivity` (`Public`, `Private`, `SecretReference`, `Restricted`,
  `SettingsSchema.cs:26`) est déclaré dans le schéma des réglages et dans le
  manifeste, mais seul `Public` est utilisé et rien n'agit sur les autres
  valeurs.
- Un document de réglages est **un** fichier (`/etc/<appId>/<doc>.config`,
  `~/.config/<appId>/<doc>.config`) ; ses permissions valent pour toutes ses
  clés. On ne peut donc pas cacher une clé à quelqu'un qui lit le fichier.
- Le seul masquage existant est celui des journaux, par nom de clé
  (`SensitiveKeyDiagnosticRedactor`).
- L'élévation administrateur est journalisée dans `/var/log/auth.log`, mais
  l'écriture qui suit dans `/etc` ne l'est pas.
- Le seul usage de Web Crypto est le hachage PBKDF2 des mots de passe
  (`WebCryptoPasswordHasher`) ; rien n'est chiffré au repos.

## Décision

### 1. Les clés sensibles vont dans un fichier privé, comme `/etc/shadow`

- Pour un document qui a des clés non publiques, `HackSettings` écrit ces clés
  dans un fichier compagnon **`<doc>.private.config`**, en mode `0600`
  (propriétaire : l'utilisateur dans `~/.config`, root dans `/etc`), et les
  clés publiques dans `<doc>.config` (inchangé). C'est la séparation
  `/etc/passwd` / `/etc/shadow`.
- `LayeredConfigResolver` lit les deux fichiers de chaque couche ; une clé
  présente dans le mauvais fichier (par exemple une clé `Private` restée dans
  `<doc>.config`) est ignorée à la lecture avec un avertissement, comme une ligne
  invalide (ADR 0044 §5), et déplacée à la prochaine écriture.
- Sens des valeurs :

  | Sensibilité | Fichier | Qui lit | Synchro, exports |
  |---|---|---|---|
  | `Public` | `<doc>.config` | selon les permissions du fichier (`0644`) | oui |
  | `Private` | `<doc>.private.config` | le propriétaire (et root) | oui (le fichier se synchronise comme les autres fichiers de `~`) |
  | `Restricted` | `<doc>.private.config` de `/etc` seulement | root | jamais : exclu de la synchro et des rapports de diagnostic |
  | `SecretReference` | `<doc>.private.config`, la valeur étant un identifiant d'entrée du trousseau (§2) | le propriétaire | l'identifiant oui, le secret selon le §2 |

- **Masquage à la lecture** : quand un appelant ne peut pas lire le fichier
  privé, `HackSettings` renvoie la clé comme « masquée » (`HackSettingValue.IsMasked`,
  valeur nulle) au lieu d'échouer ; Paramètres affiche `••••••••`, et `cat` du
  fichier privé échoue avec `Permission denied`, comme pour `/etc/shadow`. Les rapports de Diagnostic et les copies
  de réglages masquent pareil. La sauvegarde complète de l'ADR 0099, réservée à
  root, garde tout.

### 2. Chiffrement : un trousseau par utilisateur, déverrouillé à la connexion (choix de Benoit)

- Les valeurs `SecretReference` (mots de passe d'un site, jetons d'API, clés)
  sont stockées dans un **trousseau** chiffré, comme gnome-keyring :
  `~/.local/share/keyrings/login.keyring`, mode `0600`.
- Le trousseau est chiffré en AES-GCM par Web Crypto, avec une clé dérivée du
  mot de passe de connexion (PBKDF2, mêmes paramètres que le hachage existant,
  sel propre au trousseau). Il est déverrouillé à la connexion et gardé en
  mémoire pendant la session, reverrouillé à la déconnexion.
- API du SDK : `HackKeyring.GetAsync`, `SetAsync`, `DeleteAsync`, par app
  (une app ne voit que ses entrées, sauf capacité dangereuse
  `keyring.read-all` pour un gestionnaire de mots de passe).
- Changer son mot de passe re-chiffre le trousseau. **Réinitialiser** le mot de
  passe d'un autre utilisateur (administrateur) rend son trousseau illisible :
  l'OS le renomme `login.keyring.old` et en crée un vide, comme sous GNOME.
- Les services système (ADR 0097, `Boot`) n'ont pas de trousseau d'utilisateur.
- Les autres sensibilités ne sont pas chiffrées : leur protection est la
  permission du fichier, comme sous Linux.

### 3. Audit des écritures protégées

- Nouveau journal **`/var/log/audit.log`**, `0600 root`, écrit par le seul
  écrivain de journaux de la plateforme (ADR 0045), même rotation (1 Mio × 5).
- Chaque création, écriture, suppression, renommage, `chmod` et `chown` sous
  `/etc`, `/bin` et `/var` (sauf `/var/log`, pour ne pas s'auditer lui-même) y
  est écrit, réussi ou refusé, au format d'`auditd` :

  ```
  type=PATH msg=audit(1792218000.123:42): op=write path="/etc/hackeros/apps.config" uid=0 auid=1000 pid=231 comm="settings" app="org.hackeros.settings" res=success
  ```

  `auid` est l'utilisateur humain d'origine (même après `sudo`), `uid`
  l'identité qui a fait l'écriture. Jamais le contenu.
- L'Error Log Viewer sait lire ce journal (pour un administrateur) ; la commande
  `ausearch -f <chemin>` filtre par chemin.

### 4. Ce qui ne change pas

Le masquage par nom de clé des journaux reste ; il s'ajoute à celui par
sensibilité.

## Conséquences

- `v3/8` modifie `HackSettings`, `LayeredConfigResolver`, `SettingsFilesWatcher`
  (surveiller aussi `*.private.config`), le système de fichiers (écrire l'audit),
  ajoute `HackKeyring`, `ausearch`, la cible de journal `AuditLog` et des tests
  (masquage selon l'appelant, migration des clés, trousseau verrouillé et
  déverrouillé, réinitialisation du mot de passe).
- Une capacité dangereuse (`keyring.read-all`) s'ajoute au catalogue :
  `capabilities.md` et `dangerous-capability-approval.md` sont mis à jour, avec
  `settings-files.md`, `logging.md`, `sdk-reference.md` et `linux-commands.md`.
