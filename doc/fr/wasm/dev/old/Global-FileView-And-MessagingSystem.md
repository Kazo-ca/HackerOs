# Global FileView Control & Messaging System

## Purpose

This is the entry point for a cross-cutting design spanning two related additions to HackerOS v3:

1. **`FileView`** — a single, reusable Razor control for displaying and manipulating virtual-filesystem
   contents (Tree / Details / Icons), shared by every host that needs to show files: today the
   `HackerOs.Apps.FileExplorer` window app, and in a future phase the Desktop background itself.
2. **Topic messaging** — an extension of the existing `IEventBus`/`IAppEventGateway` pub/sub system to
   support named, namespaced topics; emitter authorization (closing the gap where any app can currently
   publish any event type); shared channels with optional per-channel security restrictions; and a
   `System.Threading.Channels`-based directory-watch API so `FileView` (and anything else) can react to
   filesystem changes without polling.

These two systems are coupled because `FileView` is the primary consumer that needs live directory
change notifications, and because its external-scripting surface (see
[`FileViewControl.md`](Global-FileView-And-MessagingSystem/FileViewControl.md#external-scripting-surface))
is built on the same typed-event conventions the messaging system formalizes.

## Documents in this set

| Doc | Content |
| --- | --- |
| [`FileViewControl.md`](Global-FileView-And-MessagingSystem/FileViewControl.md) | Full functional and API specification of the `FileView` control: view modes, inline rename, drag & drop, shell icon abstraction, cancelable events, context-menu customization, external scripting surface. |
| [`MessagingSystem.md`](Global-FileView-And-MessagingSystem/MessagingSystem.md) | Topic naming, emitter authorization, shared channels with capability-based security, and the filesystem-watch API built on top. |
| [`integrationPlan.md`](Global-FileView-And-MessagingSystem/integrationPlan.md) | Phased, checklist-style implementation plan (matching this repo's `docs/integration-task-list.md` conventions), ending with migrating `HackerOs.Apps.FileExplorer` onto `FileView`, declaring directory-open file association, and adding a background `RenderFragment` slot to `DesktopArea`/`DesktopShell`. |

## Motivating example

This is the imperative usage shape this design must support (from the original request), reproduced
here as the running example referenced throughout the three documents below:

```csharp
FileView fv = this.fileBrowser;
fv.OnPathChange += (s, e) => {
    foreach (FileViewItem item in fv.Items)
    {
        if (System.IO.Path.GetExtension(item.FullPath).ToLower() == ".hack")
            item.IconOverride = ShellIconDescriptor.Png("SomeIcon.png");
    }
};

toolbarNewTextFileButton.OnClick += async (s, e) => {
    await fileSystem.CreateFileAsync(System.IO.Path.Combine(fv.CurrentDirectory.Value, "Nouveau fichier texte1.txt"));
    fv.ClearSelect();
    fv.Items.FirstOrDefault(o => o.FileName == "Nouveau fichier texte1.txt")?.Select();
    fv.SelectedItem?.Rename();
};
```

Every capability this snippet touches — `Items`, `CurrentDirectory`, `OnPathChange`, per-item
`IconOverride`, `ClearSelect()`, `Select()`, `Rename()` — is a required public member of `FileView`,
specified in [`FileViewControl.md`](Global-FileView-And-MessagingSystem/FileViewControl.md).

## Problem statement

Today (as of this writing):

- `Apps/System/HackerOs.Apps.FileExplorer/FileExplorerWindow.razor` implements Details and Grid views,
  file operations, and its context menu **inline**, in one ~1000-line file, with no reusable control
  boundary. There is no Tree view. Renaming uses a boxed prompt/dialog flow, not inline label-to-textbox
  editing. Drag & drop is entirely unimplemented (only unused payload record types exist in
  `Shared/HackerOs.AppSdk/DragDrop/VirtualFileDragPayload.cs`).
- Icons are drawn per-extension in hardcoded `if`/`switch` logic against the vector icon system
  (`HackerOs.AppSdk.Icons`); there is no injectable, per-host-replaceable icon resolution service.
- There is no reusable `ContextMenu` component and no per-file-type menu extension point.
- `Platform/HackerOs.Platform.Core/Events/InMemoryEventBus.cs` (`IEventBus`) lets **any** subscriber of
  `IAppEventGateway` publish **any** event type, including platform-trusted lifecycle events such as
  `SessionLoggedOutEvent` or `ProcessStateChangedEvent` — there is no ownership or authorization check on
  `Publish<TEvent>`. This was confirmed as a real concern to close, not just harden defensively.
- The filesystem gateway (`IAppFileSystemGateway`) has no change-notification API at all; today
  `FileExplorerWindow` reloads its own directory listing after its own mutations and otherwise has no way
  to learn about changes made by another app or process.
- `Platform/HackerOs.Windowing.Blazor/DesktopArea.razor` has no rendering slot above the background and
  below the window layer; only `WindowContent` (per-window) exists today.

## Key decisions already made

These were confirmed with the requester before drafting the two spec documents, and constrain every
design choice in them:

1. **Scope of this task**: produce the three design/spec documents plus a **skeleton** (interfaces,
   models, empty component shell, no business logic) of the new projects they describe. Full
   implementation is deliberately deferred to follow-up work driven by `integrationPlan.md`.
2. **Messaging system relationship to `IEventBus`**: **extend/modify** the existing bus rather than build
   a fully parallel system, specifically to close the emitter-authorization gap described above. See
   [`MessagingSystem.md`](Global-FileView-And-MessagingSystem/MessagingSystem.md#relationship-to-the-existing-ieventbus).
3. **Icon retrieval "is the Shell's responsibility"**: means an injectable **Platform Shell icon
   service** (`IShellIconProvider`), not literal host-OS icon extraction — HackerOS has no such thing
   today and none is being added. The service must support both the existing vector icon system
   (Lucide/Bootstrap/Font Awesome/Simple Icons via `HackerOs.AppSdk.Icons`) and PNG/raster images, behind
   one `ShellIconDescriptor` type. See
   [`FileViewControl.md`](Global-FileView-And-MessagingSystem/FileViewControl.md#icon-resolution-ishelliconprovider).
4. **`DesktopArea`/`DesktopShell` change is infrastructure only**: this task adds a generic
   `RenderFragment` slot rendered above the desktop background and below the window layer. It does
   **not** wire `FileView` into the Desktop to render desktop icons — that remains explicitly a future
   phase, as stated in the original request.

## Governing ADRs

Per this repo's `AGENTS.md`, an accepted architecture decision needs a numbered ADR under
`doc/fr/wasm/dev/adr/`. All three this design required are now accepted and linked from
[`../README.md`](README.md); `integrationPlan.md`'s Phase 0 is complete and Phase 1 onward may begin:

- [ADR 0037](../adr/0037-reusable-file-view-control.md) — `FileView` as the canonical file-listing control,
  and the resulting deprecation of `FileExplorerWindow`'s inline Details/Grid rendering in favor of
  hosting `FileView`.
- [ADR 0038](../adr/0038-emitter-authorized-topic-messaging.md) — emitter-authorized topic messaging
  superseding unrestricted `IAppEventGateway.Publish<TEvent>`, a breaking contract change to the Phase 1
  baseline (`P1-SYS-008`), with a concrete migration table for every existing publish call site. See
  [`MessagingSystem.md`](Global-FileView-And-MessagingSystem/MessagingSystem.md#breaking-change-notice).
- [ADR 0039](../adr/0039-directory-open-file-association.md) — the `inode/directory` media-type convention so
  "open a folder in a new window" goes through the existing `IAppIntentGateway.OpenFileAsync`/
  `FileAssociationResolver` machinery instead of a bespoke path.
- [ADR 0040](../adr/0040-declared-topic-permissions.md) — declared topic permissions (an app-declared,
  optional permission gating its own shared channel, reusing the existing capability-grant flow) and the
  tri-state `SharedChannelPolicy` access model this depends on.
