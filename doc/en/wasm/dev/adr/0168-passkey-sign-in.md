# ADR 0168 : connexion par clé d'accès (WebAuthn) et déverrouillage du trousseau

## Statut

Proposé le 2026-10-25, à la demande de Benoit (« ajouter la possibilité de
créer une clé d'accès comme méthode pour se logger »). Les questions ouvertes
ont été posées à Benoit dans le fil du chantier ; le code suit les choix
recommandés (marqués ★, §8) en attendant sa réponse.

- **Complète :** [ADR 0013](0013-local-user-session.md) (comptes locaux et
  session), [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md) §2
  (trousseau), [ADR 0120](0120-passwd-and-administrator-password-reset.md)
  (`passwd`, `HackUserAccounts`).
- **Va avec :** [ADR 0169](0169-administrator-adds-and-removes-users.md)
  (ajout et suppression de comptes par un administrateur).
- **Ajoute au SDK 2.1 :** des membres dans `PublicAPI.Unshipped.txt`, sans
  rupture.
- **Documents :** [`passkeys.md`](../passkeys.md),
  [`sensitive-settings.md`](../sensitive-settings.md),
  [`../../framework/system-administration.md`](../../framework/system-administration.md).

## Contexte

- Un compte se connecte seulement par mot de passe (`LocalSessionService.LoginAsync`).
- Le trousseau `~/.local/share/keyrings/login.keyring` est chiffré avec une clé
  **tirée du mot de passe** (PBKDF2). Une connexion sans mot de passe ne peut
  donc pas l'ouvrir.
- Tout tourne dans le navigateur : pas de serveur qui garde un défi. Les
  comptes et leurs vérificateurs sont dans IndexedDB, comme les hachages de mot
  de passe.
- .NET en WebAssembly n'a ni ECDSA ni RSA : les signatures se vérifient par Web
  Crypto (`crypto.subtle.verify`), comme l'AES-GCM du trousseau.
- `navigator.credentials` n'existe que dans la page (pas dans un Web Worker) et
  seulement en contexte sécurisé (HTTPS ou `localhost`).

## Décision

### 1. Une clé d'accès est une vraie clé WebAuthn, vérifiée par l'OS

- Création : `navigator.credentials.create` avec `rp.id` = nom d'hôte de la
  page, `user.id` = identifiant opaque du compte (16 octets), algorithmes
  ES256 (-7) puis RS256 (-257), `residentKey: "preferred"`,
  `userVerification: "preferred"`, `attestation: "none"`, et les clés
  existantes du compte en `excludeCredentials`. L'OS vérifie le
  `clientDataJSON` (`webauthn.create`, défi, origine).
- L'OS garde, dans l'enregistrement du compte (magasin IndexedDB `users`,
  propriété `passkeys`) : l'identifiant de la clé, sa clé publique SPKI
  (`getPublicKey()`), l'algorithme, le compteur de signatures, le nom donné par
  l'utilisateur, les dates de création et de dernière utilisation, et le sel
  PRF (§3). Jamais de secret : comme un hachage de mot de passe, rien de ce qui
  est stocké ne permet de se connecter.
- Connexion : l'OS tire un défi aléatoire de 32 octets, appelle
  `navigator.credentials.get` avec les clés du compte en `allowCredentials`,
  puis **vérifie lui-même** la réponse :
  - `clientDataJSON` : `type` = `webauthn.get`, `challenge` = le défi,
    `origin` = l'origine de la page ;
  - `authenticatorData` : empreinte SHA-256 du `rp.id`, bit « présence de
    l'utilisateur » levé ;
  - signature sur `authenticatorData ‖ SHA-256(clientDataJSON)` avec la clé
    publique gardée (Web Crypto ; signature DER convertie en `r‖s` pour
    ECDSA) ;
  - compteur : s'il était non nul et ne monte pas, la clé est refusée (clé
    clonée).
- Le compteur et la date d'utilisation sont mis à jour. `auth.log` reçoit
  `session.login` (sujet suffixé ` (passkey)`), `passkey.add` et
  `passkey.remove`.
- Une clé d'accès est liée au **domaine** du site : servie sous une autre
  adresse (autre domaine Pages, `localhost`), l'OS ne la reconnaît plus et il
  faut en créer une nouvelle. C'est voulu (protection contre l'hameçonnage).

### 2. La clé d'accès s'ajoute au mot de passe ★

- Un compte garde son mot de passe : secours si l'appareil est perdu, et
  `sudo`/`passwd` dans le terminal, qui lisent un mot de passe.
- L'écran de connexion montre « Se connecter avec une clé d'accès » quand le
  compte choisi en a au moins une. L'échec d'une clé d'accès laisse la
  connexion par mot de passe disponible.
- Retirer le mot de passe d'un compte (`passwd -d`) ne retire pas ses clés.

### 3. Le trousseau s'ouvre avec la clé d'accès grâce à PRF ★

- Le trousseau passe au **format 2** : les entrées sont scellées sous une clé
  maîtresse aléatoire de 32 octets, elle-même enveloppée (AES-256-GCM) dans des
  **emplacements** en clair dans le fichier :
  - un emplacement `password` : clé tirée du mot de passe (PBKDF2-SHA-256,
    210 000 itérations, sel propre) ;
  - un emplacement `passkey` par clé d'accès compatible PRF : clé =
    SHA-256(`"hackeros-keyring-prf-v1"` ‖ sortie PRF), la sortie PRF étant
    obtenue avec le sel PRF propre à cette clé (`extensions.prf.eval`).
- À la création d'une clé d'accès, si le trousseau est ouvert et que
  l'authentificateur gère PRF, l'OS ajoute l'emplacement. Si la sortie PRF
  n'arrive pas avec la création, l'OS demande une seconde confirmation
  (`get`) pour l'obtenir.
- Connexion par clé d'accès : la sortie PRF ouvre l'emplacement de cette clé.
  Sans PRF (appareil qui ne le gère pas, ou clé créée trousseau fermé), le
  trousseau reste **verrouillé** pour la session, comme après une connexion
  automatique ; il s'ouvre à la prochaine connexion par mot de passe.
- Changer son mot de passe ré-enveloppe seulement la clé maîtresse : les
  emplacements des clés d'accès restent valides. Supprimer une clé d'accès
  retire son emplacement (pas besoin d'ouvrir le trousseau : les emplacements
  sont en clair).
- Réinitialisation par un administrateur : inchangée (ADR 0120), le trousseau
  est mis de côté (`login.keyring.old`), emplacements des clés compris.
- Migration : un trousseau au format 1 s'ouvre toujours avec le mot de passe ;
  il est réécrit au format 2 (nouvelle clé maîtresse) à cette ouverture.

### 4. Confirmation administrateur avec une clé d'accès ★

- La fenêtre de l'agent d'authentification (ADR 0055 §5) propose aussi
  « Utiliser une clé d'accès » quand l'administrateur choisi en a une. La
  vérification est celle du §1 ; elle n'ouvre pas de trousseau. `auth.log` :
  `session.elevate`.
- `sudo` dans le terminal reste au mot de passe.

### 5. SDK : gérer ses propres clés d'accès

`HackUserAccounts` (SDK 2.1, `Unshipped`) :

- `ListPasskeysAsync()` → `IReadOnlyList<HackPasskey>` : les clés de
  l'utilisateur de la session (id, nom, dates, `UnlocksKeyring`).
- `AddPasskeyAsync(nom)` : crée une clé pour l'utilisateur de la session. Pas
  de capacité : c'est le navigateur qui demande la présence de l'utilisateur
  (empreinte, code de l'appareil), et la clé ne sert qu'à ce compte.
- `RemovePasskeyAsync(id)` : retire une de ses clés.
- `HackUserAccount.PasskeyCount` : nombre de clés de chaque compte.
- Navigateur sans WebAuthn, page hors contexte sécurisé, ou app hors de la
  page (Web Worker) : `NotSupportedException`. L'utilisateur qui annule la
  fenêtre du navigateur : `OperationCanceledException`.

### 6. Où vit le code

- `Simulation.Abstractions` : `LocalPasskeyCredential`, `LocalUser.Passkeys`,
  `ILocalUserRepository.SetPasskeysAsync` (implémentation par défaut :
  `NotSupportedException`, comme `SetCredentialAsync`).
- `Platform.Core/Sessions` : `IPasskeyAuthenticator` (le pont vers le
  navigateur), `IPasskeySignatureVerifier`, `PasskeyAssertionVerifier`
  (vérification du §1, en C# sauf la signature), `PasskeyService`
  (création, connexion, retrait), `LocalSessionService.LoginWithPasskeyAsync`.
- `Platform.Core/Keyring` : format 2 et emplacements.
- `Infrastructure.Browser` : `webAuthn.js` et `BrowserPasskeyAuthenticator`
  (création, assertion et vérification de signature par Web Crypto).

### 7. Ce qui n'est pas fait

- Pas de connexion sans choisir de compte (clé découvrable). Les clés sont
  créées découvrables pour le permettre plus tard.
- Pas de vérification d'attestation (`attestation: "none"`).
- Pas de clé d'accès pour `sudo` dans le terminal.

### 8. Questions posées à Benoit

1. Trousseau à la connexion par clé d'accès : **A ★ PRF** (format 2, migration
   automatique, verrouillé si l'appareil ne gère pas PRF) ou **B** trousseau
   toujours verrouillé après une connexion par clé d'accès.
2. Mot de passe obligatoire : **A ★** la clé s'ajoute au mot de passe, ou **B**
   comptes avec seulement des clés (`sudo`/`passwd` impossibles pour eux).
3. Confirmation administrateur : **A ★** accepter la clé d'accès dans la
   fenêtre, ou **B** mot de passe seulement.
4. Dossier personnel à la suppression d'un compte : voir l'ADR 0169.

## Conséquences

- Se connecter devient possible sans taper de mot de passe, avec la même
  garantie qu'un site web : la clé privée ne quitte pas l'authentificateur.
- Comme pour les mots de passe, quelqu'un qui modifie IndexedDB à la main peut
  remplacer une clé publique : l'OS simulé n'a pas de matériel de confiance.
- Le format 2 du trousseau ne se lit plus par une version antérieure de l'OS.
- Les tests de bout en bout peuvent utiliser l'authentificateur virtuel de
  Chromium (CDP `WebAuthn.addVirtualAuthenticator`, avec `hasPrf`).
