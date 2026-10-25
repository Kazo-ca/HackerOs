> [English version](../../../en/wasm/framework/display-surface-api.md)

# Fenêtres d'affichage (surfaces) : API pour les apps

Ce document décrit comment une app ouvre une surface, dessine son contenu dedans, suit ses événements et ouvre une page dans une fenêtre détachée. Le mécanisme interne (portail, initialiseur JS, module `displaySurfaces.js`, navigateur de l'OS) est dans [`dev/detached-display-windows.md`](../dev/detached-display-windows.md).

## Types du SDK

| Fichier | Rôle |
|---|---|
| `Shared/HackerOs.AppSdk.Blazor/Display/DisplaySurfaceContracts.cs` | Contrat public : `IDisplaySurfaceService`, `IDisplaySurface`, `DisplaySurfaceOptions`, `DisplaySurfaceOpenResult`, `DisplaySurfaceOpenStatus`, `DisplaySurfaceState`. |
| `Shared/HackerOs.AppSdk.Blazor/Display/DisplaySurfaceContent.razor(.cs,.css)` | Portail : rend son contenu ici, puis déplace son élément dans la surface ; le rend avant d'être supprimé. `Surface` est facultatif (ADR 0084 §7) : sans surface ouverte, le contenu reste en place, ce qui permet de le rendre une seule fois et de garder son état au détachement. |
| `Shared/HackerOs.AppSdk/Apps/HackBrowser.cs` | `HackBrowser.OpenAsync(url)`, `HackBrowser.OpenDetachedAsync(url)`. |

## Ouvrir une surface

`IDisplaySurfaceService.OpenAsync(new DisplaySurfaceOptions(titre, largeur, hauteur))`
appelle `window.open("", "hackeros-surface-<n>", "popup=yes,…")`. Le navigateur
hôte n'ouvre une fenêtre qu'après un geste de l'utilisateur (environ cinq
secondes après un clic ou une touche) : l'appel vient d'un gestionnaire de
clic, ou juste après le lancement qu'un clic a déclenché.

- **Ouverte** : résultat `Opened` avec la surface (`surface-1`, `surface-2`…).
- **Bloquée** : résultat `Blocked` ; le service publie une notification
  « Detached window blocked » qui explique comment autoriser les fenêtres
  surgissantes.
- **Impossible** (pas de JavaScript interactif : prérendu, tests) :
  `Unavailable`.

## Dessiner dans une surface

```razor
@if (_surface is not null)
{
    <DisplaySurfaceContent Surface="_surface">
        <MonPanneau Donnees="_donnees" />
    </DisplaySurfaceContent>
}
```

Le contenu reste un morceau ordinaire de la fenêtre de l'app : mêmes
paramètres en cascade, même contexte d'exécution, mêmes gestionnaires
d'événements. Seul son DOM vit dans la surface. Il garde l'apparence de
l'endroit où il est rendu : le module recopie sur la racine de la surface les
propriétés CSS personnalisées (`--hos-*`, thème) et le style de texte de
l'ancre.

## Événements d'une surface

`IDisplaySurface.Changed` signale la taille (`Width`, `Height`), le focus
(`HasFocus`), le titre et l'état. Il arrive hors de tout événement Blazor et
hors du contexte de l'app : un composant le traite avec `InvokeAsync` et, s'il
appelle le SDK, `HackExecutionContext.Enter(AppContext)`.

| État | Quand |
|---|---|
| `Open` | La fenêtre montre la surface. |
| `ClosedByUser` | L'utilisateur a fermé la fenêtre. |
| `Closed` | Le propriétaire a appelé `CloseAsync`, ou `CloseAllAsync` a été appelé. |
| `Lost` | La fenêtre a quitté le document de la surface (autre adresse tapée) ; l'OS la ferme. |

Quand une surface se termine, son contenu revient sous son ancre dans la
fenêtre de l'app. Recharger ou fermer l'onglet de l'OS ferme toutes les
surfaces.

## Depuis une autre app

```csharp
// Depuis un clic : le navigateur hôte refuse une fenêtre sans geste.
await HackBrowser.OpenDetachedAsync("https://intranet.hack/rapport");
```

Lance le navigateur avec `--detached <url>` (capacité `apps.launch`) : la
fenêtre détachée s'ouvre avant la première navigation. Si elle est bloquée, la
page s'affiche dans le navigateur de l'OS, avec le bandeau et la notification.
`HackBrowser.OpenAsync(url)` ouvre simplement l'adresse dans un nouveau
navigateur.

## Limites pour les apps

- L'état JavaScript d'une page (variables, formulaire en cours) est perdu au
  détachement et au rattachement.
- Les styles rendus dans le corps du bureau (thème de MudBlazor) sont recopiés
  à l'ouverture ; seuls ceux de `<head>` et les attributs de `<html>` sont
  suivis ensuite. Un changement de thème met à jour les variables du thème à la
  prochaine ouverture.
- Les sites Blazor (ADR 0084, [`blazor-sites.md`](blazor-sites.md)) se
  détachent comme les autres pages et gardent leur état : la zone de page est
  rendue une seule fois à travers `DisplaySurfaceContent`, dont le portail
  déplace le DOM sans recréer les composants.
