# ADR 0089 : en-tête `Cookie` écrit par l'app

## Statut

Accepté le 2026-10-17, avec le chantier `web/14-cookie-header`.

- **Phase du plan :** phase web, suite ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** ADR 0074 §1 et §2 (échanges HTTP de la passerelle réseau,
  livraison au processus à l'écoute) et ADR 0056 §6 (`HackHttpClient` et son
  `HackCookieContainer`). Ne remplace aucune décision.

## Contexte

La passerelle réseau de l'OS (`AppNetworkGateway`) envoie à chaque saut les
cookies du conteneur du client (`HackHttpRequest.Cookies`, le « pot à cookies »).
Un en-tête `Cookie` que l'app écrivait elle-même, par exemple
`curl -H 'Cookie: .HackSession=…'`, était retiré avant la livraison au site
local et n'atteignait jamais les contrôleurs du réseau simulé, qui ne lisent que
`SimulatedHttpRequest.Cookies`. Constaté par `web/6` (`docs/asp-hack-mvc.md`),
la limite empêchait de rejouer une session ou un jeton d'anti-falsification à la
main depuis le terminal, ce que fait tout `curl` réel.

## Décision

1. **L'en-tête est envoyé.** Les paires `nom=valeur` de chaque en-tête `Cookie`
   de la requête (RFC 6265 §4.2 ; seule la première `=` sépare le nom de la
   valeur) sont fusionnées avec les cookies du pot pour l'hôte du saut, en un
   seul en-tête `Cookie` vers un site local et dans `Cookies` de la requête vers
   un site simulé.
2. **Ce qui est tapé gagne.** Une paire explicite remplace le cookie du pot de
   même nom ; les autres cookies du pot sont envoyés aussi, dans leur ordre.
3. **Même hôte seulement.** L'en-tête suit les redirections qui restent sur
   l'hôte demandé et est retiré quand une redirection part vers un autre hôte,
   comme `curl` depuis 7.83 (il ne fuit pas vers un tiers).
4. **Rien n'est stocké.** Un en-tête de requête ne crée pas de cookie : les
   paires explicites n'entrent jamais dans le pot, ne reviennent pas en
   `Set-Cookie` dans la réponse et ne sont pas ajoutées au
   `HackCookieContainer` du client. Seuls les `Set-Cookie` du site y entrent.
5. **Le navigateur ne change pas.** Son transport retire déjà tout en-tête
   `Cookie` venant d'une page (`SdkBrowserTransport`), comme un navigateur qui
   l'interdit au `fetch` ; ses cookies restent ceux de l'onglet. Le relais vers
   le vrai Internet transmettait déjà les en-têtes tels quels.

## Conséquences

- `curl -H 'Cookie: session=…' http://intranet.hack/` atteint le site ; le pot
  par exécution de `curl` garde son rôle pour les cookies posés en cours de
  route (redirection après connexion).
- Tests : `LocalNetworkTests.A_cookie_header_given_to_curl_reaches_the_site_without_entering_the_jar`
  (site local, deux modes d'exécution) et
  `Wave4NetworkTests.HackHttpClient_sends_an_explicit_cookie_header_to_a_simulated_site_without_storing_it`
  (HackMail).
