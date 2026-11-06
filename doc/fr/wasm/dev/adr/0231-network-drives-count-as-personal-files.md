> [English version](../../../../en/wasm/dev/adr/0231-network-drives-count-as-personal-files.md)

# ADR 0231 : les lecteurs réseau de l'utilisateur comptent comme ses fichiers personnels

## Statut

Acceptée le 2026-11-06 ; Benoit a choisi la recommandation (question 2 du fil
« Concevoir l'app Webamp avec Benoit », voir « Questions posées à Benoit »).
Issue [#381](https://github.com/Kazo-ca/Os/issues/381).

- **Remplace :** l'[ADR 0181](0181-host-folder-mounted-as-a-network-drive.md)
  §6 (« Accès des apps »), seulement pour le lecteur de l'utilisateur qui agit.
- **Ne change pas :** le montage, les modes `0700`/`0600`, le propriétaire, ni
  qui monte et démonte (ADR 0181 §1 à §5 et §7 à §9) ; le classement des
  capacités ([`capabilities.md`](../../framework/capabilities.md)).

## Contexte

- `FileSystemService.ResolveCapability` choisit la capacité qu'une app doit
  avoir selon le chemin : `filesystem.private.*` pour `~/.config/<appId>`,
  `filesystem.user-home.*` dans `/home/<utilisateur>`, et
  `filesystem.system.*` partout ailleurs. Les capacités `filesystem.system.*`
  sont **dangereuses** : un administrateur les approuve à l'installation.
- Un lecteur réseau (ADR 0181) se monte dans `/mnt/<nom>`, hors du home. Une
  app qui n'a que `filesystem.user-home.read` ne peut donc pas lire un fichier
  du lecteur, même quand l'utilisateur l'ouvre lui-même d'un double-clic, par
  « Ouvrir avec » ou par le dialogue d'ouverture de l'OS.
- L'ADR 0181 §6 dit qu'une app « passe par les dialogues d'ouverture et
  d'enregistrement ». En réalité, aucun dialogue ne donne de droit sur le
  fichier choisi : `filesystem.user-selected.*` existe dans le catalogue des
  capacités mais n'est accordée ni vérifiée nulle part. Chess ne peut pas ouvrir
  un `.pgn` d'un lecteur réseau, et l'app Webamp prévue ne pourrait pas jouer un
  `.mp3`.
- Le lecteur appartient à l'utilisateur qui l'a monté (mode `0700`) : c'est un
  dossier de **son** ordinateur, qu'il a choisi lui-même dans le dialogue du
  navigateur. Sous Windows et macOS, un lecteur réseau de l'utilisateur est
  ouvert aux apps comme ses documents.

## Décision

1. **Capacité.** Un chemin situé dans un montage `hostfs` dont le propriétaire
   est l'utilisateur qui agit demande `filesystem.user-home.read` pour lire et
   `filesystem.user-home.write` pour écrire, comme son dossier personnel.
   `ResolveCapability` demande au routeur des montages le montage qui contient le
   chemin, et compare son propriétaire à l'utilisateur de l'opération.
2. **Permissions.** Les bits de permission et le propriétaire s'appliquent
   toujours après la capacité : rien ne change pour les autres comptes (le
   lecteur reste en `0700`).
3. **Hors du lecteur de l'utilisateur.** Le lecteur d'un autre utilisateur,
   `/mnt` lui-même et tout autre chemin hors du home restent sous
   `filesystem.system.*`. L'explorateur, les commandes et les dialogues de l'OS
   gardent leurs capacités actuelles.
4. **Un lecteur déconnecté** (permission du navigateur pas encore redonnée) répond
   comme aujourd'hui (`IsReady` faux, erreur du fournisseur) ; la capacité ne
   change rien à cela.
5. **Aucune API nouvelle.** Le changement est dans la vérification de l'OS : une
   app sur le SDK 3.0 en profite dès que l'OS qui l'exécute l'a. Sur un OS plus
   ancien, un fichier du lecteur donne toujours « Permission refusée ».
6. **`filesystem.user-selected.*`** reste telle quelle : un droit par fichier
   choisi n'est pas décidé ici (il ne couvrirait pas une liste de lecture `.m3u`
   ni « Ajouter un dossier », qui lisent d'autres fichiers du lecteur).

## Questions posées à Benoit

Réponse de Benoit (2026-11-06) : « ok », la recommandation (A).

2. Les apps ne peuvent pas lire `/mnt` aujourd'hui. On corrige comment ?
   - A) Dans l'OS : les lecteurs réseau de l'utilisateur comptent comme ses
     fichiers personnels (même capacité que `/home`). **(Recommandé, choisi)**
   - B) Dans l'OS : un fichier que l'utilisateur ouvre lui-même devient lisible
     par l'app, ce fichier seulement (une liste `.m3u` ou « Ajouter un dossier »
     sur `/mnt` ne marcherait pas).
   - C) L'app déclare `filesystem.system.read` (approbation d'un administrateur
     à l'installation, lecture de tout le système).

## Conséquences

- Une app qui a `filesystem.user-home.read` lit les fichiers des lecteurs
  réseau de l'utilisateur ; avec `.write`, elle y écrit. Le texte qui décrit
  `filesystem.user-home.*` à l'installation devra dire « vos fichiers, y compris
  vos lecteurs réseau ».
- Webamp ([Os-App-Webamp](https://github.com/Kazo-ca/Os-App-Webamp)) et Chess
  ouvrent les fichiers d'un lecteur réseau sans capacité dangereuse.
- Tests prévus : capacité résolue pour son propre lecteur, refus pour le lecteur
  d'un autre utilisateur et pour `/mnt`, lecture par `KazoFile` sur un faux
  lecteur, même résultat dans un Web Worker.
- Docs à mettre à jour au code : [`capabilities.md`](../../framework/capabilities.md),
  [`file-explorer.md`](../file-explorer.md) (lecteurs réseau), `features.md`,
  entrée `changes/unreleased`.
