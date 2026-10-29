> [English version](../../../../en/wasm/dev/adr/0185-file-explorer-improvements.md)

# ADR 0185 : améliorations de l'explorateur de fichiers

## Statut

Accepté le 2026-10-29. Décisions prises avec Benoit dans le fil « Améliorer l'explorateur de
fichiers » (questions Q1 à Q18 posées le 2026-10-28, réponses le 2026-10-29). Pas encore mis en
œuvre : le travail est noté dans les choses à faire, pour plus tard.

## Contexte

Benoit a demandé neuf améliorations de l'app File Explorer :

1. redimensionner le panneau de gauche (Places et Network Drives) ;
2. afficher « This directory is empty » dans la liste, pas au-dessus ;
3. télécharger un fichier en le glissant hors du navigateur ;
4. un fichier texte déposé dans l'explorateur est importé, mais le navigateur l'ouvre aussi ;
5. ouvrir une archive (zip) comme un dossier en lecture seule ;
6. plus d'options dans le menu contextuel (Compresser, Envoyer vers…) ;
7. une icône de dossier qui montre un aperçu de son contenu en mode Icônes ;
8. un lecteur réseau qui se met à jour seul quand un fichier y est ajouté ;
9. une bulle d'information au survol d'un fichier ou d'un dossier.

État du code au moment de la décision (main `fd52528c`) :

- La fenêtre (`FileExplorerWindow.razor`) affiche le message de dossier vide dans une `div`
  au-dessus du contrôle de fichiers partagé (`HackFileView`, contrat `hackeros.file-view`,
  ADR 0148 et 0155). Les dialogues Ouvrir et Enregistrer (ADR 0157) ne l'ont pas.
- La largeur du panneau `explorer-sidebar` est fixée par le CSS.
- `FileView.razor.js` lit les dépôts dans ses zones de dépôt, mais un dépôt qui tombe hors
  d'une zone n'est pas annulé : le navigateur ouvre alors le fichier.
- Compresser, Extraire et Télécharger existent seulement dans la barre d'outils. Le manifeste
  ajoute « Extraire ici » au menu d'un `.zip` (ADR 0159). Aucune app ne déclare ouvrir les
  `.zip`.
- Le contrôle surveille le dossier affiché (`IAppFileSystemWatchGateway`) : un changement fait
  dans HackerOS met la liste à jour. Le fournisseur `hostfs` des lecteurs réseau
  (ADR 0181 §2) ne voit pas les changements faits hors de HackerOS.
- Le SDK a un dialogue de saisie de texte (`IDialogService.TextInputAsync`,
  `TextInputDialogRequest` : titre, texte, valeur par défaut, indication).
- Un fichier donne sa taille (`FileMetadata.Length`). Un dossier ne donne pas la taille totale
  de son contenu ; la commande `du` la calcule elle-même.

## Décision

1. **Panneau de gauche redimensionnable.** Une poignée sur le bord droit du panneau, à glisser,
   avec une largeur minimale et maximale (environ 140 à 400 px). La largeur est retenue par
   utilisateur dans l'état de l'explorateur. Un double-clic sur la poignée remet la largeur par
   défaut. Le panneau ne se replie pas.

2. **Dossier vide dans la liste.** Le message passe dans le contrôle de fichiers partagé : en
   mode Détails, une ligne sous les en-têtes de colonnes ; en mode Icônes, centré dans la zone.
   La zone reste une cible de dépôt et garde son menu contextuel. Une recherche sans résultat
   affiche « Aucun résultat pour "x" ». Les dialogues Ouvrir et Enregistrer en profitent aussi.

3. **Dépôt ouvert par le navigateur (bug).** Corrigé pour tout l'OS : un dépôt qui tombe ailleurs
   que sur une zone de dépôt prévue ne fait rien, dans aucune fenêtre (la page annule le
   comportement par défaut de `dragover` et `drop`).

4. **Glisser hors du navigateur pour télécharger.** Chrome et Edge seulement (`DownloadURL`,
   un seul fichier par glisser). Un fichier seul est téléchargé tel quel. Plusieurs éléments ou
   un dossier sont téléchargés en un `.zip`. Sur Firefox et Safari, le bouton Télécharger reste
   la seule façon de faire.

5. **Limite de taille du glisser externe.** Le contenu doit être prêt quand le glisser commence :
   il est préparé dès l'appui sur l'élément. La limite est d'environ 50 Mo et se décide sur la
   **taille donnée par l'OS**, sans lire le contenu : `FileMetadata.Length` pour un fichier. Pour
   un dossier ou une sélection multiple, l'OS ne donne pas encore la taille totale : il faut
   l'ajouter (une taille totale récursive dans le service de fichiers et le SDK, que la commande
   `du` et le dialogue Propriétés peuvent aussi utiliser). Au-delà de la limite, le glisser hors
   du navigateur est désactivé et une bulle propose « Utilisez Télécharger ». Le glisser vers un
   dossier de HackerOS reste possible.

6. **Ouvrir une archive.** File Explorer se déclare comme app pour les `.zip`
   (`fileHandlers` du manifeste). Le double-clic ouvre l'archive comme un dossier en lecture
   seule, avec un fil d'Ariane comme Documents › photos.zip › sous-dossier. « Extraire ici »
   reste dans le menu.

7. **L'archive est parcourue par l'explorateur seulement.** Pas de montage dans le système de
   fichiers : l'explorateur lit le `.zip` et présente ses entrées lui-même. Les autres apps et le
   terminal ne voient pas l'intérieur de l'archive. Ouvrir un fichier de l'archive l'extrait
   d'abord dans un dossier temporaire (`/tmp`), puis l'ouvre avec son app. Une modification de
   cette copie ne revient pas dans l'archive.

8. **Formats.** `.zip` seulement pour commencer (`System.IO.Compression`). `.tar`, `.tar.gz`,
   `.7z` et `.rar` sont hors du périmètre.

9. **Archive en lecture seule stricte.** Glisser un élément hors de l'archive l'extrait vers la
   destination. Glisser un élément dedans, renommer ou supprimer est refusé avec un message clair.

10. **Nouvelles options du menu contextuel** d'un ou plusieurs fichiers ou dossiers :
    Compresser en .zip, Télécharger, Copier le chemin et un sous-menu Envoyer vers ▸.
    Compresser ouvre un **dialogue de saisie de HackerOS** pour choisir le nom de l'archive,
    prérempli avec le nom de l'élément s'il est seul, sinon « Archive.zip ». Le dialogue de
    saisie existe déjà (`TextInputAsync`) ; s'il lui manque ce qu'il faut (par exemple la
    validation d'un nom de fichier ou la sélection du nom sans l'extension), on l'ajoute au
    dialogue de l'OS plutôt que d'en faire un dans l'explorateur.

11. **Destinations d'« Envoyer vers ».** Bureau, Dossier compressé (.zip), Mon ordinateur
    (téléchargement), puis chaque lecteur réseau monté (une copie y est faite).

12. **« Envoyer vers ▸ Bureau » crée un raccourci** vers le fichier ou le dossier, comme
    Windows. Les fichiers `.desktop` pointent aujourd'hui seulement vers des apps (ADR 0160) : il
    faut ajouter un raccourci vers un fichier, que le bureau sait ouvrir.

13. **Pas de destinations ajoutées par d'autres apps pour l'instant.** La liste d'« Envoyer
    vers » est fixée par l'OS. Une déclaration dans le manifeste viendra quand une app (par
    exemple une app du Hack Store) en aura besoin.

14. **Aperçu du contenu sur l'icône d'un dossier.** En mode Icônes, l'icône montre un dossier
    ouvert avec 3 ou 4 mini-icônes des premiers éléments, selon leur type. Le contenu n'est lu
    que pour les dossiers visibles à l'écran, puis gardé en cache, et l'aperçu se met à jour avec
    la surveillance du dossier. Les modes Détails et arbre ne changent pas.

15. **Rafraîchissement automatique d'un lecteur réseau.** Quand le navigateur l'a,
    l'explorateur utilise l'API d'observation des dossiers (`FileSystemObserver`, Chrome récent).
    Sinon, il relit le dossier affiché toutes les 5 secondes pendant que la fenêtre est visible,
    et le relit aussi quand la fenêtre reprend le focus. Les changements trouvés passent par le
    même chemin que la surveillance actuelle, donc toute app qui surveille le dossier les voit.

16. **Bulle d'information au survol.** Une bulle au style de l'OS apparaît après environ
    600 ms, avec le nom complet, le type, la taille et la date de modification, plus le nombre
    d'éléments pour un dossier. Elle vit dans le contrôle partagé, donc le bureau et les
    dialogues l'ont aussi. Pas de bulle sur mobile, où l'appui long ouvre le menu.

17. **Mobile (ADR 0171).** S'appliquent aussi sur mobile : le message de dossier vide, les
    archives, les nouvelles options du menu, l'aperçu des dossiers et le rafraîchissement des
    lecteurs réseau. Bureau seulement : le panneau redimensionnable, le glisser hors du
    navigateur et les bulles.

18. **Organisation.** Une série `explorer/base`, avec un chantier (une PR vers `explorer/base`)
    par sujet. Les deux bugs (dossier vide, fichier ouvert par le navigateur) passent en premier.
    Comme toute série, la PR finale vers `main` attend l'accord de Benoit.

## Questions posées à Benoit

Réponses du 2026-10-29. « Recommandation » veut dire que Benoit a pris l'option recommandée.

| Q | Sujet | Réponse |
| --- | --- | --- |
| 1 | Panneau redimensionnable | Recommandation : poignée, largeur retenue, double-clic = défaut |
| 2 | Message de dossier vide | Recommandation : dans le contrôle partagé, dialogues compris |
| 3 | Dépôt ouvert par le navigateur | Recommandation : corrigé pour tout l'OS |
| 4 | Glisser hors du navigateur | Recommandation : fichier seul tel quel, sinon un `.zip` |
| 5 | Limite de taille | Recommandation (50 Mo), en se fiant à la taille donnée par l'OS ; l'ajouter là où elle manque |
| 6 | Double-clic sur une archive | Recommandation : ouverte comme dossier en lecture seule |
| 7 | Montage ou explorateur seul | **B** : l'explorateur seul parcourt l'archive, un fichier ouvert est extrait dans `/tmp` |
| 8 | Formats | Recommandation : `.zip` seulement |
| 9 | Écrire dans l'archive | Recommandation : lecture seule stricte |
| 10 | Options du menu | Recommandation, et Compresser demande le nom par un dialogue de saisie de HackerOS (à créer ou compléter s'il manque quelque chose) |
| 11 | Destinations d'Envoyer vers | Recommandation : Bureau, Dossier compressé, Mon ordinateur, lecteurs réseau |
| 12 | Envoyer vers Bureau | Recommandation : raccourci |
| 13 | Destinations des autres apps | Recommandation : pas pour l'instant |
| 14 | Aperçu des dossiers | Recommandation : mini-icônes en mode Icônes |
| 15 | Rafraîchir un lecteur réseau | Recommandation : `FileSystemObserver`, sinon relecture toutes les 5 s et au focus |
| 16 | Bulle au survol | Recommandation : bulle de l'OS dans le contrôle partagé |
| 17 | Mobile | Recommandation : la liste du point 17 |
| 18 | Organisation | Recommandation : série `explorer/base`, bugs d'abord |

## Conséquences

- Le contrôle de fichiers partagé change (message de dossier vide, bulles, aperçu des
  dossiers) : les dialogues de fichiers et le bureau en profitent sans travail de leur côté.
- Le service de fichiers et le SDK gagnent une taille totale récursive (point 5), ajoutée dans
  les `PublicAPI.Unshipped.txt` pour la prochaine version du SDK.
- Les fichiers `.desktop` gagnent un raccourci vers un fichier (point 12), à décrire en
  complément de l'ADR 0160.
- Le dialogue de saisie de l'OS peut gagner une validation de nom de fichier (point 10).
- L'archive n'est pas visible hors de l'explorateur (point 7) : un fichier ouvert depuis
  l'archive est une copie temporaire.
- Seuls Chrome et Edge permettent le glisser hors du navigateur, et seul un Chrome récent a
  `FileSystemObserver` ; ailleurs, la relecture périodique coûte une lecture du dossier toutes
  les 5 secondes tant que la fenêtre est visible.
- Mise en œuvre : à faire plus tard, dans la série `explorer/base`.
