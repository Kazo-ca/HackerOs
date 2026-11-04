> [English version](../../../en/wasm/dev/cloudflare-redirect.md)

# Redirection Cloudflare de hackos.kazo.ca vers os.kazo.ca

Le script du Worker est dans [`wasm2/HackerOs/build/cloudflare/hackos-redirect-worker.mjs`](../../../../wasm2/HackerOs/build/cloudflare/hackos-redirect-worker.mjs). Vérification locale : `node wasm2/HackerOs/build/cloudflare/test-redirect-worker.mjs`. Contexte : [ADR 0228](adr/0228-rename-to-kazoos.md) (Kazo OS).

## Ce que fait le Worker

- `https://hackos.kazo.ca/service-worker.js` (avec ou sans `?mode=installed-apps`) répond
  par un petit script qui remplace le service worker en cache de la PWA : il vide le cache
  des fichiers de l'OS, se désinscrit et envoie chaque onglet ouvert vers la même adresse
  sur `os.kazo.ca`. Un navigateur refuse une redirection sur le fichier du service worker,
  c'est pourquoi une simple « Redirect Rule » laisserait les PWA installées bloquées sur
  leur copie en cache.
- Toute autre adresse reçoit une redirection permanente (301, ou 308 pour un POST) vers
  `https://os.kazo.ca`, en gardant le chemin et la requête :
  `hackos.kazo.ca/legacy/?a=1` → `os.kazo.ca/legacy/?a=1`.
- IndexedDB et localStorage de `hackos.kazo.ca` ne sont pas touchés.

Testé localement avec Node : `/` → 301 `https://os.kazo.ca/`, `/legacy/?a=1` → 301
`https://os.kazo.ca/legacy/?a=1`, `/service-worker.js?mode=installed-apps` → 200
`text/javascript`, `POST /api/x` → 308.

## Étape 0 : os.kazo.ca doit déjà servir l'OS

GitHub Pages ne sert qu'un seul domaine personnalisé par dépôt. Avant d'activer la
redirection :

1. GitHub, dépôt Kazo-ca/HackerOs → **Settings → Pages → Custom domain** : remplacer
   `hackos.kazo.ca` par `os.kazo.ca`, **Save**, puis cocher **Enforce HTTPS** quand
   GitHub l'offre.
2. Dans Cloudflare, l'enregistrement `os` doit être un `CNAME` vers `kazo-ca.github.io`.
   Laisse-le en **DNS only** (nuage gris) le temps que GitHub émette le certificat.
3. Ouvre `https://os.kazo.ca/` : l'OS doit démarrer. Sinon, n'active pas l'étape 2 :
   une 301 est gardée en cache par les navigateurs.

## Étape 1 : créer le Worker

1. Tableau de bord Cloudflare → **Workers & Pages** (menu de gauche, sous Compute) →
   **Create** → **Create Worker** (modèle « Hello World »).
2. Nom : `hackos-redirect` → **Deploy**.
3. **Edit code** : remplace tout le contenu par celui de `hackos-redirect-worker.mjs` →
   **Deploy**.

## Étape 2 : brancher le Worker sur hackos.kazo.ca

1. Dans le Worker `hackos-redirect` → **Settings** → **Domains & Routes** → **Add** →
   **Custom domain** → `hackos.kazo.ca` → **Add domain**.
2. Si Cloudflare refuse parce qu'un enregistrement DNS `hackos` existe déjà : va dans
   **kazo.ca → DNS → Records**, supprime l'enregistrement `hackos` (le CNAME vers GitHub),
   puis refais le point 1. Cloudflare crée lui-même l'enregistrement et le certificat.

(Variante si tu préfères garder l'enregistrement DNS : mets `hackos` en **Proxied**
(nuage orange), puis dans le Worker **Add → Route** : `hackos.kazo.ca/*`, zone `kazo.ca`.)

## Étape 3 : vérifier

- `https://hackos.kazo.ca/` et `https://hackos.kazo.ca/legacy/` arrivent sur `os.kazo.ca`.
- `https://hackos.kazo.ca/service-worker.js` affiche le petit script (pas de redirection).
- Une PWA installée depuis hackos.kazo.ca : à la prochaine ouverture elle charge encore
  sa copie en cache, voit le nouveau service worker, puis part vers `os.kazo.ca`. Il faut
  réinstaller la PWA depuis `os.kazo.ca`.

## Coût

Gratuit : le plan Workers Free donne 100 000 requêtes par jour.
