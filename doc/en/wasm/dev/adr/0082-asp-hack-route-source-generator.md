# ADR 0082 : table de routes ASP.Hack produite par un générateur de sources Roslyn

## Statut

Accepté le 2026-10-16, avec le chantier `web/6-mvc-core`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Remplace :** la phrase de l'[ADR 0076](0076-asp-hack-mvc.md) §3 « la table de
  routes est générée au build *par le générateur de manifestes* ». Le reste de
  l'ADR 0076 §3 (table générée au build, pas de réflexion au démarrage, conflits
  en erreurs `HOSWEB0001`...) est conservé.
- **Complète :** ADR 0076 §1, §3, §4 et §5 sur les points listés plus bas.
- **Documentation :** [`asp-hack-mvc.md`](../../framework/asp-hack-mvc.md).

## Contexte

Le générateur de manifestes (`HackerOs.Tools.ManifestGenerator`, ADR 0047) tourne
**après** la compilation : il charge l'assembly de l'app et instancie ses
classes `[AppManifest]`. Il ne peut donc pas ajouter de code à l'assembly. Or
une table de routes sans réflexion doit **contenir du code** : la création du
contrôleur (avec ses dépendances), la liaison de chaque paramètre et l'appel de
l'action. Produire ce code après coup voudrait dire recompiler l'assembly, ou
retomber sur la réflexion au démarrage, ce que l'ADR 0076 exclut (trimming,
temps de démarrage dans le worker web).

## Décision

1. **Un générateur de sources Roslyn.** `Tools/HackerOs.Tools.WebRouteGenerator`
   (`netstandard2.0`, `IIncrementalGenerator`) est branché comme *analyzer* sur
   chaque projet d'app par `build/HackerOs.Apps.targets` (jamais une dépendance
   d'exécution ; ignoré par la vérification de frontière du SDK, comme le
   générateur de manifestes). Pour une app sans contrôleur ni `WebAppDefinition`,
   il ne produit rien.
2. **Ce qu'il produit.**
   - `HackerOs.AppSdk.Web.Generated.HackWebGeneratedRoutes.Table` (interne à
     l'assembly) : un `HackControllerRouteTable` qui décrit chaque action
     (`HackControllerActionDescriptor`) avec trois délégués générés :
     `CreateController` (constructeur public le plus long, services résolus dans
     la portée de la requête), `BindArguments` (un appel typé du
     `HackModelBinder` par paramètre, validation `DataAnnotations` recopiée en
     code) et `Invoke` (appel direct de la méthode, tâche attendue).
   - Pour chaque `WebAppDefinition` déclarée `partial`, l'entrée du site :
     `EntryPoint => typeof(<Nom>WebHost)` et une classe
     `<Nom>WebHost : HackWebHost`. Le générateur de manifestes lit ensuite cet
     `EntryPoint` comme pour toute app : rien ne change pour lui.
3. **Un seul analyseur de gabarit.** `Shared/HackerOs.AppSdk.Web/Routing/HackRouteTemplate.cs`
   est compilé dans la bibliothèque (le routeur s'en sert) **et** lié dans le
   générateur : le build et l'exécution comprennent un gabarit exactement de la
   même façon.
4. **Erreurs de build** (catégorie `HackerOs.Web`, toutes en erreur) :

   | Code | Cas |
   |---|---|
   | `HOSWEB0001` | Deux actions sur le même gabarit (aux noms de paramètres près) et la même méthode HTTP, même ordre ; ou deux actions conventionnelles au même `{controller}/{action}`. |
   | `HOSWEB0002` | Gabarit invalide (accolade non fermée, segment complexe, contrainte inconnue, paramètre facultatif suivi d'un segment obligatoire...). |
   | `HOSWEB0003` | Même nom de route (`Name =`) sur deux gabarits différents. |
   | `HOSWEB0004` | Paramètre impossible à lier (type sans `IParsable<T>`, deux sources de corps...). |
   | `HOSWEB0005` | Contrôleur sans constructeur public. |
   | `HOSWEB0006` | `WebAppDefinition` qui n'est pas une classe `partial` de premier niveau et ne donne pas elle-même son `EntryPoint`. |
   | `HOSWEB0007` | Action non prise en charge : attribut de méthode HTTP dérivé qui n'est pas celui d'ASP.Hack, type de retour `ref struct` ou pointeur, ou action d'un `HackApiController` sans route d'attribut (comme `[ApiController]` dans ASP.NET Core). Les méthodes génériques, statiques ou `[NonAction]` ne sont pas des actions, comme dans ASP.NET Core. |

5. **Ajustements de l'ADR 0076.**
   - §1 : la bibliothèque dépend aussi de `HackerOs.App.Abstractions` (pour
     `AppDefinition`) et de `Microsoft.Extensions.DependencyInjection` (§6).
   - §3 : les contraintes `double`, `decimal`, `float`, `datetime`, `required`,
     `range`, `minlength` et `maxlength` s'ajoutent à la liste ; les segments
     complexes (`{nom}.{ext}`) ne sont pas pris en charge (`HOSWEB0002`).
   - §4 : les valeurs de route d'une API anonyme (`new { id = 3 }`) passent par
     des surcharges génériques (`RedirectToAction<TValues>`, `Url.Action<TValues>`...)
     au lieu d'un paramètre `object` : le trimming garde ainsi les propriétés lues.
   - §5 : `View`, `PartialView`, l'anti-falsification et `UseStaticFiles` arrivent
     avec `web/7-razor-views`, pas avec ce chantier.
   - Une action qui rend un objet est sérialisée avec son type d'exécution, ou
     avec son **type déclaré** quand le contexte JSON du site ne connaît pas le
     type d'exécution (`DeclaredResultType`, comme `ObjectResult.DeclaredType`
     d'ASP.NET Core) : une liste construite par `[.. items]` a un type interne au
     compilateur.

## Conséquences

- Aucune réflexion pour trouver ni appeler une action : un site reste
  compatible avec le trimming (`IsTrimmable` et l'analyseur de trimming sont
  actifs sur la bibliothèque).
- Les erreurs de route apparaissent dans l'éditeur (IDE) et au build, avec
  l'emplacement du code fautif, et non au premier accès au site.
- Le compilateur dans l'OS (plus tard) pourra réutiliser le même générateur,
  comme le prévoyait l'ADR 0076.
- Un projet hors de `Apps/` (les tests, par exemple) qui contient un site doit
  référencer lui-même le générateur en *analyzer* (`OutputItemType="Analyzer"`).
