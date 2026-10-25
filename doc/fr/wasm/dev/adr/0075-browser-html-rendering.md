> [English version](../../../../en/wasm/dev/adr/0075-browser-html-rendering.md)

# ADR 0075 : rendu HTML du navigateur dans une iframe isolée

## Statut

Accepté le 2026-10-16 (proposé le même jour, puis validé par Benoit avec le
questionnaire de la section 4 du plan).

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Dépend de :** ADR 0074 §1 (corps HTTP réels).

## Contexte

Le navigateur HackerOS ne dessine que des `SimulatedPage` structurés. Pour
servir du HTML, du CSS et du JavaScript, il doit afficher une vraie page web.
Benoit a choisi l'isolation : le JS d'un site ne doit atteindre ni HackerOS ni
Internet.

## Décision

### 1. Une iframe sans même origine

La page est écrite dans une `iframe sandbox="allow-scripts allow-forms"` par
`srcdoc`, **sans** `allow-same-origin` : son origine est opaque, elle ne peut
lire ni le DOM de HackerOS, ni son IndexedDB, ni ses cookies. Le module JS de la
vue de page (`BrowserPageView.razor.js` dans l'app Browser, ADR 0007) est le seul
code qui lui parle, par `postMessage`.

### 2. Le navigateur charge les sous-ressources lui-même

Comme un vrai navigateur, l'app Browser :

1. reçoit le HTML par `HackHttpClient` (ou `HackFile` pour `file://`) ;
2. l'analyse en C# (bibliothèque AngleSharp, compatible WebAssembly) ;
3. charge par le réseau simulé chaque `<link rel=stylesheet>`, `<script src>`,
   `<img>`, `<source>`, police et `url()` des feuilles de style, en résolvant les
   URL relatives contre l'adresse de la page ;
4. les remplace par des URL `blob:`/`data:` et écrit le document final dans
   l'iframe.

Les outils de développement du navigateur existent dès la v1, avec deux
onglets : **Réseau** (chaque requête avec méthode, statut, type, taille et
durée) et **Console** (sortie de `console.*` et erreurs JS de la page,
relayées par le pont).

### 3. Le pont de la page

Un petit script injecté en tête du document remplace :

- `fetch` et `XMLHttpRequest` : envoyés au navigateur, qui les passe par
  `HackHttpClient` (toutes les méthodes, en-têtes, corps), puis rendus à la page ;
- les clics de liens et l'envoi de formulaires (`GET` et `POST`) : navigation de
  l'onglet ;
- `document.cookie` : lu depuis le `HackCookieContainer` de l'onglet, écrit par
  message ;
- `localStorage`/`sessionStorage` (interdits dans une origine opaque) : stockage
  par site, gardé par le navigateur dans
  `~/.local/share/hackeros/browser/storage/<hôte>/` ;
- les ressources ajoutées par le JS après le chargement (`img.src`, nouveaux
  `<link>`) : un `MutationObserver` les fait charger par le pont.

`WebSocket`, `EventSource`, `window.open` et la géolocalisation sont refusés pour
l'instant (erreur dans la console de la page).

### 4. `file://`

`file:///home/user/site/index.html` est lu par `HackFile` avec les droits de
l'utilisateur (le navigateur est une app système). Les ressources relatives sont
lues dans le même dossier. La page n'a pas de pont réseau vers `localhost` sauf
par ses URL absolues.

### 5. Types de contenu

| `Content-Type` | Rendu |
|---|---|
| `text/html` | iframe (§1 à §3) |
| `SimulatedPage` (sites existants) | `SimulatedPageRenderer` (inchangé) |
| `image/*`, `text/plain`, `application/json`, `application/pdf` | visionneuse intégrée |
| autre | téléchargement vers `~/Downloads` |

## Conséquences

- Nouvelle dépendance AngleSharp dans l'app Browser seulement.
- Le code JS reste minimal et appartient à l'app Browser ; rien n'est ajouté aux
  hôtes. Dans `HackerOs.Server` (rendu Interactive Server), le pont fonctionne
  de la même façon : le JS tourne dans le navigateur, le C# sur le serveur.
- Les liens et formulaires des pages de `SimulatedPage` ne changent pas.
