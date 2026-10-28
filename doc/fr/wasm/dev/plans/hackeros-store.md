> [English version](../../../../en/wasm/dev/plans/hackeros-store.md)

# Note : l'app HackerOs Store

Note du 2026-10-28, à reprendre **quand des apps seront publiées** dans leurs
dépôts. Pas de chantier ouvert. C'est le « plan à part » annoncé pour le magasin
d'apps par [`ecosystem-rebuild-plan.md`](ecosystem-rebuild-plan.md) (section 9)
et [`wasm-v3-completion-plan.md`](wasm-v3-completion-plan.md) (question 1).

## Idée

Une app système **HackerOs Store**, façon Play Store ou Microsoft Store, liste
les apps qu'on peut installer dans HackerOS, montre leur fiche et les installe,
les met à jour ou les retire en un clic.

## Ce qui existe déjà ou est proposé

- Le dépôt [`Kazo-ca/HackerOs-AppMarketPlace`](https://github.com/Kazo-ca/HackerOs-AppMarketPlace)
  est créé (vide le 2026-10-28) : il portera la liste des dépôts d'apps.
- Le dépôt [`Kazo-ca/HackerOs-AppTemplate`](https://github.com/Kazo-ca/HackerOs-AppTemplate)
  est créé (vide le 2026-10-28) : point de départ d'une app,
  [ADR 0176](../adr/0176-app-repository-template.md).
- L'installation depuis un dépôt GitHub au terminal (`hackpkg`), ses paquets
  `.hackapp` et ses sources de confiance : [ADR 0182](../adr/0182-install-apps-from-github.md).
  Le Store passe par le même service d'installation ; il n'ajoute que
  l'interface et la liste.

## À décider le moment venu

- Format de la liste dans `HackerOs-AppMarketPlace` (par exemple un `apps.json` :
  dépôt, catégorie, description courte, captures), lu par
  `raw.githubusercontent.com` comme les paquets.
- Qui ajoute une app à la liste (pull request revue par Benoit ?) et si une app
  listée devient une source de confiance.
- Signature des paquets, installation transactionnelle et migration des données
  d'une version à l'autre (question 1 de `wasm-v3-completion-plan.md`).
- Fiche d'une app : icône et captures venant du paquet ou du dépôt.
- Mises à jour : vérification au démarrage, à côté de l'annonce de nouvelle
  version de l'OS ([ADR 0174](../adr/0174-announce-a-new-version-to-open-sessions.md)).
