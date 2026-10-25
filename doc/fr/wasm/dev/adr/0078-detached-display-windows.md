> [English version](../../../../en/wasm/dev/adr/0078-detached-display-windows.md)

# ADR 0078 : fenêtres d'affichage détachées (préparation du deuxième écran)

## Statut

Accepté le 2026-10-16 (proposé le même jour, puis validé par Benoit avec le
questionnaire de la section 4 du plan).

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Dépend de :** ADR 0075 (iframe isolée).

## Contexte

Benoit veut ouvrir un site dans une nouvelle fenêtre, hors de l'OS : une fenêtre
avec l'OS, l'autre avec le même OS mais qui n'affiche que la page. Il veut plus
tard un deuxième écran dans une autre fenêtre ; ce chantier doit le préparer.

Démarrer une seconde instance de HackerOS dans la nouvelle fenêtre donnerait un
second OS : ses processus, sa mémoire et son état seraient séparés, et
`HackerOs.Server` n'accepte qu'un circuit actif.

## Décision

### 1. Une seule instance, plusieurs surfaces d'affichage

La fenêtre détachée est ouverte par l'instance principale
(`window.open` sur une page vide de même origine) et **pilotée par elle**. Elle
ne démarre pas de runtime .NET. L'OS reste unique : processus, fichiers et
réseau vivent dans l'instance principale.

### 2. Le lien d'affichage

`IDisplaySurfaceService` (Platform) gère les surfaces : la principale (le
bureau) et les surfaces détachées. Chaque surface a un identifiant, une taille,
un titre, un état (ouverte, fermée par l'utilisateur, perdue). L'ouverture doit
venir d'un geste de l'utilisateur (règle des bloqueurs de fenêtres
surgissantes). Le module JS du service crée le document de la surface, y place
le contenu et relaie les événements de la surface (fermeture, redimensionnement,
focus). Fermer l'OS ou se déconnecter ferme les surfaces.

### 3. Pages web dans une surface détachée

« Ouvrir dans une fenêtre détachée » (menu de l'onglet, et une nouvelle façade
`HackBrowser.OpenDetached(url)` du SDK) déplace l'onglet dans une surface : l'iframe isolée de l'ADR 0075 y
est créée, et son pont parle toujours au navigateur de l'instance principale.
La barre de titre de la surface montre l'adresse ; « Rattacher » remet l'onglet
dans le navigateur.

### 4. Ce qui reste à prouver

Dessiner un composant Blazor (site Blazor interactif de l'ADR 0073 §4, ou plus
tard un bureau sur un deuxième écran) dans une surface détachée demande que le
moteur de rendu de Blazor écrive dans le DOM d'un autre document et y reçoive
les événements. Le chantier `web/10-detached-window` fait cette preuve ; s'il
réussit, les sites Blazor s'ouvrent aussi en fenêtre détachée, sinon ils restent
dans le navigateur de l'OS et un ADR décrit l'autre voie avant le deuxième
écran.

## Conséquences

- Le deuxième écran sera une surface de plus pour le gestionnaire de fenêtres.
- Aucune seconde instance, aucun second circuit : compatible avec les trois
  hôtes.
- Le navigateur hôte peut bloquer `window.open` ; l'OS affiche alors une
  notification qui explique comment l'autoriser.
