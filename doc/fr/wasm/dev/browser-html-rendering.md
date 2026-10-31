> [English version](../../../en/wasm/dev/browser-html-rendering.md)

# Rendu HTML du navigateur

**Phase web, chantier `web/4-browser-html`** ([`asp-hack-plan.md`](plans/asp-hack-plan.md),
section 5). Décision : [ADR 0075](adr/0075-browser-html-rendering.md), ajustée
par l'[ADR 0079](adr/0079-browser-html-rendering-adjustments.md). S'appuie sur
les corps HTTP réels de [`http-bodies.md`](../framework/http-bodies.md).

## Objet

Avant ce chantier, le navigateur ne dessinait que des `SimulatedPage`
structurés et affichait le HTML brut comme du texte. Désormais, une réponse
`text/html` (d'un site du réseau simulé ou d'un fichier local en `file://`)
devient une **vraie page web** : son CSS s'applique, son JavaScript s'exécute,
ses `fetch`, ses `XMLHttpRequest`, ses formulaires, ses cookies et son
`localStorage` fonctionnent. La page reste prisonnière : elle n'atteint ni
HackerOS, ni Internet, ni les fichiers de l'utilisateur. Tout ce qu'elle
demande passe par le navigateur, qui décide en C#, comme un vrai navigateur
applique la politique de même origine et CORS.

Le navigateur gagne aussi :

- les URL `file://` (fichier ou listing « Index of » d'un dossier) ;
- la vue source (`view-source:` ou `Ctrl+U`) ;
- des outils de développement avec un onglet **Network** et un onglet
  **Console** (`F12`) ;
- des pages d'erreur à la Chromium (`ERR_NAME_NOT_RESOLVED`,
  `ERR_CONNECTION_REFUSED`, `ERR_FILE_NOT_FOUND`, erreurs HTTP) ;
- les téléchargements vers `~/Downloads` et l'affichage PDF.

## Architecture

```
BrowserWindow ── NavigateAsync ──► BrowserResourceLoader ──► SdkBrowserTransport
     │                                  │  (journal Network/Console)   ├─ http(s) : HackHttpClient + cookies de l'onglet
     │                                  ▼                              └─ file:   : HackFile / HackDirectoryInfo
     │                            DevToolsLog ◄── DevToolsPanel
     ▼
HtmlPageComposer (AngleSharp) ── SubresourceFetcher + CssResourceRewriter
     │   CSS, scripts, images, polices… inlinés en URL data:, en-tête CSP, <base>, script du pont
     ▼
BrowserPageView (.razor.js) ── iframe sandbox="allow-scripts allow-forms", srcdoc
     ▲            │ postMessage (jeton)
     │            ▼
PageBridgeHandler ◄── PageBridge.js (dans la page) : fetch, XHR, liens, formulaires,
     │                 cookies, localStorage, console, history, titre, raccourcis
     └── PageStorage (~/.local/share/hackeros/browser/storage/<site>/local-storage.json)
```

Tous les fichiers sont sous `Apps/System/HackerOs.Apps.Browser/` :

| Dossier / fichier | Rôle |
|---|---|
| `Net/BrowserTransport.cs` | `BrowserRequest`, `BrowserResponse`, `BrowserFetchResult`, `IBrowserTransport`, codes `BrowserErrorCodes`. |
| `Net/SdkBrowserTransport.cs` | Transport réel : `HackHttpClient` (cookies de l'onglet ou conteneur vide selon les credentials, en-têtes interdits filtrés) et `file:` (fichier ou listing). |
| `Net/BrowserUrl.cs` | Barre d'adresse (`view-source:`, `about:blank`, chemins `/…` et `~/…`, recherche HackerSearch), résolution relative, origine, clé de stockage par site. |
| `Net/DirectoryListingPage.cs` | Page « Index of » d'un dossier local. |
| `Net/BrowserResourceLoader.cs` | Point de passage unique : chaque requête produit une ligne Network et, en échec, un message Console « Failed to load resource ». |
| `Rendering/HtmlPageComposer.cs` | Analyse le HTML (AngleSharp), inline les sous-ressources, retire `<base>` et les cadres, ajoute charset, `<base href>`, CSP et le script du pont. |
| `Rendering/SubresourceFetcher.cs` | Charge et met en cache les sous-ressources (300 requêtes et 32 Mio au plus par page) ; refuse `file:` depuis une page web. |
| `Rendering/CssResourceRewriter.cs` | Réécrit `url()` et `@import` (profondeur 4) en URL data. |
| `Bridge/PageBridge.js` | Ressource embarquée injectée dans la page : remplace `fetch`, `XMLHttpRequest`, `document.cookie`, `localStorage`/`sessionStorage`, intercepte liens et formulaires, relaie la console et les erreurs. |
| `Bridge/PageBridgeContracts.cs` | Messages du pont (JSON camelCase, `PageBridgeJsonContext`). |
| `Bridge/PageBridgeHandler.cs` | Côté C# du pont : règles `fetch` (credentials, CORS, réponses opaques), cookies du document, ressources chargées après coup. |
| `Storage/PageStorage.cs` | `localStorage` persisté par site (quota 5 Mi), `sessionStorage` en mémoire de l'onglet. |
| `DevTools/*` | Journal borné (500 entrées) et panneau Network/Console. |
| `BrowserPageView.razor(.js)` | Crée l'iframe isolée, relaie les messages, détecte une navigation par script. |
| `BrowserErrorPages.cs`, `BrowserDownloads.cs` | Pages d'erreur et téléchargements. |

`SimulatedFileTypes` (`Shared/HackerOs.Simulation.Abstractions/Network/`)
donne le type MIME d'un fichier local d'après son extension.
`HackCookieContainer.Remove` (SDK) supprime un cookie expiré par la page.

## Fonctionnement

### Navigation

`BrowserWindow.NavigateAsync` annule la navigation précédente, vide les outils
de développement, puis demande le document au chargeur. La réponse est
affichée selon son type :

- `SimulatedPage` : rendu structuré historique (`SimulatedPageRenderer`), ses
  formulaires deviennent des navigations `POST` ;
- statut HTTP d'erreur sans corps affichable : page d'erreur ;
- `Content-Disposition: attachment` ou corps binaire : enregistré dans
  `~/Downloads` (nom unique `nom (n).ext`) ;
- `text/html` : composé puis affiché dans l'iframe ;
- autre type : `ResponseBodyViewer` (texte, JSON, image, PDF).

L'historique distingue `Push`, `Replace` (redirection, `history.replaceState`)
et `Traverse` (précédent/suivant), ce qui corrige l'ancien bug qui tronquait
l'historique « suivant ».

### Vrais sites et barre d'adresse (ADR 0210)

Un hôte inconnu du réseau simulé passe par le proxy du serveur HackerOS quand un
serveur est connecté ([ADR 0210](adr/0210-browser-real-sites-through-the-proxy.md)) :
le Navigateur est une app système avec `network.real.access`. La page suit le
même chemin que les autres (récupération en C#, `srcdoc` isolé), ses
ressources et ses `fetch` aussi, avec les cookies du site.

Au début de la barre d'adresse, `AddressBarIndicators` montre d'où vient la
page (`BrowserResponse.Source`) : `⇄` vrai site par le proxy, `⌂` site de cette
machine, `◎` réseau simulé, rien pour un fichier. Puis le cadenas pour
`https:` ; pour `http:`, le texte « Non sécurisé ». Un refus du relais donne la
page `ERR_BLOCKED_BY_ADMINISTRATOR` (« Votre administrateur a bloqué … ») ou
`ERR_TIMED_OUT` ; la page `ERR_NAME_NOT_RESOLVED` propose de connecter un
serveur.

### La page isolée

L'iframe est en `srcdoc` avec `sandbox="allow-scripts allow-forms"`, sans
`allow-same-origin` : la page a une origine opaque et ne peut lire ni le DOM
de HackerOS ni son stockage. Une meta CSP interdit toute connexion
(`connect-src 'none'`, `img-src data: blob:`…) : la page ne charge rien
elle-même, tout ce dont elle a besoin a été inliné en URL `data:` ou passe par
le pont.

Le pont communique par `postMessage` avec un jeton propre à chaque document.
Une réponse qui arrive après que l'onglet a changé de page est jetée.

### Ce que voit le JavaScript de la page

| API | Comportement |
|---|---|
| `fetch`, `XMLHttpRequest`, `navigator.sendBeacon` | Envoyés au navigateur. Même origine : en-têtes complets. Origine différente : CORS vérifié (`Access-Control-Allow-Origin`, `-Allow-Credentials`, `-Expose-Headers`), `no-cors` donne une réponse opaque. `file:` toujours refusé. |
| `document.cookie` | Cookies de l'onglet pour l'URL de la page (`Max-Age` et `Expires` gérés). Vide sur une page `file:`. |
| `localStorage` | Persisté par site dans `~/.local/share/hackeros/browser/storage/<clé>/local-storage.json` (clés `https_hôte`, `http_hôte_port`, `file`). Quota 5 Mi, `QuotaExceededError` au-delà. |
| `sessionStorage` | En mémoire, le temps de l'onglet. |
| Liens et formulaires | Interceptés : `GET`, `POST` urlencoded, multipart, `text/plain` ; fragment `#` local ; `download` enregistre le fichier. |
| `history.pushState`/`replaceState` | Met à jour la barre d'adresse et l'historique. |
| `document.URL`, `document.referrer` | L'URL réelle de la page, pas `about:srcdoc`. |
| `console.*`, erreurs non gérées | Onglet Console des outils de développement. |
| `alert` | Bannière au-dessus de la page ; `confirm` renvoie `false`, `prompt` `null`. |
| Saisie semi-automatique | À l'envoi d'un formulaire, les champs texte nommés (ni mot de passe, ni caché, ni `autocomplete="off"`, ni numéro de carte) vont dans `~/.local/share/hackeros/browser/form-history.json` ; au focus d'un champ, ses valeurs passées sont proposées par une `<datalist>` ; `Ctrl+Maj+Suppr` efface l'historique (ADR 0088 §6, [`asp-hack-site-data.md`](../framework/asp-hack-site-data.md#saisie-semi-automatique)). |
| `WebSocket`, `EventSource`, `window.open`, géolocalisation | Refusés. |
| Éléments ajoutés après coup (`img.src = …`, `appendChild`) | Chargés par le navigateur à la demande. |

### `file://`

Le navigateur lit les fichiers locaux avec les droits de l'utilisateur, limités
à son dossier personnel (le navigateur n'est pas une app système, ADR 0079). Un
dossier donne une page « Index of ». Une page `file:` peut charger ses fichiers
relatifs et appeler un site en `fetch` (soumis à CORS, origine `null`) ; une
page web ne peut jamais charger un `file:` (« Not allowed to load local
resource »). Le navigateur ouvre les fichiers `.html` et `.htm`
(`FileHandlers`).

### Outils de développement

`F12` (ou le bouton) ouvre le panneau du bas. **Network** liste chaque
requête : nom, méthode, statut, type, taille, durée, en rouge en cas d'échec.
**Console** liste les messages de la page et du navigateur. Le badge du bouton
compte les erreurs.

### Fenêtre détachée

Le bouton `⧉` de la barre d'outils déplace l'onglet dans une fenêtre du
navigateur hôte, hors du bureau, toujours servie par cette fenêtre du
navigateur de l'OS (chantier `web/10-detached-window`, voir
[`detached-display-windows.md`](detached-display-windows.md)). L'iframe de la
page y est recréée et son pont continue de parler à ce navigateur.

## Utilisation

- Taper `example.hackeros/page.html` ou un chemin (`~/Documents/site/index.html`)
  dans la barre d'adresse.
- `Ctrl+U` ou le bouton `</>` : vue source ; `F12` : outils de développement ;
  `F5` : recharger ; `Alt+←`/`Alt+→` : historique. Ces raccourcis marchent
  aussi quand le focus est dans la page.
- Un site qui veut être appelé depuis une autre origine renvoie
  `Access-Control-Allow-Origin` (HackerSearch le fait sur `/api/search`).

## Décisions clés

- Les sous-ressources sont inlinées par le navigateur en C# (AngleSharp) :
  la page ne touche jamais le réseau, et chaque requête passe par le réseau
  simulé, donc dans le journal Network et dans les compteurs de l'app.
- Le pont est un fichier JS embarqué dans l'assembly, pas un script de l'hôte
  (AGENTS.md) ; il est injecté dans le document de la page.
- Une navigation par script (`location.href = …`) ne peut pas être
  interceptée dans une iframe sans même origine : le cadre la détecte (un
  chargement sans annonce du pont) et affiche une page « This page navigated by
  script » plutôt qu'une page sans pont.
- Ajustements de l'ADR 0075 : voir l'[ADR 0079](adr/0079-browser-html-rendering-adjustments.md).

## Limites connues

- Pas de cadres imbriqués (`iframe`, `object`, `embed` deviennent vides).
- Les `import` relatifs de modules ES ne se résolvent pas (le script est une
  URL `data:`).
- Pas de requête de prévol CORS (`OPTIONS`) : seuls les en-têtes de la
  réponse sont vérifiés.
- `HttpOnly` n'est pas modélisé côté `document.cookie`.
- `target="_blank"` ouvre dans le même onglet.

## Tests

- `Tests/HackerOs.Apps.Browser.Tests` : composition HTML, réécriture CSS, pont
  (CORS, credentials, cookies), URL, stockage, pages d'erreur, listing,
  téléchargements, journal des outils de développement.
- `Tests/HackerOs.UI.E2E.Tests/BrowserHtmlRenderingTests.cs` : dans Chromium,
  page web avec CSS, script, `fetch` et cookie ; sandbox vérifié ; onglet
  Network ; vue source ; XHR et formulaire `POST` ; site local `file://` avec
  `localStorage`, `fetch` CORS, lien et retour ; page d'erreur DNS.
- `Tests/HackerOs.Apps.Browser.Tests/FormHistoryTests.cs` et
  `Tests/HackerOs.UI.E2E.Tests/BrowserSiteDataTests.cs` : historique de
  formulaires (fichier, limites, `<datalist>` dans Chromium).
- `Tests/HackerOs.UI.E2E.Tests/BrowserHomePageTests.cs` : hors mode démo, le
  navigateur s'ouvre sur HackerSearch et une recherche tapée dans la barre
  d'adresse affiche ses résultats (ADR 0085 : le catalogue réseau par défaut
  est branché dans l'OS).
- Pages de test : `example.hackeros` (`/page.html`, `/form.html`, `/echo`,
  `/api/ping`) dans `SmokeTestNetworkSeed`, et le site
  `~/Documents/e2e-site` du mode démo E2E.

## Liste des tâches

- [x] Types MIME des fichiers locaux (`SimulatedFileTypes`).
- [x] Transport du navigateur (`http(s)` par le SDK, `file:` et listing).
- [x] URL de la barre d'adresse, `view-source:`, chemins locaux.
- [x] Chargeur de sous-ressources et réécriture CSS.
- [x] Composition HTML (AngleSharp), CSP et injection du pont.
- [x] Iframe isolée et relais `postMessage` (`BrowserPageView.razor.js`).
- [x] Pont : `fetch`, XHR, liens, formulaires, cookies, stockage, console, history.
- [x] `localStorage` persisté par site, `sessionStorage` par onglet.
- [x] Outils de développement Network et Console.
- [x] Pages d'erreur réseau et HTTP, téléchargements, PDF.
- [x] Historique `Push`/`Replace`/`Traverse`.
- [x] Pages de test dans le réseau de démo et site local E2E.
- [x] Tests unitaires et E2E.
- [x] Documentation, ADR 0079, index.
