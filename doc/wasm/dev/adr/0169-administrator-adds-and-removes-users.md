# ADR 0169 : un administrateur ajoute et supprime des comptes

## Statut

Proposé le 2026-10-25, à la demande de Benoit (« ajouter la possibilité de
pouvoir ajouter ou supprimer des user quand on est administrateur »). La
question du dossier personnel a été posée à Benoit dans le fil du chantier ; le
code suit le choix recommandé (★) en attendant sa réponse.

- **Complète :** [ADR 0013](0013-local-user-session.md) (comptes et groupes
  locaux), [ADR 0120](0120-passwd-and-administrator-password-reset.md)
  (`users.manage`, `HackUserAccounts`, page Paramètres ▸ Utilisateurs).
- **Va avec :** [ADR 0168](0168-passkey-sign-in.md) (clés d'accès).
- **Ajoute au SDK 2.1 :** des membres dans `PublicAPI.Unshipped.txt`.
- **Documents :** [`settings-app.md`](../settings-app.md),
  [`../../framework/system-administration.md`](../../framework/system-administration.md).

## Contexte

- Le seul compte créé l'est par l'assistant du premier démarrage
  (administrateur, groupe `administrators`). Rien ne permet d'en ajouter un
  autre ni d'en supprimer un.
- `ILocalUserRepository` sait créer un compte mais pas le supprimer. Le dossier
  personnel est créé à la première connexion (`FileSystemSeeder`).
- La capacité dangereuse `users.manage` et l'agent d'authentification
  administrateur existent déjà (ADR 0120).

## Décision

### 1. Ajouter un compte, comme `useradd -m` + `passwd`

- `HackUserAccounts.CreateAsync(new HackNewUserAccount(login, nomAffiché,
  autorité, motDePasse))` exige `users.manage` **et** un administrateur (fenêtre
  de l'agent ; rien sous `sudo`). `auth.log` : `users.add`.
- Le nom de connexion suit `LocalLoginName` (minuscules, chiffres, `-`, `_`,
  32 caractères au plus), n'est pas réservé (`hackeros`, ADR 0097) et n'est ni
  un compte ni un groupe existant. Le nom affiché est obligatoire (128
  caractères au plus).
- Le mot de passe initial est obligatoire et suit les règles de l'ADR 0120.
- Groupes, comme `USERGROUPS_ENAB` : un groupe propre du même nom devient le
  groupe principal ; un administrateur est aussi membre de `administrators`.
- Le dossier personnel est créé à la première connexion, comme aujourd'hui.

### 2. Supprimer un compte, comme `userdel` ★

- `HackUserAccounts.DeleteAsync(login, supprimerDossier)` exige `users.manage`
  et un administrateur. `auth.log` : `users.delete`.
- Refusé pour : le compte de la session (on ne se supprime pas soi-même), le
  dernier administrateur actif, le compte de service `hackeros`.
- Le compte, ses clés d'accès (ADR 0168) et ses autorisations de capacités
  sont supprimés. Le groupe propre du compte est laissé (aucun fichier n'est
  réattribué).
- **Dossier personnel ★** : une case « Supprimer aussi le dossier personnel »,
  décochée par défaut (`userdel` le garde, `userdel -r` le supprime). Le
  dossier est supprimé en opération système, trousseau compris. Gardé, il
  reste à `/home/<login>` ; un compte recréé sous le même nom le retrouve.

### 3. Paramètres ▸ Utilisateurs

- Pour un administrateur : un formulaire « Ajouter un utilisateur » (nom de
  connexion, nom affiché, type standard/administrateur, mot de passe et
  confirmation) et, dans la liste, un bouton « Supprimer » par compte autre que
  le sien, avec la case du dossier personnel et une confirmation.
- Pour tous : une section « Clés d'accès » (ADR 0168 §5) qui liste ses clés,
  en ajoute et en retire.
- Seul le contenu de la page Utilisateurs change ; la navigation de
  Paramètres n'est pas touchée.

### 4. Où vit le code

- `Simulation.Abstractions` : `ILocalUserRepository.DeleteUserAsync`
  (implémentation par défaut : `NotSupportedException`) ; le dépôt refuse
  lui-même de supprimer le dernier administrateur actif.
- `Platform.Core/Sessions/LocalAccountService` : création et suppression,
  règles du §1 et §2, `auth.log`.
- `AppUserAccountsGateway` : capacité, agent administrateur, puis le service.

### 5. Question posée à Benoit

Dossier personnel à la suppression : **A ★** case décochée par défaut,
**B** toujours supprimé, **C** jamais supprimé.

## Conséquences

- Plusieurs personnes peuvent avoir leur session sur la même installation,
  chacune avec son dossier, ses réglages et son trousseau.
- Un compte supprimé dont le dossier est gardé laisse des fichiers dont le
  propriétaire n'existe plus ; `ls -l` montre alors l'identifiant brut.
