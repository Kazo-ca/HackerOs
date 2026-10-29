> [English version](../../../../en/wasm/dev/adr/0187-guest-start-and-sign-in-credits.md)

# ADR 0187 : démarrer en invité et crédits sur la page de connexion

## Statut

Accepté le 2026-10-29. Décisions prises avec Benoit dans le fil « Démarrer en invité et
crédits de connexion » (questions 1 à 10 posées le 2026-10-28, réponses le 2026-10-29).
Partie B (crédits, §7 à §9) mise en œuvre le 2026-10-29 (branche `login/1-credits`). Partie A
(compte invité) mise en œuvre le 2026-10-29 (série `guest/base`, PR #279, #281 et #286) ;
voir « Mise en œuvre » plus bas et [`guest-account.md`](../guest-account.md).

- **Complète :** [ADR 0013](0013-local-user-session.md) (comptes et session locale),
  [ADR 0120](0120-passwd-and-administrator-password-reset.md) (`passwd`, Paramètres ▸
  Utilisateurs), [ADR 0168](0168-passkey-sign-in.md) (clés d'accès et trousseau),
  [ADR 0169](0169-administrator-adds-and-removes-users.md) (ajout et suppression de comptes).

## Contexte

Benoit a demandé deux choses :

1. pouvoir démarrer l'OS la première fois sans créer de compte, avec un bouton
   `Start as a Guest`, puis transformer cette session invité en vrai compte depuis
   Paramètres et depuis la page d'accueil / de connexion ;
2. afficher les crédits sur la page de connexion : `Auteur : Benoit Robin` (lien vers
   https://kazo.ca) et `Projet GitHub : (icône GitHub) Kazo-ca/HackerOs` (lien vers le dépôt).

État du code au moment de la décision (main `fd52528c`) :

- `App.razor` (hôte de l'écosystème) montre l'écran `FirstRun` quand aucun compte n'existe :
  formulaire login, nom affiché, mot de passe (8 caractères au moins), qui crée un
  administrateur du groupe `administrators` puis ouvre sa session. Sinon l'écran `Login`
  liste les comptes (mot de passe ou clé d'accès).
- `LocalUser.Credential` peut déjà être `null` : `LocalSessionService.LoginAsync` accepte
  alors la connexion sans mot de passe et le trousseau s'ouvre sans mot de passe.
- `AdminElevationVerifier` exige un mot de passe (ou une clé d'accès) : un administrateur sans
  mot de passe ne peut pas confirmer une élévation aujourd'hui.
- Un compte ne peut pas être renommé. Le dossier personnel est `/home/<login>`
  (`LocalUser.HomePath`).
- Les installations d'apps ont une portée Utilisateur et une portée Machine (admin).
- Le pied de page `ProjectInformation.razor` affiche `HackerOS <version>` et l'URL brute
  `https://github.com/Kazo-ca/HackerOs` sur les écrans de premier démarrage, de connexion,
  de récupération et d'erreur. Paramètres ▸ À propos affiche la version et l'URL du dépôt.

## Décision

### Partie A : l'invité

#### 1. L'invité est un vrai compte local, conservé (Q1 : A)

- Un compte local `guest`, **sans mot de passe** (`Credential = null`), dont les fichiers
  (`/home/guest`) et les réglages restent d'une visite à l'autre.
- Le nom de connexion `guest` est **réservé** (Q7 : A) : on ne peut pas créer un compte
  nommé `guest` (ni à l'assistant, ni par `HackUserAccounts.CreateAsync`), comme
  `hackeros-svc`, `root` et `administrators`.
- Le nom affiché est « Invité » ou « Guest » selon la langue du système (`/etc/locale.conf`).
- Le compte porte une marque d'invité (par exemple un champ `IsGuest` dans le dépôt des
  comptes) : on ne le reconnaît pas seulement à son nom.

#### 2. L'invité n'existe que s'il n'y a aucun compte enregistré, et il est administrateur (Q2 : réponse de Benoit)

- Le bouton `Start as a Guest` n'est offert **que lorsqu'aucun compte enregistré n'existe**,
  donc sur l'écran de premier démarrage, à côté du formulaire de création.
- L'invité a l'autorité **Administrateur** et est membre de `administrators`.
- Comme il n'a pas de mot de passe, l'élévation de l'invité (agent d'authentification
  administrateur, approbation d'une capacité dangereuse) se fait par **simple confirmation**
  dans la fenêtre de l'agent, sans saisie. Cette règle vaut **seulement** pour le compte
  invité ; un autre compte sans mot de passe reste refusé comme aujourd'hui. Chaque
  élévation reste écrite dans `auth.log`.
- Le risque est accepté : tant que l'invité existe, il est le seul compte, il n'y a donc les
  fichiers de personne d'autre à protéger.

#### 3. Un seul invité, jamais recréé (Q4 : A modifié par Benoit)

- Il existe au plus un compte invité.
- Tant que l'invité est le seul compte, l'écran de connexion propose
  `Continue as Guest` (connexion directe, sans mot de passe) et le lien « Créer mon compte »
  (§4).
- Dès qu'un compte enregistré existe, l'invité **ne peut plus être créé**, même après sa
  conversion ou sa suppression. Le bouton invité ne réapparaît pas sur cette installation
  (ce navigateur) tant qu'un compte enregistré existe.
- Pas de réglage « autoriser l'invité » dans Paramètres : la règle précédente le rend inutile.

#### 4. Créer son compte depuis l'invité : migrer ou supprimer l'invité (Q2 et Q3 : réponses de Benoit)

Créer un compte quand l'invité existe ouvre toujours le même formulaire : login, nom
affiché, mot de passe et confirmation (8 caractères au moins, règles de l'ADR 0120 ;
Q6 : A, la clé d'accès s'ajoute ensuite dans Paramètres ▸ Clés d'accès). Le formulaire
demande un choix :

- **Migrer l'invité** (choix par défaut) : conversion sur place. Le compte garde son
  identifiant (`LocalUserId`), donc la propriété de ses fichiers, ses autorisations de
  capacités et ses apps installées. Le login `guest` devient le nouveau login, le nom
  affiché est remplacé, la marque d'invité est retirée, le mot de passe est posé et le
  trousseau est rechiffré avec lui. `/home/guest` est déplacé vers `/home/<login>`. Les
  chemins absolus `/home/guest` écrits dans les fichiers de réglages de l'utilisateur
  (`~/.config`) sont réécrits ; ceux écrits ailleurs par l'utilisateur ne le sont pas
  (limite documentée).
- **Supprimer l'invité** : un nouveau compte administrateur est créé, puis le compte invité
  et son dossier `/home/guest` sont supprimés (comme `userdel -r`, ADR 0169, sans case à
  cocher). Une confirmation prévient que les fichiers de l'invité seront perdus. Les apps
  installées pour la machine restent ; celles installées pour l'invité seul disparaissent
  avec lui.

Dans les deux cas le compte créé est **administrateur** (c'est le premier compte
enregistré), la session se poursuit ou s'ouvre sous ce compte, et `auth.log` reçoit
`users.guest-migrate` ou `users.guest-delete`.

Tant que l'invité existe, Paramètres ▸ Utilisateurs ne propose pas « Ajouter un compte »
(ADR 0169) : il propose seulement « Créer mon compte ». Le premier compte enregistré passe
toujours par le choix migrer / supprimer. Après, l'ajout de comptes suit l'ADR 0169.

#### 5. Où on propose « Créer mon compte » (Q5 : A)

- **Paramètres ▸ Utilisateurs** : un bandeau en haut de la page quand la session est celle
  de l'invité, avec le bouton « Créer mon compte » (formulaire du §4).
- **Page de connexion** : un lien « Créer mon compte » sous `Continue as Guest` ; il ouvre le
  même formulaire, puis ouvre la session du compte créé.
- **Menu Démarrer** : un rappel discret dans la tuile de l'utilisateur (« Invité · Créer mon
  compte ») qui ouvre Paramètres ▸ Utilisateurs.
- Pas de notification de rappel à la connexion.

#### 6. Ce que l'invité ne change pas

- Le serveur (`HackerOs.Server`, comptes serveur, ADR 0024 et 0134) n'est pas concerné :
  l'invité est un compte local du navigateur, comme les autres comptes locaux.
- Le SDK des apps ne change pas pour les apps ordinaires. `HackUserAccount` peut exposer
  un indicateur « invité » pour Paramètres ; s'il est ajouté, il passe par
  `PublicAPI.Unshipped.txt`.

### Partie B : les crédits

#### 7. Pied de page de l'hôte (Q8 : A)

Dans `ProjectInformation.razor`, sur tous les écrans où il apparaît (premier démarrage,
connexion, récupération, erreur) :

```
HackerOS <version>
Auteur : Benoit Robin                           → lien https://kazo.ca
Projet GitHub : [icône GitHub] Kazo-ca/HackerOs → lien https://github.com/Kazo-ca/HackerOs
```

La ligne « Projet GitHub » remplace l'URL brute. Les liens s'ouvrent dans un nouvel onglet
(`target="_blank" rel="noopener noreferrer"`). L'icône GitHub est une SVG en ligne (aucune
ressource externe), avec `aria-hidden="true"` ; le texte du lien suffit aux lecteurs d'écran.
L'avis de l'ancienne version (domaine public) ne change pas.

#### 8. Paramètres ▸ À propos (Q9 : A)

Une ligne « Auteur : Benoit Robin » avec le lien kazo.ca, et la ligne du dépôt au même
format `[icône] Kazo-ca/HackerOs`.

#### 9. Langue (Q10 : A)

Les libellés suivent la langue du système (écrans de connexion) ou de la session
(Paramètres) : « Auteur » / « Author », « Projet GitHub » / « GitHub project ». Le nom
« Benoit Robin » ne se traduit pas.

## Conséquences

- Premier démarrage sans friction ; l'invité garde son travail et peut devenir un vrai
  compte sans rien perdre.
- Un nouveau cas dans l'élévation : la confirmation sans mot de passe limitée au compte
  invité, à tester (aucun autre compte sans mot de passe ne doit en profiter).
- Le renommage d'un compte (login et dossier personnel) est nouveau ; il sert seulement à
  la migration de l'invité.
- Tests à prévoir : bouton invité absent dès qu'un compte existe, `guest` réservé,
  migration (identifiant, propriété, trousseau, dossier déplacé), suppression (dossier
  supprimé), élévation par confirmation seulement pour l'invité, rendu du pied de page
  (`ProjectInformationTests` : liens kazo.ca et dépôt), libellés en français et en anglais.

## Choses à faire

Suivi : [issue #243](https://github.com/Kazo-ca/HackerOs/issues/243).
Une PR par chantier ; les crédits d'abord (petit et indépendant).

1. Crédits dans le pied de page de l'hôte et dans Paramètres ▸ À propos, libellés FR/EN
   (§7 à §9).
2. Compte invité : marque d'invité, `guest` réservé, bouton `Start as a Guest` au premier
   démarrage, `Continue as Guest` à la connexion, jamais recréé (§1 à §3).
3. Élévation par simple confirmation pour l'invité seulement (§2).
4. Renommage du compte invité (login, `/home/guest` → `/home/<login>`, réglages réécrits,
   trousseau rechiffré) et suppression de l'invité (§4).
5. Formulaire « Créer mon compte » avec le choix migrer / supprimer : page de connexion,
   Paramètres ▸ Utilisateurs (bandeau), tuile du menu Démarrer (§4, §5).
6. Documentation : `settings-app.md`, guide de l'utilisateur, FR et EN.

Tous les points sont faits (2026-10-29).

## Mise en œuvre

Écarts et précisions par rapport à la décision (partie A) :

- **Le formulaire appartient à l'OS.** « Créer mon compte » s'affiche sur l'écran de
  connexion, comme le formulaire du premier compte. Depuis Paramètres ▸ Utilisateurs ou le
  menu Démarrer, l'app demande ce formulaire à l'OS
  (`HackUserAccounts.RequestGuestAccountCreationAsync`) : la session invité se ferme (ses
  apps s'arrêtent), puis le formulaire s'ouvre. L'app ne voit donc jamais le mot de passe,
  et la conversion se fait sans session ouverte, comme `usermod` qui refuse un utilisateur
  connecté. Ensuite la session du compte créé s'ouvre.
- **Le dossier personnel est copié, pas renommé.** Les dossiers que le système a créés pour
  l'invité appartiennent au nom de connexion `guest` ; ils sont donc recopiés au nom du
  nouveau login (comme `cp -a` puis `chown -R`), puis `/home/guest` est supprimé. Les dates
  de modification sont celles de la copie.
- **Trousseau.** L'invité n'a pas de trousseau (pas de mot de passe) : rien à rechiffrer ; le
  trousseau du compte est créé à sa première connexion.
- **Réécriture des chemins** : fichiers texte de `~/.config` et cibles des liens symboliques.
- **Fermeture de session.** En testant, deux défauts de la fermeture de session normale ont
  été corrigés : l'écran restait sur le démarrage après « Fermer la session », et les
  fenêtres de la session précédente restaient ouvertes. Les apps de la session sont
  maintenant arrêtées avant la fin de la session
  (`AppLifecycleOrchestrator.StopSessionAppsAsync`).
