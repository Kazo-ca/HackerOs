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

Décidé le 2026-10-29. Chantiers faits : 2 (heure locale, ADR 0193) et 4 (menus
contextuels, ADR 0194). Restent :

| Chantier | Décisions |
| --- | --- |
| 1. Bulles de notification | Bulle (toast) pour toutes les notifications de l'OS et des apps : en bas à droite, au-dessus de la barre de l'écran 1, ~6 s, pile de 3 au plus, elles restent ensuite dans le panneau de l'horloge. « Update ready » devient une bulle + une notification persistante avec le bouton « Redémarrer », aussi visible sur l'écran de connexion (qui n'a pas de barre). Aujourd'hui les notifications n'existent que dans le panneau de l'horloge, et « Update ready » est un `aside` fixe dans `App.razor`, caché sous la barre. |
| 3. Animations des fenêtres | Ouvrir, fermer, minimiser (vers le bouton de la barre de **son** écran), restaurer, agrandir, snap. Rapide et sobre (~150 à 200 ms, fondu + échelle). Coupées par le réglage existant « Réduire les animations ». Durée et courbe en variables CSS `--hos-window-anim-*` qu'un thème peut redéfinir. La minimisation retarde l'attribut `hidden` jusqu'à la fin de l'animation. |
| 5. Aperçu au survol + peek | Copie réduite en direct (HTML cloné, CSS `scale`), image de repli (icône + titre) pour les iframes et canvas. Apparaît après ~400 ms ; clic = active, « × » = ferme ; pas sur mobile. Survoler l'aperçu rend les autres fenêtres transparentes (peek, comme Windows). Le clone ne duplique pas les ID HTML et n'a pas de champs interactifs (axe). |

Une ADR par sujet, un chantier par ADR, multi-écran pris en compte partout
(ADR 0178 à 0184).

## Navigateur : afficher les vrais sites (issue #266)

Analyse du 2026-10-29, pas d'ADR. Décisions :

- Pas d'iframe directe vers le vrai site. Le contenu récupéré passe par le
  pipeline existant (récupération en C#, rendu dans un `srcdoc` isolé,
  ADR 0079).
- Ordre des sources : réseau simulé → proxy du serveur HackerOs → proxy public
  tiers (désactivable) → Wayback Machine automatiquement en dernier recours.
- Firefox et Safari : afficher « non supporté » quand une fonction exige
  Chrome/Edge.
- Barre d'adresse : une icône montre d'où vient la page (simulé, serveur,
  proxy, archive), un cadenas pour HTTPS et un avertissement « non sécurisé ».
- Lacunes à combler (listées dans #266) : le Navigateur n'a pas la capacité
  `network.real.access` ni d'entrée `GrantedApps` côté serveur ; les cookies
  sont perdus dans `SendRealAsync` et l'en-tête `Cookie` est bloqué côté
  serveur ; le quota de 8 requêtes simultanées rejette les ressources d'une
  page.

## Apps tierces portées (dépôts `Kazo-ca/HackerOs-App-*`)

Décidé le 2026-10-28 :

- Chaque projet open source enveloppé vit dans son propre dépôt
  (`HackerOs-App-Webamp`, `HackerOs-App-IRC` avec KiwiIRC,
  `HackerOs-App-BoxedWine`, et d'autres), créé à partir de
  `HackerOs-AppTemplate` (ADR 0176) et listé dans `HackerOs-AppMarketPlace`
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
  du chantier `wwwroot` des apps installées (issue #247).

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
profonds par URL (`?app=`, `?open=`), animations des fenêtres (voir #242),
boîte « Exécuter » (Super+R), économiseur d'écran et verrouillage après
inactivité, recherche de fichiers dans le lanceur, aperçu au survol (voir #242),
miniatures de fichiers (`~/.cache/thumbnails`), visionneuses (Markdown, PDF,
images, vidéo), outils de développement Eruda, infobulles de fichiers riches,
fonds d'écran animés ou en diaporama, `git clone` (exige un proxy CORS), Python (plutôt dans un dépôt d'app).
Questions à reposer au démarrage : où vivent les visionneuses, portée de
`?app=`, Eruda ou non.

## Éditeurs (décision close, 2026-10-17)

- Code Editor **reste une app système**.
- Le remplacement de l'éditeur de texte reste **sans expressions régulières**.

## Questions posées, réponse à confirmer

- **Changer d'utilisateur** (issue #290) : plusieurs sessions ouvertes en même
  temps ; le comportement cible a été demandé à Benoit le 2026-10-30.
- **« Nouveau dossier » dans les dialogues** (ADR 0175) : rendre
  `SelectFolderDialogRequest.AllowCreateFolder` vrai par défaut pour toutes
  les apps ? Recommandé : garder l'option par app. Posée le 2026-10-26.

## Lacunes connues

- La géométrie des fenêtres n'est pas enregistrée à la déconnexion.
- Chrome capture `Ctrl+Maj+T` et `Ctrl+Maj+W` sauf quand HackerOS est installé
  en PWA (ADR 0061).
