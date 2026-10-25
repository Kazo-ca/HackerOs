> [English version](../../../../en/wasm/dev/adr/0087-asp-hack-intranet-sample-adjustments.md)

# ADR 0087 : exemple Intranet, ajustements de fin de phase web

## Statut

Accepté le 2026-10-17, avec le chantier `web/11-sample-final`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md), §5).
- **Complète :** ADR 0084 §5 (navigation d'un site Blazor), ADR 0083 et 0086
  (classes de base des vues), ADR 0076 §6 (sous-ensemble d'ASP.NET retenu).
  Ne remplace aucune décision ; constate aussi ce que la phase n'a pas livré de
  l'ADR 0073 §3.

## Contexte

Le dernier chantier de la phase web réunit toutes les briques dans un seul site,
l'exemple **Intranet** (`http://intranet.hack/`) : pages MVC en `.cshtml` et en
`.razor` sous un même layout, API REST, JavaScript du `wwwroot`, session par
cookie, formulaires anti-falsification et tableau de tâches Blazor sous
`/board`. C'est le premier site **livré** (projet sous `Apps/`, analysé pour le
trimming comme toute app) qui a des vues, et le premier qui mêle pages MVC et
pages Blazor sur la même origine. Quatre points sont apparus.

## Décision

### 1. Un lien à `target` autre que `_self` quitte le site Blazor

L'ADR 0084 §5 fait de tout lien vers l'adresse du site une navigation interne.
Un site qui a aussi des pages MVC (ADR 0073 §2 : « MVC et Blazor dans la même
app ») n'avait alors aucun lien simple vers elles : le routeur du site répondait
« page introuvable ». Comme Blazor, qui n'intercepte pas un lien dont la cible
est un autre cadre, la zone de page traite désormais un `<a>` dont l'attribut
`target` existe et n'est pas `_self` (`_top`, `_blank`, un nom) comme une
navigation du document, avec `forceLoad`. Un onglet par fenêtre : la page
s'ouvre dans cet onglet (comme `target="_blank"` dans une page HTML, ADR 0079).
`HackWebSiteView.OnLinkClicked` reçoit ce drapeau du module JS.

L'exemple écrit `<a href="/Notes" target="_top">` ; dans l'autre sens, un lien
ordinaire d'une page MVC vers `/board` charge la page hôte et démarre le site
Blazor.

### 2. Les classes de base des vues ne portent plus d'annotation de trimming

Les vues sont nommées par `typeof()` avec `DynamicallyAccessedMemberTypes.All`
(table des vues générée, `RazorCompiledItemAttribute`, `[Layout]`). Le trimmer
considère alors chaque membre de la vue **et de ses classes de base** comme lu
par réflexion, et signale tout membre annoté (`IL2110`, `IL2111`). Les projets
de test n'analysent pas le trimming : la faute n'apparaissait qu'avec une app
livrée, où elle casse la build (avertissements en erreurs).

- `HackViewBase.Layout` n'est plus annoté ; son `set` supprime `IL2067` avec la
  même justification que `HackViewEngine.LayoutOf` (un layout est un composant
  de l'assembly de la vue, nommé par `typeof()` comme dans `[Layout]`).
- `HackRazorPage.RenderAsync` remplit ses `@inject` avant de passer à une
  méthode asynchrone : le paramètre annoté `pageType` ne devient plus un champ
  de la machine à états.

Règle pour la suite : aucun membre d'une classe de base de vue ou de composant
(`HackViewBase`, `HackRazorPage`, `HackComponentBase`...) ne porte
`[DynamicallyAccessedMembers]` ; l'annotation reste sur les paramètres des
méthodes de l'OS qui reçoivent un type (`HackViewDescriptor`, `View<T>()`).
L'exemple Intranet, compilé avec l'hôte Ecosystem, vérifie la règle à chaque
build.

### 3. `TryValidateModel` sur les contrôleurs

Un `PATCH` fusionne un corps partiel dans l'entité stockée puis doit vérifier
le résultat avec les règles `DataAnnotations` du modèle complet, ce que
`ControllerBase.TryValidateModel` fait dans ASP.NET Core. `HackControllerBase`
reçoit `TryValidateModel<TModel>(model, prefix)` (type annoté
`PublicProperties`, erreurs dans `ModelState`, renvoie `ModelState.IsValid`),
ajouté au sous-ensemble de l'ADR 0076 §6.

### 4. L'exemple est installé dans les hôtes ; ses données restent en mémoire

`HackerOs.Samples.Intranet` est, comme le site d'exemple Blazor (ADR 0084), une
app `Web` intégrée non système, cachée du lanceur, référencée par l'Ecosystem et
`test/test` (chargement paresseux). Ses notes et ses cartes sont des singletons
du processus du site : elles reviennent à leur état initial quand l'OS arrête le
site.

La base d'un site décidée par l'ADR 0073 §3 (IndexedDB de l'hôte, nom imposé
`hackweb.<id>`, `HackWebDb`, effacement à la désinstallation, liste dans
Diagnostic) n'a été confiée à aucun chantier du plan et n'existe pas. De même,
les journaux de débogage d'un site ASP.Hack (`/var/log/hackweb/<id>.log`) et la
saisie semi-automatique du navigateur ne sont pas réalisés. Ces décisions
restent valables ; elles demandent un chantier à part.

## Conséquences

- Un site mixte relie ses deux moitiés par des liens ordinaires ; la règle du
  §1 est celle de Blazor, donc un développeur ASP.NET la connaît.
- Toute app livrée peut avoir des vues `.razor` et `.cshtml` ; une régression
  du §2 casse la build de l'Ecosystem.
- Le guide du développeur ([`asp-hack-developer-guide.md`](../../framework/asp-hack-developer-guide.md))
  s'appuie sur l'exemple et signale le §4 dans ses limites.
