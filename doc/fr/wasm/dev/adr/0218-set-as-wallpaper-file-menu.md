> [English version](../../../../en/wasm/dev/adr/0218-set-as-wallpaper-file-menu.md)

# ADR 0218 : « Définir comme fond d'écran » dans le menu des images

## Statut

Acceptée le 2026-10-31 ; Benoit a choisi les recommandations (voir
« Questions posées à Benoit »). Issue
[#298](https://github.com/Kazo-ca/HackerOs/issues/298), série `todo/base`
(branche `todo/24-set-as-wallpaper`).

- **Complète :** [ADR 0202](0202-desktop-wallpaper-images.md) §6 (entrée
  laissée hors du chantier des fonds d'écran),
  [ADR 0151](0151-app-registered-file-menu-entries.md) et
  [ADR 0159](0159-app-menu-entries-implementation-adjustments.md) (entrées de
  menu des apps).
- **Ne change pas :** les clés d'`appearance.config` de l'ADR 0202 §1, ni le
  panneau Paramètres ▸ Apparence ▸ Fond d'écran.

## Contexte

- Les fonds d'écran en image (ADR 0202) se choisissent seulement dans
  Paramètres. Sous Windows, macOS ou GNOME, le menu d'une image propose
  « Définir comme fond d'écran ».
- Les entrées de menu des apps sont déclarées **au build** (ADR 0151 §2,
  ADR 0159 §1) : le menu ne sait pas combien d'écrans sont ouverts, alors que
  l'issue demande un sous-menu par écran avec le bureau étendu (ADR 0178).
- La charge utile d'une entrée est `{"paths":[…],"scope":"File"}` : une app
  qui a plusieurs entrées sur la même intention ne sait pas laquelle a été
  choisie.

## Décision

### 1. Une petite app service déclarée par Paramètres

Le projet `HackerOs.Apps.Settings` déclare une deuxième app,
`org.hackeros.settings.wallpaper` (« Wallpaper »), de type `Service`, cachée
du lanceur (`LaunchVisibility.Hidden`), exécutée sur le fil principal (une
tâche d'un instant par clic : un worker démarrerait un runtime .NET à chaque
fois). Elle déclare l'intention `org.hackeros.settings.set-wallpaper.v1` et les
entrées de menu. À chaque clic, le menu la lance ; elle :

1. lit le chemin de la charge utile et le nom de l'entrée choisie (§3) ;
2. lit le fichier **en tant qu'utilisateur de la session** et vérifie, comme
   le shell (ADR 0202 §3), qu'il fait au plus 16 Mo et que ses premiers
   octets sont ceux d'un PNG, d'un JPEG ou d'un WebP ;
3. écrit la clé dans la **couche utilisateur** d'`appearance.config`
   (`wallpaper` pour « l'écran 1 et les autres », `wallpaperScreen<n>` pour
   l'écran n) ; le shell suit le fichier et change le fond tout de suite ;
4. publie une notification « Fond d'écran changé » (ou l'erreur : clé
   verrouillée par l'administrateur, fichier illisible, pas une image), puis
   s'arrête.

Aucune fenêtre ne s'ouvre (Q1). Le chemin est gardé tel quel, comme
« Parcourir… » (ADR 0202 Q2).

### 2. Filtre par écran des entrées de menu

Deux champs facultatifs s'ajoutent aux entrées de `contextMenus` :

| Champ | Valeurs | Sens |
| --- | --- | --- |
| `screens` | `single`, `multiple` | L'entrée n'apparaît qu'avec un seul écran ouvert, ou qu'avec deux ou plus. Absent : toujours. |
| `screen` | 2 à 9 | L'entrée n'apparaît que si l'écran n est ouvert. |

Le SDK les pose après `Register` :
`RegisterFileContextMenuContext.RestrictToScreens(name, MenuScreens.Single | Multiple)`
et `RestrictToScreen(name, n)`. Le validateur du manifeste refuse une autre
valeur, un numéro hors de 2 à 9 et une entrée qui a les deux champs. L'OS
compte l'écran 1 et les écrans secondaires ouverts (`ScreenService`). Un
parent dont tous les enfants sont filtrés disparaît (ADR 0150 §3), comme
pour les autres filtres. Le réglage Paramètres ▸ Menus de fichiers liste ces
entrées comme les autres.

### 3. Le nom de l'entrée dans la charge utile

La charge utile devient `{"paths":[…],"scope":"File","entry":"<nom>"}`.
`HackFileMenuIntent.ReadEntry(payload)` le lit (`null` quand il manque). Les
apps existantes ignorent ce champ ; une app peut ainsi brancher plusieurs
entrées sur une seule intention.

### 4. Les entrées de Paramètres

Pour `.png`, `.jpg`, `.jpeg`, `.webp`, `image/png`, `image/jpeg`,
`image/webp`, portée `File`, une seule image (`selection: single`), placées
juste après « Ouvrir avec » :

- `set-wallpaper` « Définir comme fond d'écran » (`screens: single`) ;
- `set-wallpaper-on` « Définir comme fond d'écran » (`screens: multiple`),
  sous-menu :
  - `set-wallpaper-all` « Écran 1 et les autres » (clé `wallpaper`) ;
  - `set-wallpaper-screen-2` à `set-wallpaper-screen-9` « Écran n »
    (`screen: n`, clé `wallpaperScreen<n>`).

Elles apparaissent partout où l'OS dessine le menu des fichiers : explorateur,
bureau, dialogues de fichiers (ADR 0159 §4).

## Conséquences

- Le SDK gagne `MenuScreens`, `RestrictToScreens`, `RestrictToScreen` et
  `HackFileMenuIntent.ReadEntry` (dans `PublicAPI.Unshipped.txt`, prochaine
  mineure) ; `AppContextMenuEntryManifest` gagne `Screens` et `Screen`.
- Le projet Paramètres déclare deux apps : ses clés de
  `Resources/AppManifest*.resx` sont préfixées par l'identifiant de l'app
  (ADR 0159 §3).
- Les clés du mobile (`mobileWallpaper`, [ADR 0217](0217-mobile-home-wallpaper.md)) ne sont pas écrites : le SDK ne dit pas à une app quelle interface est active. L'image du mobile se choisit dans Paramètres.
- Une app installée qui pose `screens` ou `screen` sur un OS plus ancien voit
  ses entrées partout (champ ignoré) : sans danger, l'entrée marche toujours.

## Solutions écartées

- **Ouvrir Paramètres** à chaque clic (Q1 B) : interrompt l'utilisateur pour
  une action d'un clic.
- **Neuf intentions**, une par écran : encombre le manifeste ; le nom de
  l'entrée dans la charge utile sert à toutes les apps.
- **Un sous-menu fixe de neuf écrans** (Q2 C) : menu encombré avec un seul
  écran.

## Questions posées à Benoit

Posées le 2026-10-31 dans le fil « Définir une image comme fond d'écran » :

- **Q1** Que fait le clic : A (recommandé) applique sans fenêtre et confirme
  par une notification ; B ouvre Paramètres ▸ Apparence ▸ Fond d'écran avec
  l'image appliquée.
- **Q2** Choix de l'écran : A (recommandé) filtre par nombre d'écrans dans le
  modèle des entrées, sous-menu seulement avec plusieurs écrans ; B entrée
  simple seulement (écran 1 et les autres) ; C sous-menu fixe de neuf écrans.

**Réponse (2026-10-31)** : Benoit choisit les deux recommandations (Q1 A,
Q2 A). C'est ce que décrit la décision ci-dessus ; rien ne change.

## Tâches

- [x] Champs `screens` et `screen`, validateur, générateur (`RestrictToScreens`, `RestrictToScreen`).
- [x] Filtre par écran dans `FileMenuEntries` (nombre et numéros des écrans ouverts).
- [x] `entry` dans la charge utile, `HackFileMenuIntent.ReadEntry`.
- [x] App service `org.hackeros.settings.wallpaper` et ses entrées.
- [x] Tests : validateur, contexte d'enregistrement, filtre, charge utile, service.
- [x] Docs fr/en (`wallpapers.md`, `context-menu.md`), `features.md`, entrée de changement.
