# Platform File Dialogs

## Purpose

Platform file dialogs provide app-scoped selection over the virtual filesystem.
They never expose native browser objects or grant authority merely by returning
a raw path.

## Architecture

`FileDialogCoordinator` implements the App SDK `IFileDialogService` once per
authenticated session. It validates the exact dialog capability through the
app-bound `ICapabilityChecker`, rejects contexts from another session, and keeps
open, save, and folder requests in one FIFO queue. Only `ActiveRequest` may be
rendered. Completing or cancelling it promotes the next request and raises
`Changed`.

Cancellation is an ordinary dialog result, including cancellation while queued.
Disposal cancels every remaining request. Request presentations carry only the
identity required to bind ownership, the typed SDK request, and the exact
app-scoped filesystem gateway captured from the trusted execution context. They
do not expose root services or unrestricted repositories.

`VirtualFolderBrowser` is shared by Open, Save, and Folder Select. It implements
lazy enumeration, canonical breadcrumbs, loading/error/empty states, stale-load
rejection, keyboard navigation, filters, and single/multiple selection. Save
validates canonical entry names and detects conflicts with `StatAsync`. Folder
creation uses the last observed parent revision and leaves policy enforcement to
the scoped gateway.

`FileMetadata.MediaType` carries the normalized persisted descriptor into Stat
and directory enumeration snapshots, so media filters remain lazy and require no
content-read authority merely to classify visible files.

Successful SDK results contain `SelectedFileResource` or
`SelectedFolderResource`, pairing the canonical path with a short-lived
`FileSystemSelectedResourceHandle`. Open maps requested read/read-write access
to exact handle bits; Save delegates read/write/metadata and Folder Select
delegates enumerate/metadata. Handles default to a 15-minute lifetime and are
bound to the requesting app, user, and process.

`FileDialogWindowAdapter` projects the active request into a real owner-modal
Window owned by the requesting process window. Completion closes that modal
through the authoritative runtime and restores owner focus. Escape, title-bar
close, and request cancellation all complete as ordinary Cancelled results.

## Usage

The session composition root creates one coordinator with its trusted
`SessionId` and exposes it to Window apps as `IFileDialogService`. A Platform
renderer observes `ActiveRequest`, displays the matching owner-modal component,
then calls `SelectOpen`, `SelectSave`, `SelectFolder`, or `Cancel` with the exact
request ID.

## Key Decisions

- Open, save, and folder requests share one deterministic FIFO queue.
- Capability denial occurs before rendering or filesystem enumeration.
- Session identity comes from `IAppExecutionContext`; callers cannot choose it.
- The renderer receives the already restricted app filesystem gateway rather
	than resolving a root repository or rebuilding authority from identifiers.
- Media filtering uses directory metadata and does not open visible file content.
- Unix dotfiles remain visible and participate in the active filter like other
	entries; dialogs do not maintain a separate hidden-file preference.
- App disable publishes one typed event per transitive disabled app; the handle
	registry revokes every matching grant. Uninstall must run this disable lifecycle
	before removing a future runtime-installed catalog entry.

## Task List

- [x] `P2-DLG-001` Session-bound FIFO coordinator and cancellation.
- [x] `P2-DLG-002` Exact pre-render capability validation.
- [x] `P2-DLG-003` Reusable virtual folder browser.
- [x] `P2-DLG-004` File-open dialog.
- [x] `P2-DLG-005` File-save dialog.
- [x] `P2-DLG-006` Folder-select dialog.
- [x] `P2-DLG-007` Selected-resource handle issuance.
- [x] `P2-DLG-008` Expiry and lifecycle revocation.
- [x] `P2-DLG-009` Modality and ordinary cancellation.
- [x] `P2-DLG-010` Component/browser integration coverage.

Chrome harness scenarios render the typed Open, Save, and Folder components in
authoritative owner-modal Windows. They cover filters and dotfiles,
multi-selection, overwrite confirmation, folder creation, filesystem denial,
Escape, focus return, and clean console/network behavior. Headless authorization
and lifecycle suites cover capability denial, protected resources, expiry, and
revocation.