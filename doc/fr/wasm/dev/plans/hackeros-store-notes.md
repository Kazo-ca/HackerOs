> [English version](../../../../en/wasm/dev/plans/hackeros-store-notes.md)

# Notes : apps tierces, installation depuis GitHub et HackerOs Store

Notes de Benoit du 2026-10-28, à reprendre quand les premières apps tierces
seront disponibles. Ce n'est pas un plan validé : aucune décision ni aucun code.

## Dépôts

- **Apps séparées** : chaque app tierce vit dans son propre dépôt
  `Kazo-ca/HackerOs-App-<Nom>`, hors de ce dépôt.
- **Template** : `Kazo-ca/HackerOs-AppTemplate`, point de départ d'un dépôt
  d'app ([ADR 0176](../adr/0176-app-repository-template.md)).
- **Liste des apps installables** : `Kazo-ca/HackerOs-AppMarketPlace` contient
  la liste des dépôts d'apps qui peuvent être installées dans HackerOS.

## Premier jalon : une commande du terminal

Installer une app à partir de son dépôt GitHub, par une commande du terminal.
L'OS doit donc admettre GitHub comme hôte de confiance pour télécharger les
paquets d'apps. Points à trancher avant le code (évaluation du 2026-10-28) :

- l'installation au runtime n'existe pas encore : aujourd'hui une app entre
  dans l'OS au build (`ProjectReference` ou `PackageReference` de l'hôte) ;
- le framework est élagué à la publication (ADR 0106) : une app chargée au
  runtime ne doit appeler que des membres gardés ;
- les fichiers des Releases GitHub n'envoient pas d'en-têtes CORS, alors que
  `api.github.com`, `raw.githubusercontent.com` et GitHub Pages en envoient ;
- le JavaScript d'une app tourne dans la même origine que l'OS.

## Plus tard : l'app HackerOs Store

Une app système `HackerOs Store`, comme le Play Store ou le Microsoft Store,
lira la liste de `Kazo-ca/HackerOs-AppMarketPlace`, affichera les apps
disponibles et les installera par le même service que la commande du terminal.
Le futur magasin d'apps est déjà prévu par le format de `manifest.json`
([`app-model.md`](../../framework/app-model.md)) ; une app qu'il installe n'est
jamais une app système.
