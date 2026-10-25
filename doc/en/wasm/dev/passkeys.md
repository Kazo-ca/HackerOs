# Clés d'accès (WebAuthn)

## Objet

Décrit la connexion par clé d'accès de HackerOS : une vraie clé WebAuthn
(empreinte, visage, NIP de l'appareil, téléphone ou clé de sécurité USB),
vérifiée par l'OS lui-même avec la clé publique gardée dans le compte, qui
peut aussi ouvrir le trousseau. Décision : [ADR 0168](adr/0168-passkey-sign-in.md).
La clé d'accès s'ajoute au mot de passe : le compte garde son mot de passe
(secours, `sudo` et `passwd` dans le terminal).

## Où vit le code

| Couche | Fichier | Rôle |
| --- | --- | --- |
| Modèle | `Shared/HackerOs.Simulation.Abstractions/Sessions/LocalPasskeyCredential.cs` | Une clé d'accès d'un compte : id, clé publique SPKI, algorithme (ES256 `-7` ou RS256 `-257`), nom, compteur, dates, sel PRF. `LocalUser.Passkeys` les porte. |
| Dépôt | `ILocalUserRepository.SetPasskeysAsync` ; `InMemoryLocalUserRepository`, `IndexedDbLocalUserRepository` | Le magasin `users` d'IndexedDB garde un tableau `passkeys` par compte (sans changement de version du schéma). |
| Vérification | `Platform.Core/Sessions/PasskeyService.cs`, `PasskeyResponseChecks.cs` | Défi, contrôles, compteur, journal `auth.log` (`passkey.add`, `passkey.verify`, `passkey.remove`). |
| Contrat navigateur | `Platform.Core/Sessions/PasskeyContracts.cs` (`IPasskeyAuthenticator`) | Ce que la page fournit : environnement, création, assertion, vérification de signature. |
| Pont navigateur | `Infrastructure.Browser/wwwroot/webAuthn.js`, `OS/HackerOs.Ecosystem.Host/Passkeys/BrowserPasskeyAuthenticator.cs` | `navigator.credentials.create`/`get` et `crypto.subtle.verify` ; les erreurs WebAuthn deviennent des exceptions .NET. |
| Session | `LocalSessionService.LoginWithPasskeyAsync` | Connexion : vérifie la clé, ouvre la session, puis le trousseau par la clé. |
| Trousseau | `Platform.Core/Keyring/UserKeyringService.cs`, `KeyringFileFormat.cs` | Format 2 à emplacements ([`sensitive-settings.md`](sensitive-settings.md#2-trousseau-de-connexion)). |
| Administrateur | `AdminElevationVerifier.VerifyWithPasskeyAsync`, `AdministratorAuthenticationAgent.PasskeyVerifier` | Confirmation administrateur par clé d'accès ([`admin-authentication-agent.md`](admin-authentication-agent.md)). |
| Interface | `App.razor` (écran de connexion), `AdministratorAuthenticationOverlay`, Paramètres ▸ Utilisateurs (`PasskeysPanel`) | Boutons « Utiliser une clé d'accès » et gestion des clés. |
| SDK | `HackUserAccounts.ListPasskeysAsync`, `AddPasskeyAsync`, `RemovePasskeyAsync` | Les clés de la session, sans capacité ([`../framework/system-administration.md`](../framework/system-administration.md)). |

## Enregistrer une clé

1. Paramètres ▸ Utilisateurs ▸ « Mes clés d'accès » : l'utilisateur donne un
   nom (« Téléphone », « YubiKey »), puis `HackUserAccounts.AddPasskeyAsync`.
2. `PasskeyService.RegisterAsync` tire un défi et un sel PRF de 32 octets, puis
   demande au navigateur une création : relying party = nom d'hôte de la page,
   identifiant d'utilisateur = GUID du compte, `attestation: "none"`,
   `residentKey: "preferred"`, algorithmes ES256 puis RS256, clés existantes
   exclues, extension PRF avec le sel.
3. L'OS vérifie les données client (`webauthn.create`, défi, origine, pas de
   `crossOrigin`) et les données de l'authentificateur (hachage du relying
   party, présence de l'utilisateur), puis garde la clé publique
   (`getPublicKey()`, SPKI DER). Aucune attestation n'est vérifiée.
4. Si l'authentificateur a PRF et que le trousseau est ouvert, la clé reçoit
   un emplacement dans le trousseau. Un authentificateur qui ne donne la sortie
   PRF qu'à une assertion demande une deuxième confirmation, tout de suite.

## Se connecter

1. Écran de connexion : le bouton « Utiliser une clé d'accès » apparaît quand
   le compte choisi en a une (`HackUserAccount.PasskeyCount`).
2. `LoginWithPasskeyAsync` → `PasskeyService.AuthenticateAsync` : nouveau défi,
   `allowCredentials` = les clés du compte, PRF `evalByCredential` avec le sel
   de chaque clé.
3. Contrôles (ADR 0168 §1) : clé connue du compte, données client
   (`webauthn.get`, défi, origine), hachage du relying party, présence,
   signature sur `authenticatorData ‖ SHA-256(clientDataJSON)` vérifiée par Web
   Crypto avec la clé publique gardée (signature ECDSA convertie de DER en
   `r‖s` par `PasskeySignatureFormat.DerToP1363`), compteur croissant (deux
   zéros acceptés, sinon il doit monter : une clé clonée est refusée).
4. Le compteur et la date d'utilisation sont enregistrés, la session s'ouvre,
   puis `UnlockWithPasskeyAsync` ouvre le trousseau avec la sortie PRF. Sans
   PRF, la session s'ouvre et le trousseau reste verrouillé.

Une demande fermée par l'utilisateur (`NotAllowedError`, `AbortError`) devient
`OperationCanceledException` et ne journalise aucun échec ; un contrôle raté
lève `InvalidOperationException` et écrit `passkey.verify` refusé.

## Limites

- Une clé d'accès est liée au nom d'hôte de la page : servi sous une autre
  adresse (autre domaine, `localhost` contre `127.0.0.1`), l'OS demande une
  nouvelle clé.
- WebAuthn exige un contexte sécurisé (HTTPS ou `localhost`) et n'existe que
  dans le fil de la page : une app lancée dans un worker reçoit
  `NotSupportedException`.
- Pas d'attestation, pas de connexion sans nom de compte (« conditional UI »),
  pas de compte sans mot de passe (ADR 0168 §7).
- Supprimer un compte supprime ses clés d'accès (ADR 0169).

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Sessions/PasskeyServiceTests.cs` :
  enregistrement, connexion qui ouvre le trousseau, PRF à la création, clé sans
  PRF, compteur et date, réponses falsifiées (défi, origine, relying party,
  signature, compteur cloné, présence absente), demande fermée, page sans
  WebAuthn, compte sans clé, retrait avec l'emplacement du trousseau,
  changement de mot de passe, migration du trousseau format 1.
  `FakePasskeyAuthenticator` y produit de vraies réponses WebAuthn (clé P-256,
  signature DER, PRF par HMAC).
- `Tests/HackerOs.Platform.Core.Tests/Sessions/PasskeySignatureFormatTests.cs` :
  conversion DER vers `r‖s`, entiers courts, DER mal formé.
- `Tests/HackerOs.Platform.Core.Tests/Elevation/AdministratorAuthenticationAgentTests.cs` :
  demande fermée qui ne coûte pas d'essai, clé d'accès qui approuve.
- `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbLocalUserRepositoryTests.cs` :
  aller-retour des clés d'accès dans IndexedDB.
- Le pont `webAuthn.js` demande un vrai authentificateur : il n'a pas de test
  automatique ; il se vérifie à la main dans Chrome (authentificateur virtuel
  des outils de développement, onglet WebAuthn).
