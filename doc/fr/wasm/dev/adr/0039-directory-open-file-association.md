# ADR 0039: `inode/directory` Media-Type Convention for Directory File Association

## Status

Accepted on 2026-08-17.

## Context

[`FileView`](0037-reusable-file-view-control.md)'s `NewWindow` folder-activation mode needs to open a
directory in whatever app is registered to handle it — "let the Shell manage it," per the original
request — rather than `FileView` hardcoding a target app ID. HackerOS already has a complete file-open
association pipeline for **files**: `IAppIntentGateway.OpenFileAsync(VirtualPath, mediaType)` →
`FileAssociationResolver.ResolveAsync` (`Platform/HackerOs.Platform.Core/Intents/FileAssociationResolver.cs`)
→ explicit preferred app, then a configured default from the protected
`/etc/hackeros/file-associations.json` document, then any sole/chooser-required manifest-declared
candidate. Matching is `HandlesFile`/`MatchesTarget`, which accepts either an extension match **or** a
`FileHandlerManifest.MediaType` match — `MediaType` is a free-form nullable string with no format
validation beyond "not null/whitespace when `Extensions` is also empty"
(`AppManifestValidator.cs:109`). Nothing in this pipeline is file-only by construction; it has simply
never been asked to resolve a directory, because `FileExplorerWindow` today navigates directories
internally and never dispatches an intent for one.

## Decision

1. **Adopt the Unix/XDG `inode/directory` media type** as the sentinel `FileHandlerManifest.MediaType`
   value and `OpenFileIntent.MediaType` value meaning "this is a directory-open request," reusing an
   existing, well-known convention rather than inventing a HackerOS-specific string.
2. **The caller supplies `MediaType = "inode/directory"` explicitly** when it knows the target path is a
   directory (e.g. `FileView`'s `NewWindow` activation, which already knows `FileViewItem.IsDirectory`
   before calling `OpenFileAsync`). This matches how `MediaType` is already supplied everywhere else in
   this codebase (it is never auto-detected from content); no new detection logic is introduced.
3. **No `FileHandlerManifest`/`AppManifest` schema or validator change is required.** A directory handler
   is declared exactly like any media-type-based file handler: `FileHandlerManifest(MediaType:
   "inode/directory", Extensions: [], Actions: ["open"])`. `FileAssociationResolver.HandlesFile`/
   `MatchesTarget` already match on `MediaType` alone when `Extensions` doesn't apply; a directory path's
   `GetExtension` helper already returns `null` for a typical directory name (no trailing `.ext`), so no
   collision risk with real extension-based handlers exists.
4. **`org.hackeros.file-explorer` declares itself as an `inode/directory` handler** in its
   `app.manifest.json` (which today declares no `fileHandlers` at all — it has never needed to, since it
   opens directories internally) and is seeded as the protected default for `inode/directory`/`open` in
   `/etc/hackeros/file-associations.json`, via the same `FileAssociationSettingsDocuments` seeding
   mechanism used for other protected defaults.
5. **`inode/directory` is reserved and must never be produced as a regular file's detected media type.**
   Nothing in the codebase currently auto-detects `FileMetadata.MediaType` from file content (it defaults
   to `application/octet-stream` and is otherwise caller-supplied), so this is a documentation-level
   reservation, not a new runtime guard: a future write path must not be able to set a regular file's
   `MediaType` to this sentinel.

This ADR accepts the convention and the resolver behavior it relies on; it does **not** accept the
manifest/seed changes themselves as already done — those are tracked as `INT-006` through `INT-010` in
[`../Global-FileView-And-MessagingSystem/integrationPlan.md`](../old/Global-FileView-And-MessagingSystem/integrationPlan.md).

## Consequences

- `FileView`'s `NewWindow` folder activation (`FV-009`) becomes meaningful (resolves to `FileExplorer`
  instead of `NoHandler`) only once `INT-008`/`INT-009` land; until then it correctly resolves to
  `NoHandler`/`ChooserRequired`, which is documented as acceptable intermediate behavior in
  `integrationPlan.md`, not a bug to work around early.
- Establishes a general `inode/*` sentinel pattern that could later extend to other pseudo-types (e.g. a
  distinct handler for symbolic links) without further design work — this ADR accepts only
  `inode/directory`, not the general pattern as a standing policy.
- `Platform/HackerOs.Platform.Core/Intents/FileAssociationResolver.cs` gains its first regression test
  exercising directory resolution (`INT-007`) — a codepath that has existed since `P1-APP-009` but was
  never exercised for a directory target.
- [`../app-intents-and-associations.md`](../old/app-intents-and-associations.md) must document the
  `inode/directory` convention in the same change that implements it (`INT-010`), per this repo's
  documentation-maintenance rule.

## References

- [`../Global-FileView-And-MessagingSystem/FileViewControl.md`](../old/Global-FileView-And-MessagingSystem/FileViewControl.md#folder-double-click-behavior) —
  the `NewWindow` activation mode this convention serves.
- [`../Global-FileView-And-MessagingSystem/integrationPlan.md`](../old/Global-FileView-And-MessagingSystem/integrationPlan.md) —
  Phase 5 (`INT-006`–`INT-010`).
- [`../app-intents-and-associations.md`](../old/app-intents-and-associations.md) — the existing intent
  dispatch/association pipeline this convention reuses unchanged.
- `Shared/HackerOs.App.Abstractions/AppManifest.cs` (`FileHandlerManifest`) and
  `Platform/HackerOs.Platform.Core/Intents/FileAssociationResolver.cs` — the existing contracts/logic this
  ADR confirms require no structural change.
- ADR 0037: `FileView` as the Canonical File-Listing Control — the consumer this association serves.
