> [English version](../../../en/wasm/memory/decisions-not-built.md)

# Décisions prises, pas encore construites

État au **2026-10-30**. Benoit a pris ces décisions en discussion ; elles ne
sont pas encore dans le code, et la plupart ne sont écrites dans aucun ADR. Un
contributeur qui reprend un de ces sujets part d'ici, écrit l'ADR (prochain
numéro libre dans [`README.md`](README.md)) et pose à Benoit les questions qui
restent ouvertes, avec des choix (section IV de
[`AGENTS.md`](../../../../AGENTS.md)).

Rien ici ne se code sans que Benoit lance le sujet.

## Fenêtres et barre des tâches (issue #242, série `taskbar/`)

Décidé le 2026-10-29. Chantiers faits : 1 (bulles de notification, ADR 0197), 2 (heure locale,
ADR 0193), 3 (animations des fenêtres, ADR 0198) et 4 (menus contextuels,
ADR 0194). Restent :

| Chantier | Décisions |
| --- | --- |
| 5. Aperçu au survol + peek | Copie réduite en direct (HTML cloné, CSS `scale`), image de repli (icône + titre) pour les iframes et canvas. Apparaît après ~400 ms ; clic = active, « × » = ferme ; pas sur mobile. Survoler l'aperçu rend les autres fenêtres transparentes (peek, comme Windows). Le clone ne duplique pas les ID HTML et n'a pas de champs interactifs (axe). |

Une ADR par sujet, un chantier par ADR, multi-écran pris en compte partout
(ADR 0178 à 0184).

## Navigateur : afficher les vrais sites (issue #266)

Analyse du 2026-10-29. Le proxy du serveur HackerOS est construit
([ADR 0210](../dev/adr/0210-browser-real-sites-through-the-proxy.md), série
`todo/base`) : droit du Navigateur, cookies, redirections, icône de source,
« Non sécurisé », erreurs. Décisions encore à construire :

- Ordre des sources : réseau simulé → proxy du serveur HackerOs (fait) → proxy
  public tiers (désactivable) → Wayback Machine automatiquement en dernier
  recours. Une icône de source par nouvelle source (proxy public, archive).
- Pas d'iframe directe vers le vrai site : tout passe par le `srcdoc` isolé.
- Firefox et Safari : afficher « non supporté » quand une fonction exige
  Chrome/Edge.

## Apps tierces portées (dépôts `Kazo-ca/HackerOs-App-*`)

Décidé le 2026-10-28 :

- Chaque projet open source enveloppé vit dans son propre dépôt
  (`HackerOs-App-Webamp`, `HackerOs-App-IRC` avec KiwiIRC,
  `HackerOs-App-BoxedWine`, et d'autres), créé à partir de
  `HackerOs-AppTemplate` (ADR 0176, modèle `hackeros-solution` de l'ADR 0207 ;
  le dépôt template est régénéré à la release qui contient ce modèle) et listé dans `HackerOs-AppMarketPlace`
  (catalogue du Hack Store, ADR 0188).
- La licence de chaque dépôt suit celle du projet enveloppé (GPL-2.0 pour
  BoxedWine, par exemple), pas MIT par défaut.
- Ordre de difficulté évalué : Webamp (le plus simple : MIT, `renderInto(div)`,
  pas de réseau), puis IRC, puis BoxedWine (le plus difficile). Benoit a choisi
  la première app de test ; son plan est dans son propre dépôt. Aucun jeu sur
  ce repository.
- Faits techniques : les fichiers des Releases GitHub n'ont pas d'en-tête CORS
  (le navigateur ne peut pas les lire), `raw.githubusercontent.com` et jsDelivr
  en ont ; l'hôte publié est trimmé, donc une app chargée à l'exécution peut
  manquer d'API (`MissingMethodException`) ; le JavaScript d'une app tourne
  sur la même origine que l'OS ; une app qui enveloppe du JavaScript a besoin
  du chantier `wwwroot` des apps installées (issue #247, fait par l'ADR 0206
  dans la série `todo/base` : fichiers servis à `_content/<assemblage>/` par le
  service worker, Web Workers compris).

## Apps hébergées par le serveur (« bac à sable serveur »)

Débat du 2026-10-19, récapitulatif validé par Benoit, rien de commencé :

- Comptes de confiance d'abord, public plus tard (moteur d'isolation
  interchangeable). Hôte Windows.
- **Apps natives** : une VM Hyper-V jetable par utilisateur (la VM est le bac
  à sable), OS invité au choix de l'administrateur, réserve chaude de 2 VM,
  file d'attente quand c'est plein. Installation par l'administrateur seulement
  + fichier de déclaration HackerOS ; accès par groupe (ex. `hostapps`). Un
  agent dans la VM capture chaque fenêtre, H.264 sur WebSocket + WebCodecs,
  entrées par socket Hyper-V. v1 : clavier, souris, presse-papiers texte,
  fenêtres secondaires. Dossier personnel par WinFsp depuis le stockage de
  synchronisation. Internet en NAT, pas de réseau local. Fermer l'app dans
  HackerOS ferme l'app ; perte du navigateur = délai de grâce. Visibles dans
  `ps`, `kill` et le Gestionnaire des tâches comme processus « host ».
- **Apps HackerOS côté serveur** : drapeau d'exécution serveur dans
  `[AppManifest]`, installées par l'administrateur, processus restreint par
  utilisateur, même SDK, fichiers = dossier synchronisé, réseau par les règles
  du relais. Grisées hors connexion.
- Prévu : 5 ADR et 9 chantiers, en commençant par une preuve de concept du
  rendu d'une fenêtre Blazor distante. Question restée ouverte : branche
  `host/base` depuis `main` (recommandé) ou PR directes vers `main`.

## Idées tirées de daedalOS

Évaluation du 2026-10-28. Benoit a choisi « rien pour l'instant » : finir les
séries en cours d'abord. Ce qui manque à HackerOS et pourrait servir : liens
profonds par URL (`?app=`, `?open=`), boîte « Exécuter » (Super+R), économiseur d'écran et verrouillage après
inactivité, recherche de fichiers dans le lanceur, aperçu au survol (voir #242),
miniatures de fichiers (`~/.cache/thumbnails`), visionneuses (Markdown, PDF,
images, vidéo), outils de développement Eruda, infobulles de fichiers riches,
fonds d'écran animés ou en diaporama, `git clone` (exige un proxy CORS), Python (plutôt dans un dépôt d'app).
Questions à reposer au démarrage : où vivent les visionneuses, portée de
`?app=`, Eruda ou non.

## Changer d'utilisateur (décision close, 2026-10-30)

- « Changer d'utilisateur » **reste grisé** (ADR 0201, issue #290) : une
  session à la fois. Un prototype de sessions simultanées est gardé, non
  fusionné, sur la branche `switch-user/simultaneous-sessions-prototype`.

## Éditeurs (décision close, 2026-10-17)

- Code Editor **reste une app système**.
- Le remplacement de l'éditeur de texte reste **sans expressions régulières**.

## Questions posées, réponse à confirmer

- **« Nouveau dossier » dans les dialogues** (ADR 0175) : rendre
  `SelectFolderDialogRequest.AllowCreateFolder` vrai par défaut pour toutes
  les apps ? Recommandé : garder l'option par app. Posée le 2026-10-26.

## Lacunes connues

- La géométrie des fenêtres n'est pas enregistrée à la déconnexion.
- Chrome capture `Ctrl+Maj+T` et `Ctrl+Maj+W` sauf quand HackerOS est installé
  en PWA (ADR 0061).
