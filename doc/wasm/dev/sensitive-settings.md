# Réglages sensibles, trousseau et journal d'audit

Mise en œuvre du chantier `v3/8-settings-security` du
[plan de fin de migration](plans/wasm-v3-completion-plan.md), selon
l'[ADR 0100](adr/0100-sensitive-settings-and-protected-write-audit.md). Ce
document décrit le code livré ; l'ADR reste la référence des décisions.

## Objectif

- Une clé de réglage non publique (`SettingSensitivity` autre que `Public`) ne
  se lit que par qui peut lire son fichier privé ; les autres la voient
  « masquée ».
- Les secrets (`SecretReference`) sont chiffrés au repos dans un trousseau par
  utilisateur, déverrouillé à la connexion.
- Chaque modification sous `/etc`, `/bin` et `/var` (sauf `/var/log`) laisse une
  ligne dans `/var/log/audit.log`, réussie ou refusée, au format d'`auditd`.

## 1. Fichiers privés et masquage

### Emplacements

| Couche | Clés publiques | Clés non publiques |
| --- | --- | --- |
| Système | `/etc/<appId>/<doc>.config` (`0644`) | `/etc/<appId>/<doc>.private.config` (`0600`, root) |
| Utilisateur | `~/.config/<appId>/<doc>.config` | `~/.config/<appId>/<doc>.private.config` (`0600`) |

Le fichier privé n'est lu et surveillé que si le schéma du document a au moins
une clé non publique (`SettingsPrivateFiles.HasPrivateFields`).

| Sensibilité | Fichier | Couche utilisateur |
| --- | --- | --- |
| `Public` | `<doc>.config` | oui |
| `Private` | `<doc>.private.config` | oui |
| `SecretReference` | `<doc>.private.config` ; la valeur est l'identifiant d'une entrée du trousseau | oui |
| `Restricted` | `<doc>.private.config` de `/etc` seulement | non : ignorée avec un avertissement, et `SetAsync` sur la couche utilisateur lève `InvalidOperationException` |

### Lecture (`LayeredConfigResolver`)

`LayeredConfigResolver.Resolve(schema, LayeredConfigFiles)` lit jusqu'à quatre
fichiers. Une clé dans le mauvais fichier est ignorée avec un avertissement,
comme une ligne invalide :

- `sensitive key 'k' belongs in <chemin privé>, ignored` ;
- `public key 'k' belongs in <chemin public>, ignored` ;
- `restricted key 'k' is only honored in the system private file, ignored` ;
- `[locked] is only honored in the public system file, section ignored`.

La section `[locked]` n'est lue que dans le fichier public du système. Pour une
clé non publique, l'ordre est :

1. valeur utilisateur privée (si la clé n'est ni verrouillée ni `Restricted`) ;
2. **masquée**, origine utilisateur, si le fichier privé de l'utilisateur est
   illisible ;
3. valeur système privée ;
4. **masquée**, origine système, si le fichier privé du système est illisible ;
5. valeur par défaut.

Un fichier illisible pour une raison de droits (`PermissionDenied`,
`CapabilityDenied`, `AuthorityDenied`, `ProtectedEntry`) ne fait pas échouer la
lecture. `LayeredConfigSnapshot.MaskedKeys` liste les clés masquées ; leur
entrée dans `Values` garde la valeur par défaut déclarée, jamais la vraie.

### Écriture (`SettingsPrivateFiles.Apply`)

Une écriture lit les deux fichiers de la couche, déplace les clés mal placées
(dans les deux sens), puis range chaque clé selon sa sensibilité ; retirer une
clé l'efface des deux fichiers. Le fichier privé est écrit en premier (créé en
`0600`), puis le fichier public, chacun par une édition à révision
(`SettingsTextFile.EditAsync`) qui garde commentaires et clés inconnues.

### API du SDK

```csharp
await using HackSettingsDocument mail = await HackSettings.OpenAsync(AppContext);
HackSettingValue server = mail.GetValue("Account.server");   // IsMasked, Source, IsLocked, Sensitivity
string shown = server.DisplayText;                            // "••••••••" si masquée ou secrète
bool masked = mail.IsMasked("Account.server");
string host = mail.Get<string>("Account.server");             // HackSettingMaskedException si masquée

await mail.SetSecretAsync("Account.password", "s3cr3t");      // trousseau + référence dans le fichier privé
string? password = await mail.GetSecretAsync("Account.password");
await mail.SetSecretAsync("Account.password", null);          // efface l'entrée et la référence
```

- `HackSettingMaskedException` dérive de `UnauthorizedAccessException` et donne
  `QualifiedKey` et `PrivatePath`.
- La référence d'un secret est `settings:<document>:<groupe.clé>`.
- `GetSecretAsync`/`SetSecretAsync` exigent une clé `SecretReference` et ne
  sont pas offerts sur les documents de l'OS (`NotSupportedException`).
- L'événement `Changed` signale aussi un changement d'état masqué.

### Exports et diagnostics

`StorageSnapshotRedactor` masque, dans l'instantané de stockage de l'app
Diagnostics (`AppStorageDiagnosticsGateway`), le contenu des fichiers
`*.private.config` et de tout fichier sous `~/.local/share/keyrings/` : le
morceau devient `[masked: ADR 0100]`. La synchronisation ne couvre que le
dossier personnel, donc le fichier privé de `/etc` (seul porteur des clés
`Restricted`) n'est jamais synchronisé. La sauvegarde complète de l'ADR 0099,
réservée à root, garde tout.

## 2. Trousseau de connexion

| Élément | Valeur |
| --- | --- |
| Fichier | `~/.local/share/keyrings/login.keyring` (`0600`, dossier `0700`) |
| Chiffrement | AES-256-GCM, nonce de 12 octets neuf à chaque écriture |
| Clé | PBKDF2-SHA-256 sur le mot de passe de connexion, 210 000 itérations, sel de 16 octets propre au trousseau |
| Moteur | Web Crypto (`cryptoHasher.js`, `WebCryptoKeyringCipher`) dans l'hôte, `ManagedKeyringCipher` (.NET) dans les tests |
| Format | JSON `hackeros-keyring` v1 : `kdf`, `cipher`, `iterations`, `salt`, `nonce`, `ciphertext` (base64) |

### Cycle de vie (`UserKeyringService`)

- **Connexion** (`LocalSessionService`) : `UnlockAsync(principal, password)`
  ouvre le trousseau, ou le crée vide. Un utilisateur sans mot de passe n'a pas
  de trousseau : il reste verrouillé.
- **Mot de passe différent** (réinitialisé par un administrateur) : le
  trousseau illisible est renommé `login.keyring.old` et un trousseau vide le
  remplace, comme sous GNOME.
- **Déconnexion, arrêt** : `Lock()` efface la clé de la mémoire.
- **Changement de mot de passe** : `ChangePasswordAsync(userId, nouveau)`
  re-chiffre le trousseau avec un nouveau sel. **Mise à l'écart par un
  administrateur** : `SetAsideAsync(homePath)`. La commande `passwd` et
  l'onglet « Utilisateurs » des Paramètres les appellent (ADR 0120, voir
  [§4](#4-mots-de-passe--passwd-et-réinitialisation) plus bas).
- Le journal d'authentification (`IAuditLog`) reçoit `keyring.unlock`,
  `keyring.create`, `keyring.reset` (aussi pour une mise à l'écart par
  l'administrateur) et `keyring.rekey`, sans aucun secret.

### API du SDK (`HackKeyring`)

```csharp
if (HackKeyring.IsUnlocked(AppContext))
{
    await HackKeyring.SetAsync(AppContext, "imap", "s3cr3t", label: "Compte IMAP");
    string? secret = await HackKeyring.GetAsync(AppContext, "imap");
    IReadOnlyList<HackKeyringEntry> mine = await HackKeyring.ListAsync(AppContext);
    await HackKeyring.DeleteAsync(AppContext, "imap");
}

// Gestionnaire de mots de passe seulement (capacité dangereuse keyring.read-all) :
IReadOnlyList<HackKeyringEntry> all = await HackKeyring.ListAllAsync(AppContext);
string? other = await HackKeyring.GetAsync(AppContext, "org.example.mail", "imap");
```

- Une app ne voit que ses propres entrées. Lire celles d'une autre app ou tout
  lister demande `keyring.read-all` ; sans elle, `UnauthorizedAccessException`.
- Un trousseau verrouillé lève `HackKeyringLockedException`.
- Un identifiant fait au plus 256 caractères, sans caractère de contrôle.
- Chaque méthode existe aussi sans contexte (contexte ambiant de l'ADR 0043).
- `IAppExecutionContext.Keyring` (`IAppKeyringGateway`) est la passerelle ; elle
  a une implémentation par défaut toujours verrouillée, pour les contextes de
  test et les services système (ADR 0097), qui n'ont pas de trousseau.

## 3. Journal d'audit

### Écriture

`FileSystemService` reçoit un `IFileSystemAuditSink` optionnel
(`FileSystemAuditLog` dans l'hôte). Après chaque création, écriture,
renommage (`rename`), copie (`copy`, sur le chemin de destination),
suppression et `chmod`, si le chemin ou sa destination est audité
(`FileSystemAuditPolicy.IsAudited` : `/etc`, `/bin`, `/var` sauf `/var/log`),
une ligne est mise en file pour l'écrivain unique de journaux (ADR 0045) vers
`LogFileTarget.AuditLog` : `/var/log/audit.log`, `0600`, rotation 1 Mio × 5.

```
type=PATH msg=audit(1792218000.123:42): op=write path="/etc/hosts" uid=0 auid=1000 pid=231 comm="nano" app="org.hackeros.commands.nano" res=success UID="root" AUID="alice"
```

- `uid` : l'identité qui écrit (`root` pour une opération système ou élevée par
  `sudo`/approbation) ; `auid` : l'humain d'origine, `unset` pour l'OS.
- HackerOS n'a pas d'identifiants numériques : `root` vaut `0`, un utilisateur
  `1000`, `unset` `4294967295` ; les champs `UID=` et `AUID=` donnent les noms,
  comme les champs enrichis d'`auditd`.
- Un refus ajoute `err=<code>` (par exemple `err=PermissionDenied`). Le contenu
  n'est jamais écrit.
- HackerOS n'a pas d'opération `chown` : il n'y a donc rien à auditer pour elle.

`AuditLineFormat` (Simulation.Abstractions) écrit et relit ces lignes.

### Lecture

- **`ausearch`** (`org.hackeros.commands.ausearch`, avec `sudo`) : `-f chemin`
  (le chemin, un fichier dessous, ou un nom de fichier), `-ua`, `-ui`, `-ul`
  (utilisateur, `uid`, `auid`), `-c comm`, `--success yes|no` (`-sv`),
  `-if fichier`, `-i` (accepté). Lit les archives `.5` à `.1` puis le journal ;
  affiche `----`, `time->…` puis la ligne ; `<no matches>` et code 1 sans
  résultat.
- **Error Log Viewer** : la famille `audit.log` apparaît pour un
  administrateur ou une session élevée (`LogSource.RequiresRoot`), et n'est pas
  lue par la vue « tous les journaux », pour ne pas demander d'élévation à
  chaque ouverture. Une écriture refusée s'affiche en avertissement, une
  écriture réussie en information, avec l'app comme source.

## 4. Mots de passe : `passwd` et réinitialisation

Décision : [ADR 0120](adr/0120-passwd-and-administrator-password-reset.md).

- **`LocalPasswordService`** (`Platform.Core/Sessions`) fait le travail de
  `passwd` et de `/etc/shadow` : il hache le nouveau mot de passe comme à la
  création du compte (`LocalPasswordHasher`, PBKDF2-SHA-256, Web Crypto dans
  le navigateur), l'enregistre par `ILocalUserRepository.SetCredentialAsync`,
  fait suivre le trousseau et écrit `passwd.change`, `passwd.reset` ou
  `passwd.delete` dans `auth.log`.
- **Son propre mot de passe**, sans capacité : le mot de passe courant est
  vérifié (vide si le compte n'en a pas), le nouveau doit avoir 8 à 256
  caractères sans caractère de contrôle et différer de l'actuel. Le trousseau
  est re-chiffré (`ChangePasswordAsync`) ; s'il n'était pas ouvert, il est
  d'abord ouvert avec l'ancien mot de passe.
- **Le mot de passe d'un autre compte** : capacité dangereuse `users.manage`
  et un administrateur par l'agent d'authentification (rien sous `sudo`). Le
  trousseau du compte est mis de côté (`login.keyring.old`) ; si c'est celui
  de la session, il est re-chiffré (réinitialisation) ou mis de côté puis
  verrouillé (retrait). Retirer le mot de passe du dernier administrateur
  actif qui en a un est refusé.
- **SDK** : `HackUserAccounts` (`ListAsync`, `ChangePasswordAsync`,
  `ResetPasswordAsync`, `RemovePasswordAsync`, `ValidateNewPassword`) sur
  `IAppExecutionContext.UserAccounts`, branché par
  `SystemAdministrationGatewayServices.Passwords`.
- **Terminal** : `passwd`, `passwd NOM` et `passwd -d NOM` sous `sudo`,
  `passwd -S [NOM]` ([`linux-commands.md`](linux-commands.md)). Les mots de
  passe sont lus écho coupé : `TerminalExecutionContext.ReadSecretLineAsync`
  coupe `ITerminalInputMode.EchoEnabled` de l'entrée du terminal, comme
  `getpass(3)` coupe `ECHO` ([`terminal-emulator.md`](terminal-emulator.md)).
- **Paramètres > Utilisateurs** (`UsersPanel`) : liste des comptes avec leur
  état `P`/`NP`/`L`, changement de son mot de passe, et pour un administrateur
  la réinitialisation ou le retrait du mot de passe d'un autre compte.

## Limites connues

- Les documents de l'OS (`OsSettingsDocument`) n'acceptent que des clés
  publiques : un schéma avec une clé non publique lève `ArgumentException`.
- L'app Paramètres n'a pas encore d'écran pour les réglages des apps ; une UI
  qui en affiche utilise `HackSettingValue.DisplayText` pour le masque.
- Un fichier créé par une approbation ou `sudo` appartient à l'utilisateur
  humain (comportement existant de l'ADR 0042), y compris un fichier privé de
  `/etc` créé ainsi ; son mode reste `0600`.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Settings/SensitiveSettingsTests.cs` :
  fichier privé `0600`, masquage d'une valeur système, valeur utilisateur
  prioritaire, déplacement d'une clé mal placée, `Restricted` refusée côté
  utilisateur, secret dans le trousseau, avertissements du résolveur.
- `Tests/HackerOs.Platform.Core.Tests/Keyring/UserKeyringServiceTests.cs` :
  création et modes, reconnexion, mise à l'écart sur un autre mot de passe,
  changement de mot de passe, mise à l'écart par l'administrateur, utilisateur
  sans mot de passe, confinement par app et `keyring.read-all`, chiffrement.
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/FileSystemAuditTests.cs` :
  politique, format aller-retour, écritures acceptées et refusées dans
  `audit.log` (`0600`), `uid`/`auid` sous `sudo`.
- `Tests/HackerOs.Commands.Tests/CoreCommandsTests.cs` : filtres d'`ausearch`.
- `Tests/HackerOs.Platform.Core.Tests/Sessions/LocalPasswordServiceTests.cs` :
  changement avec trousseau conservé, mauvais mot de passe courant, règles,
  réinitialisation qui met le trousseau de côté, retrait refusé au dernier
  administrateur, passerelle (`users.manage` et administrateur).
- `Tests/HackerOs.Commands.Wave5.Tests/PasswdCommandTests.cs` : invites écho
  coupé, confirmation différente, `sudo passwd NOM`, `-d`, `-S`.
- `Tests/HackerOs.UI.E2E.Tests/PasswdTerminalTests.cs` : `passwd` dans le vrai
  terminal n'affiche rien de ce qui est tapé, puis seule la nouvelle valeur
  ouvre la session.

## Liste des tâches

- [x] Fichier compagnon `<doc>.private.config` (`0600`) et rangement des clés à l'écriture
- [x] Lecture des quatre fichiers, avertissements des clés mal placées, masquage
- [x] `HackSettingValue`, `HackSettingMaskedException`, `GetSecretAsync`/`SetSecretAsync`
- [x] Trousseau `login.keyring` chiffré AES-GCM (Web Crypto) sous une clé PBKDF2
- [x] Déverrouillage à la connexion, verrouillage à la déconnexion, mise à l'écart `.old`
- [x] `HackKeyring` et capacité dangereuse `keyring.read-all`
- [x] Masquage des fichiers privés et du trousseau dans les instantanés de diagnostic
- [x] Journal `/var/log/audit.log` écrit par le système de fichiers
- [x] Commande `ausearch` et lecture du journal d'audit par l'Error Log Viewer
- [x] Tests
- [x] Commande `passwd` et réinitialisation par un administrateur (ADR 0120 : `LocalPasswordService`, `HackUserAccounts`, onglet « Utilisateurs »)
