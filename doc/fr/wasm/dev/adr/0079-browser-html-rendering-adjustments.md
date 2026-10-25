> [English version](../../../../en/wasm/dev/adr/0079-browser-html-rendering-adjustments.md)

# ADR 0079 : ajustements du rendu HTML du navigateur

## Statut

Accepté le 2026-10-16, avec le chantier `web/4-browser-html`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** [ADR 0075](0075-browser-html-rendering.md), sans changer ses
  décisions (iframe sans même origine, sous-ressources chargées en C#, pont).
- **Documentation :** [`browser-html-rendering.md`](../browser-html-rendering.md).

## Contexte

La mise en œuvre de l'ADR 0075 a rencontré des points que l'ADR ne tranchait
pas, ou qu'une iframe sans même origine ne permet pas de faire comme un vrai
navigateur. Ils sont consignés ici.

## Décision

1. **`file://` limité au dossier personnel.** Le navigateur ne demande pas
   `filesystem.system.read` : cette capacité est dangereuse (ADR 0049) et
   déclenchait une approbation administrateur à chaque lancement, puisque le
   navigateur n'est pas une app système. Il lit et écrit le dossier de
   l'utilisateur (`filesystem.user-home.read`/`write`), comme ses
   téléchargements. Un fichier hors du dossier personnel donne
   `ERR_ACCESS_DENIED`.
2. **Sous-ressources en URL `data:` uniquement.** Pas de `blob:` créé par
   l'hôte : le `srcdoc` porte tout. Une ressource en échec devient `data:,`
   et apparaît dans l'onglet Network.
3. **Pas de cadres imbriqués.** `iframe`, `frame`, `object` et `embed`
   d'une page deviennent `about:blank`, avec un avertissement Console.
4. **Navigation par script détectée, pas interceptée.** `location` reste
   `about:srcdoc` dans une iframe sans même origine : `location.href = …`
   ne peut pas être détourné. `document.URL` et `document.referrer` donnent
   l'URL réelle ; une navigation par script est détectée (chargement du cadre
   sans annonce du pont) et remplacée par une page d'erreur
   `ERR_BLOCKED_BY_CLIENT` plutôt que par un document sans pont.
5. **Limites acceptées pour cette phase.** Les `import` relatifs de modules
   ES ne se résolvent pas ; aucun prévol CORS (`OPTIONS`) n'est envoyé, seuls
   les en-têtes de la réponse sont vérifiés ; `HttpOnly` n'est pas modélisé ;
   `target="_blank"` ouvre dans le même onglet.
6. **Boîtes de dialogue.** `alert` devient une bannière non bloquante
   au-dessus de la page ; `confirm` renvoie `false` et `prompt` `null`, comme
   un navigateur qui bloque les boîtes de dialogue d'une page.
7. **Sites de démonstration.** Le site `example.hackeros` du réseau de démo
   sert des pages HTML de test (`/page.html`, `/form.html`, `/echo`,
   `/api/ping`) et HackerSearch renvoie `Access-Control-Allow-Origin: *` sur
   `/api/search`, pour qu'une page d'une autre origine puisse l'appeler.

## Conséquences

- Le navigateur ne déclenche plus d'approbation administrateur au lancement.
- Une page qui navigue par script doit utiliser un lien ou un formulaire
  pour fonctionner dans HackerOS.
- Les limites du point 5 pourront être levées par un ADR ultérieur.
