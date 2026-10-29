> [English version](../../../../en/wasm/dev/adr/0189-app-sources-trust-and-per-user-install.md)

# ADR 0189 : sources d'apps de confiance, installation pour soi et validation d'un administrateur

## Statut

Accepté le 2026-10-29 par Benoit, dans le fil « Planifier le Hack Store »
(réponses en fin de document). Aucun code n'est écrit par cette ADR. **Révise
l'[ADR 0182](0182-install-apps-from-github.md)** : sa règle « installation pour
la machine seulement, administrateur requis » (Q4) est remplacée par les §3 et
§4 ci-dessous ; le reste de l'ADR 0182 tient. Sert le Hack Store de
l'[ADR 0188](0188-hack-store-and-app-catalog.md) et `hackpkg`.

## Contexte

- `/etc/hackeros/app-sources.config` (ADR 0182) liste les hôtes contactés et
  les propriétaires GitHub de confiance (`github.com/Kazo-ca`). Une app d'une
  autre source demande `--allow-untrusted` et un administrateur.
- Toute installation est aujourd'hui pour la machine, sous `sudo`.
- Le code d'une app installée tourne dans le même runtime et la même origine
  que l'OS : la confiance dans la source reste la vraie barrière.
- Benoit veut : les apps de notre catalogue toutes de confiance (relues et
  compilées par nous) ; un utilisateur qui installe pour lui-même sans
  administrateur depuis une source de confiance ; une liste noire de
  l'administrateur ; une validation en deux étapes, à chaque fois, pour une
  installation sur tout le système.

## Décision

### 1. Les sources

Deux sortes de sources, dans `/etc/hackeros/app-sources.config` :

```text
[hosts]
github=raw.githubusercontent.com

# Catalogues (ADR 0188). Nom = adresse de catalog.json.
[catalogs]
official=https://raw.githubusercontent.com/Kazo-ca/HackerOs-AppMarketPlace/packages/catalog.json

# Sources de confiance.
[trusted]
catalog:official=true
github.com/Kazo-ca=true

[updates]
interval-hours=6
```

- Une source est un **catalogue** (un autre dépôt du format de l'ADR 0188) ou
  un **dépôt GitHub** seul, ou tous les dépôts d'un propriétaire
  (`github.com/<owner>`).
- **Sources compilées avec l'OS** : les valeurs par défaut du fichier sont
  fixées à la compilation de l'hôte (propriété MSBuild de l'hôte, par défaut le
  catalogue officiel et `github.com/Kazo-ca`). Elles sont de confiance sans
  rien faire.
- **Sources ajoutées par un administrateur**, sans redéployer : par Paramètres ▸
  Apps ▸ Sources ou `hackpkg sources add`, avec une authentification
  d'administrateur. À l'ajout, l'administrateur choisit « de confiance » ou non
  (**non** par défaut).

### 2. Niveaux de confiance montrés à l'utilisateur

- **Vérifiée** : app du catalogue officiel, relue et compilée par nous.
- **De confiance** : autre source compilée avec l'OS ou marquée de confiance
  par un administrateur.
- **Non fiable** : toute autre source. Chaque installation demande un
  administrateur (§4), même pour un seul utilisateur.

### 3. Installer pour soi, sans administrateur

- Une app d'une source **Vérifiée** ou **De confiance** s'installe pour
  l'utilisateur courant **sans administrateur**, sauf si la liste noire (§5)
  la bloque.
- Fichiers dans `~/.local/share/hackeros/apps/<appId>/<version>/`, inscription
  dans `~/.config/hackeros/installed-apps.config` (section `[installed]`, même
  forme que dans `apps.config`).
- L'app apparaît à l'ouverture suivante de la session de cet utilisateur
  (redémarrage, ADR 0182 Q5 C) et pour lui seul. Le catalogue d'apps devient
  donc en partie propre à l'utilisateur de la session.
- Une seule copie à la fois : on ne peut pas installer pour soi une app déjà
  installée pour le système ; l'installer pour le système retire les copies
  personnelles (les réglages `~/.config/<appId>/` restent).
- L'utilisateur met à jour et désinstalle ses propres apps sans administrateur.

### 4. Installer pour le système : deux étapes, à chaque fois

Installer, mettre à jour ou retirer une app **pour tout le système**, ou
installer depuis une source **Non fiable**, demande :

1. **Connexion d'un administrateur** avec son compte (mot de passe ou passkey,
   ADR 0168). Aucune session d'administrateur gardée ouverte, aucun délai de
   `sudo` réutilisé : la connexion est demandée à chaque opération.
2. **Fenêtre de validation** qui montre l'app (nom, icône, version, taille),
   sa source et son niveau de confiance, et les capacités demandées (les
   dangereuses en évidence, ADR 0049). L'administrateur approuve ou refuse.

La même fenêtre de l'OS sert au Store et au terminal : `hackpkg install
--system …` l'ouvre, et `sudo hackpkg` ne suffit plus pour ces opérations.
Les fichiers vont dans `/usr/lib/hackeros/apps/` comme dans l'ADR 0182.

### 5. La liste noire de l'administrateur

Nouveau fichier `/etc/hackeros/app-policy.config` (`system:system 0644`,
modifiable par un administrateur avec `sudo nano` ou Paramètres ▸ Apps) :

```text
[blocked-apps]
org.example.chat=true

[blocked-categories]
social=true

[blocked-capabilities]
network.real.access=true
```

- Une app bloquée par son id, sa catégorie ou une capacité qu'elle demande ne
  s'installe plus pour soi : il faut l'installation système du §4.
- Les valeurs par défaut sont fixées à la compilation de l'hôte (comme les
  sources, §1) ; le fichier est créé au démarrage s'il n'existe pas.
- Le Store montre pourquoi une app demande un administrateur.

### 6. `hackpkg`

```text
hackpkg install <appId | source>[@<version>] [--user | --system]
hackpkg remove <appId> [--user | --system]
hackpkg update [<appId>]
hackpkg list
hackpkg info <appId | source>
hackpkg sources [add <catalogue | github:owner[/repo]> [--trusted] | remove <nom>]
hackpkg policy
```

Sans option, `install` choisit `--user` si c'est permis, sinon il propose
l'installation système (§4). `--allow-untrusted` disparaît : une source non
fiable passe toujours par la fenêtre du §4.

## Conséquences

- Un utilisateur sans droits peut ajouter du code à sa propre session ;
  comme ce code partage le runtime et l'origine de l'OS, il pourrait en
  théorie lire les données d'autres comptes du même navigateur. La confiance
  dans la source (relue et compilée par nous pour le catalogue officiel) et la
  liste noire en tiennent lieu.
- Le catalogue des apps est construit à l'ouverture de la session, avec les
  apps du système puis celles de l'utilisateur.
- Les deux étapes à chaque fois sont plus lentes pour un administrateur, au
  profit d'une validation explicite de chaque installation système.
- `sudo hackpkg` des scripts existants doit passer à `hackpkg --system`.

## Réponses de Benoit

- **Q4** Tout notre catalogue de confiance ; ajout d'un autre catalogue ou d'un
  dépôt seul par un administrateur ; seules les sources compilées avec l'OS
  sont de confiance sans changement par un administrateur (réponse de Benoit).
- **Q5** Installation pour soi sans administrateur depuis une source de
  confiance, sauf liste noire (fixée à la compilation, modifiable par un
  administrateur) ; installation système validée en deux étapes, à chaque fois
  (réponse de Benoit).
- **Q24** B : garder `github.com/Kazo-ca` de confiance en plus du catalogue
  officiel, et un administrateur peut ajouter une source de confiance sans
  redéployer.
- **Q25** A : l'administrateur choisit « de confiance » à l'ajout, non par
  défaut. **Q26** A : liste noire par app, catégorie et capacité. **Q27** A :
  une seule copie à la fois. **Q28** A : la même fenêtre pour `hackpkg`, sans
  délai de `sudo`.

## Tâches

- [ ] `app-sources.config` : sections `[catalogs]` et `[updates]`, valeurs par
      défaut fixées à la compilation de l'hôte, sources ajoutées par un administrateur.
- [ ] `app-policy.config` (lecture, valeurs par défaut, création au démarrage).
- [ ] Installation pour soi (`~/.local/share/hackeros/apps`,
      `installed-apps.config`), catalogue construit à l'ouverture de la session.
- [ ] Fenêtre de validation en deux étapes, partagée par le Store et `hackpkg`.
- [ ] `hackpkg` : `--user`, `--system`, `sources add/remove`, `policy`.
- [ ] Paramètres ▸ Apps ▸ Sources et liste noire.
- [ ] Documentation : `app-installation.md`, `linux-commands.md`.
