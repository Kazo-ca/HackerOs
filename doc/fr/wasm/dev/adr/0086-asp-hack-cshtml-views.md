# ADR 0086 : vues `.cshtml` d'ASP.Hack compilées par le compilateur Razor, sans ASP.NET MVC

## Statut

Accepté le 2026-10-16, avec le chantier `web/8-cshtml-views`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** [ADR 0076](0076-asp-hack-mvc.md) §8 (la preuve de faisabilité a réussi :
  le moteur interprété de repli n'est pas construit) ; [ADR 0083](0083-asp-hack-razor-views.md)
  (même moteur de vues, même table générée, mêmes sections).
- **Amende :** [ADR 0007](0007-enforce-collocated-razor-assets.md) : la vérification au build
  couvre aussi les `.cshtml` (§6) ; [ADR 0083](0083-asp-hack-razor-views.md) : une clé absente
  de `ViewData` se lit `null`, comme dans ASP.NET (§3).
- **Documentation :** [`asp-hack-cshtml-views.md`](../../framework/asp-hack-cshtml-views.md).

## Contexte

L'ADR 0076 §8 demande une preuve avant le code : compiler au build un `.cshtml`
avec `@model`, `@inject`, `@section`, `_Layout` et `_ViewStart` contre une classe
de base d'ASP.Hack, sans référencer ASP.NET MVC (qui ne tourne pas dans Blazor
WebAssembly).

La preuve (SDK .NET 10.0.402, projet `Microsoft.NET.Sdk.Razor`) montre que le
générateur de sources Razor du SDK compile **toujours** un `.cshtml` en mode MVC,
même sans référence à MVC, et que le C# produit ne dépend d'ASP.NET que par des
noms, jamais par du code :

- deux attributs d'assemblage et de classe :
  `Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemAttribute` et
  `RazorCompiledItemMetadataAttribute` ;
- l'attribut `Microsoft.AspNetCore.Mvc.Razor.Internal.RazorInjectAttribute` sur
  chaque propriété `@inject` ;
- trois `using` (`Microsoft.AspNetCore.Mvc`, `.Rendering`, `.ViewFeatures`) ;
- la classe de base par défaut `Microsoft.AspNetCore.Mvc.Razor.RazorPage<TModel>`
  et cinq `@inject` implicites (`Html`, `Json`, `Component`, `Url`,
  `ModelExpressionProvider`) dont les types sont ceux de MVC ;
- `Microsoft.AspNetCore.Mvc.Razor.HelperResult` pour un modèle en ligne
  (`@<b>@item</b>`).

Tout le reste est un appel de méthodes sur la classe de base (`WriteLiteral`,
`Write`, `BeginWriteAttribute`, `DefineSection`, `PushWriter`...). Un
`_ViewImports.cshtml` peut remplacer la classe de base (`@inherits` avec le
jeton `TModel`, que `@model` remplace) et chacun des `@inject` implicites (une
propriété du même nom gagne). La preuve a donc réussi.

## Décision

### 1. Compilation

Le compilateur Razor du SDK compile les `.cshtml` du projet au build, comme dans
ASP.NET Core. ASP.Hack ne fournit ni compilateur ni analyseur de vues.

### 2. Ce que le build ajoute à un site

Pour un projet qui référence `HackerOs.AppSdk.Web` et contient des `.cshtml`, la
cible `_HackerOsAspHackRazorViews` de `build/HackerOs.Apps.targets` ajoute :

- un `_ViewImports.cshtml` **racine** (fichier du SDK, `build/asp-hack/_ViewImports.cshtml`,
  lié au projet sous ce nom) : `@inherits HackRazorPage<TModel>` et les cinq
  `@inject` implicites remplacés par les helpers d'ASP.Hack ;
- un fichier C# (`build/asp-hack/HackRazorCompat.cs`) de types **internes au
  site**, aux noms qu'attend le C# généré : les attributs ci-dessus, la classe
  `HelperResult` (dérivée de `HackHelperResult`), un type vide dans chacun des
  trois espaces de noms des `using`, et les deux attributs d'assemblage que le
  SDK écrit pour un projet qui a des `.cshtml`
  (`ProvideApplicationPartFactoryAttribute`, `RelatedAssemblyAttribute`).

Ces noms `Microsoft.AspNetCore.*` ne sortent jamais du site : ils sont internes,
la bibliothèque `HackerOs.AppSdk.Web` n'en déclare aucun, et un site ne peut pas
référencer ASP.NET MVC. Le projet ne doit pas avoir son propre
`_ViewImports.cshtml` à la racine (erreur de build) : ses imports vont dans
`Views/_ViewImports.cshtml`. L'avertissement `RAZORSDK1004` (« pas de support
MVC ») est rendu silencieux : l'absence de MVC est voulue.

### 3. Classe de base et activation

`HackRazorPage` / `HackRazorPage<TModel>` (`HackerOs.AppSdk.Web.Views`) offre ce
que le C# généré appelle et ce qu'une vue MVC utilise : `Model`, `ViewData`,
`ViewContext`, `Context`, `Request`, `ModelState`, `Layout` (un nom de vue),
`RenderBody()`, `RenderSection(...)`, `IsSectionDefined`, `IgnoreBody`,
`IgnoreSection`. `ViewData["x"]` d'une clé absente vaut `null` (pour les
deux genres de vues), comme dans ASP.NET. Le moteur crée la page, remplit les propriétés `[RazorInject]`
(un helper de la page, sinon un service de la portée de la requête, comme le
`RazorPageActivator` d'ASP.NET), puis appelle `ExecuteAsync`. L'encodage HTML
suit MVC (`Write` encode, `WriteLiteral` non, un contenu `IHackHtmlContent` est
écrit tel quel ; un attribut dont la seule valeur est `null` ou `false`
disparaît, `true` écrit le nom de l'attribut).

### 4. Table des vues

Le générateur de routes (ADR 0082) lit aussi les `.cshtml` de `Views/` et ajoute
leur classe à `HackWebGeneratedRoutes.Views`, nommée comme le compilateur Razor
la nomme (`AspNetCoreGeneratedDocument.Views_Home_Index`, ou l'espace de noms du
`@namespace` le plus proche suivi des dossiers). Les `_ViewImports.cshtml` ne
sont pas des vues. Un `.razor` et un `.cshtml` au même chemin sont une erreur
`HOSWEB0008`.

### 5. Rendu

`HackViewEngine` rend les deux genres de vues avec le même algorithme que
l'ADR 0083 : la vue d'abord, puis chaque layout autour du HTML précédent, avec
les mêmes `ViewData` et le même dictionnaire de sections. Les genres se mêlent :
une vue `.cshtml` peut avoir un layout `.razor` et l'inverse, un partial peut
être de l'un ou l'autre genre. Spécificités `.cshtml` :

- les `_ViewStart.cshtml` de `Views/` puis des dossiers jusqu'à celui de la vue
  s'exécutent avant une vue (pas avant un partial ni un layout) et donnent sa
  valeur initiale à `Layout` ;
- `Layout = "_Layout"` est cherché comme un nom de vue (dossier du contrôleur,
  puis `Shared/`) ;
- une section `@section` est rendue en HTML dès la fin de la page qui la définit
  (ASP.NET la rend quand le layout la demande ; le résultat est le même sauf
  effet de bord) ; un layout imbriqué peut redéfinir une section qu'il a rendue.

### 6. Règle de l'ADR 0007

La vérification au build de l'ADR 0007 (pas de `<style>`, de `style=`, de
`<script>` avec corps ni d'attribut `on...=`) couvre aussi les `.cshtml` : le
CSS et le JavaScript d'une vue vont dans le `wwwroot` du site, comme pour une
vue `.razor` (ADR 0083 §6).

### 7. Limites retenues

Pas de tag helpers (`asp-action`, `asp-for`) : le SDK ne les trouve pas sans
MVC ; les helpers HTML (`Html.ActionLink`, `Html.BeginForm`, `Html.TextBoxFor`...)
et les composants `.razor` de l'ADR 0083 (`@await Component.InvokeAsync<HackLink>(...)`)
les remplacent. Pas de Razor Pages (`@page`), de `ViewBag`, de `TempData` ni de
view components MVC. Un `~/` dans un attribut écrit en dur n'est pas résolu
(pas de `UrlResolutionTagHelper`) : `@Url.Content("~/...")`.

## Conséquences

- Un développeur ASP.NET retrouve ses `.cshtml` : `@model`, `@inject`,
  `@section`, `@functions`, modèles en ligne, `_Layout`, `_ViewStart`,
  `_ViewImports`, `Html.*`.
- La compilation **dans l'OS** reste possible plus tard : la classe de base et
  les types de compatibilité sont propres à ASP.Hack.
- Le moteur interprété de repli (question 17 du plan) n'est pas construit.
- Si une version du SDK change les noms attendus par le C# généré, le build d'un
  site échoue avec une erreur de type manquant : il suffit d'ajouter le nom au
  fichier de compatibilité.
