# Display windows (surfaces): API for apps

This document describes how an app opens a surface, draws its content in it, follows its events and opens a page in a detached window. The internal mechanism (portal, JS initializer, `displaySurfaces.js` module, OS browser) is in [`dev/detached-display-windows.md`](../dev/detached-display-windows.md).

## SDK types

| File | Role |
|---|---|
| `Shared/HackerOs.AppSdk.Blazor/Display/DisplaySurfaceContracts.cs` | Public contract: `IDisplaySurfaceService`, `IDisplaySurface`, `DisplaySurfaceOptions`, `DisplaySurfaceOpenResult`, `DisplaySurfaceOpenStatus`, `DisplaySurfaceState`. |
| `Shared/HackerOs.AppSdk.Blazor/Display/DisplaySurfaceContent.razor(.cs,.css)` | Portal: renders its content here, then moves its element into the surface; renders it back before being removed. `Surface` is optional (ADR 0084 §7): with no open surface, the content stays in place, which makes it possible to render it only once and keep its state when detaching. |
| `Shared/HackerOs.AppSdk/Apps/HackBrowser.cs` | `HackBrowser.OpenAsync(url)`, `HackBrowser.OpenDetachedAsync(url)`. |

## Opening a surface

`IDisplaySurfaceService.OpenAsync(new DisplaySurfaceOptions(title, width, height))`
calls `window.open("", "hackeros-surface-<n>", "popup=yes,…")`. The host
browser only opens a window after a user gesture (about five seconds after a
click or a key press): the call comes from a click handler, or just after the
launch that a click triggered.

- **Opened**: `Opened` result with the surface (`surface-1`, `surface-2`...).
- **Blocked**: `Blocked` result; the service publishes a
  "Detached window blocked" notification that explains how to allow pop-up
  windows.
- **Impossible** (no interactive JavaScript: prerendering, tests):
  `Unavailable`.

## Drawing in a surface

```razor
@if (_surface is not null)
{
    <DisplaySurfaceContent Surface="_surface">
        <MonPanneau Donnees="_donnees" />
    </DisplaySurfaceContent>
}
```

The content remains an ordinary piece of the app's window: same cascading
parameters, same execution context, same event handlers. Only its DOM lives in
the surface. It keeps the appearance of the place where it is rendered: the
module copies the custom CSS properties (`--hos-*`, theme) and the anchor's
text style onto the surface root.

## Surface events

`IDisplaySurface.Changed` reports the size (`Width`, `Height`), the focus
(`HasFocus`), the title and the state. It arrives outside any Blazor event and
outside the app's context: a component handles it with `InvokeAsync` and, if it
calls the SDK, `HackExecutionContext.Enter(AppContext)`.

| State | When |
|---|---|
| `Open` | The window shows the surface. |
| `ClosedByUser` | The user closed the window. |
| `Closed` | The owner called `CloseAsync`, or `CloseAllAsync` was called. |
| `Lost` | The window left the surface's document (another address was typed); the OS closes it. |

When a surface ends, its content returns under its anchor in the app's
window. Reloading or closing the OS tab closes all surfaces.

## From another app

```csharp
// From a click: the host browser refuses a window without a gesture.
await HackBrowser.OpenDetachedAsync("https://intranet.hack/rapport");
```

Launches the browser with `--detached <url>` (capability `apps.launch`): the
detached window opens before the first navigation. If it is blocked, the
page is displayed in the OS browser, with the banner and the notification.
`HackBrowser.OpenAsync(url)` simply opens the address in a new
browser.

## Limits for apps

- The JavaScript state of a page (variables, form in progress) is lost on
  detaching and on reattaching.
- Styles rendered in the desktop body (MudBlazor theme) are copied when
  opening; only those in `<head>` and the attributes of `<html>` are followed
  afterwards. A theme change updates the theme variables at the next
  opening.
- Blazor sites (ADR 0084, [`blazor-sites.md`](blazor-sites.md)) detach like
  other pages and keep their state: the page area is rendered only once through
  `DisplaySurfaceContent`, whose portal moves the DOM without recreating the
  components.
