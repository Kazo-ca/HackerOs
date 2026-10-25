# Virtual Filesystem

## Purpose

Provide one Linux-like, browser-independent filesystem contract for HackerOS
apps, terminal commands, settings projections, file dialogs, in-memory tests, and
future IndexedDB persistence.

## Status

The architecture was accepted on 2026-08-01 in
`docs/adr/0008-virtual-filesystem-model.md`. The Phase 1 contracts, streaming,
authorization, routing, traversal, in-memory implementation, clean-profile seed,
canonical settings mount, and assembled contract suite are complete.

## Architecture

The proposed model separates four responsibilities:

1. `VirtualPath` canonicalizes the global, ordinal case-sensitive namespace.
2. A filesystem router resolves the most specific mount or projection.
3. Providers store immutable entry identities, metadata, directory links, and
   separate content descriptors behind transactions.
4. App-scoped gateways apply capability, authority, identity, and filesystem
   mode checks to every operation.

Regular files, directories, and symbolic links are first-class entry kinds.
Renames preserve entry identity, copies allocate new identities, and content is
streamed rather than required in one in-memory buffer.

Settings paths are canonical projections. `/etc/hackeros/*` routes to the
settings service before ordinary storage, so editing a settings file and using a
settings UI observes one document and one revision.

## Implemented metadata contracts

`HackerOs.Simulation.Abstractions.FileSystem` now provides:

- `FileSystemEntryId`, a canonical opaque 128-bit identity independent of path;
- `FileSystemEntryName`, which applies Unicode Form C and the 255-byte limit;
- `FileSystemAccess` and `FileSystemPermissions`, which model owner/group/other
  access and convert to or from a nine-bit mode;
- `FileSystemTimestamps`, which requires deterministic UTC values;
- immutable `FileMetadata`, `DirectoryMetadata`, and `SymbolicLinkMetadata`
  records with positive optimistic revisions; and
- `FileSystemDirectoryEntry`, which relates a parent/name to a child identity so
  rename does not replace file identity.

The ID type does not generate randomness. Future repositories receive an
injectable ID generator, allowing domain tests to remain deterministic.

## Implemented operations and errors

Every required operation now has an immutable request contract: read, enumerate,
create, write, move, copy, delete, stat, and permission replacement. Mutation
requests carry the entry and parent revisions needed for optimistic conflict
detection. Move and copy reject identical source/destination paths before a
provider runs.

`FileSystemResult<T>` returns either a value or `FileSystemError`. Errors identify
the failed operation and use explicit stable `FileSystemErrorCode` values for:

- invalid paths and names;
- missing, existing, wrong-kind, and non-empty entries;
- mode, capability, and authority denial;
- revision conflicts;
- symbolic-link loops, hop limits, and root containment;
- cross-mount, protected, and unsupported operations;
- cancellation; and
- unavailable or failed providers.

Directory enumeration snapshots require unique ordinal name ordering and copy
their input collection. Transaction results distinguish committed, rejected, and
cancelled outcomes. Rejected and cancelled transactions expose no affected entry
IDs, preserving the all-or-nothing contract.

## Implemented content streaming

`FileSystemContentDescriptor` distinguishes opaque binary bytes from encoded
text. Text descriptors carry an encoding web name, defaulting to UTF-8; binary
descriptors never imply an encoding.

Writes consume `IFileSystemContentSource`, which opens a fresh readable stream
owned by the filesystem operation. Reads return
`FileSystemContentReadHandle`, which pairs the matching metadata revision with a
readable stream and owns asynchronous disposal. Streams may be non-seekable and
deliver arbitrarily small chunks. Neither contract exposes or requires a complete
byte array.

## Implemented authorization boundary

`FileSystemAuthorizationContext` combines trusted `AppOperationContext`, exact
group membership, deterministic evaluation time, and an optional selected
resource handle. `FileSystemAuthorizer` evaluates:

1. minimum `System > Administrator > User` authority;
2. the exact required capability or a valid selected-resource delegation; and
3. owner, exact-group, or other permission bits.

System authority never skips the capability check. Selected handles are bound to
one app, user, path subtree, operation set, policy revision, issue time, expiry,
and revocation state. A selected handle can replace a broad capability only for
its delegated path and operation; normal mode checks still apply.

## Implemented provider routing

`IFileSystemProvider` exposes the same read, enumerate, mutation, stat, and
permission operations for ordinary storage and projections. Mutations return an
atomic transaction result plus the current entry snapshot when one remains.

`FileSystemMountRouter` requires one `/` provider and accepts more-specific
mounts such as `/etc/hackeros`. It resolves the longest matching mount on a full
path-segment boundary, rejects duplicate mount paths, and preserves the canonical
absolute path when calling a provider. This makes projected settings authoritative
without storing a shadow ordinary file.

## Implemented traversal

`VirtualPath` now normalizes every segment to Unicode Form C and enforces 255
UTF-8 bytes per segment and 4,096 UTF-8 bytes per canonical path.
`FileSystemPathResolver` walks providers through the mount router and:

- resolves absolute and parent-relative symbolic-link targets;
- follows at most 40 links and detects repeated link entry IDs;
- rejects target expansion above `/`;
- re-routes each expanded path through the global mount table;
- reports non-directory intermediate entries; and
- preserves the final link when an operation requests no-follow behavior.

Since ADR 0090, when every prefix of a path is on one mount whose provider
implements `IFileSystemPathWalkProvider` (IndexedDB), the resolver gets all the
components in one request instead of one `lstat` per prefix, then applies the
same rules (see [`../filesystem-path-lookup.md`](../filesystem-path-lookup.md)).

Deleting, moving, or copying a symbolic link therefore acts on the link entry,
not its target. `~` and other presentation aliases are deliberately rejected by
`VirtualPath`; shell/dialog code expands them before filesystem parsing.

## Implemented in-memory filesystem

`InMemoryFileSystemRepository` stores entry records by immutable ID and keeps
directory path links separately. Rename rewrites links while preserving IDs;
copy allocates new IDs. It provides streamed content, deterministic ordinal
enumeration, optimistic revisions, atomic subtree move/copy/delete, permission
replacement, cancellation before commit, and protected root behavior.

`FileSystemService` resolves links, routes mounts, authorizes the target or
mutation parent, and then dispatches to one provider. It selects private, home,
or system capabilities by canonical path, requires Administrator authority for
protected system writes, prevents deleting protected mount roots, and rejects
cross-provider move/copy before either provider mutates.

The repository accepts injected entry-ID, transaction-ID, and `TimeProvider`
sources so tests do not depend on randomness or wall-clock time.

## Clean-profile seed

`FileSystemSeeder` creates these system directories exactly once:

```text
/
/bin
/etc
/home
/tmp
/var
/var/log
```

For each user it creates `/home/{user}` plus `Desktop`, `Documents`, `Downloads`,
`.config`, `.config/hackeros`, and `.config/apps`. System directories are owned
by `system`; user directories use the requested user and primary group. Repeated
seeding preserves IDs and revisions, and provisioning another user does not
rewrite existing homes.

Kernel provisioning uses an explicit audited System operation. System authority
may bypass mode bits for this OS-owned work, but exact capabilities remain
mandatory.

## Canonical settings mount

`SettingsFileSystemProvider` mounts canonical settings definitions under
`/etc/hackeros`. It exposes synthetic directories and streamed UTF-8 files with
stable path-derived IDs. File metadata uses the canonical settings revision, so
filesystem and direct settings operations participate in one conflict sequence.

Writes decode UTF-8 and delegate the complete candidate to
`ISettingsFileProjection`; syntax/schema validation, authority, document
capabilities, audit events, and atomic replacement remain owned by the settings
service. Invalid content and stale revisions leave the prior document untouched.
Projected files are `system:administrators` with `0664` mode, while the mounted
service still requires Administrator/System authority and exact capabilities for
writes. More-specific projection routing prevents ordinary records from
shadowing canonical documents.

## Proposed behavior

- Paths use `/`, Unicode Form C, ordinal case-sensitive comparison, 255 UTF-8
  bytes per segment, and 4,096 UTF-8 bytes per absolute path.
- Owner/group/other read, write, and execute bits provide Linux-inspired mode
  checks alongside exact app capabilities and trusted authority.
- Symbolic links support absolute and relative targets, a 40-link traversal
  limit, loop detection, dangling links, and root containment.
- `~` and other presentation aliases expand outside the filesystem; arbitrary
  dynamic filesystem alias callbacks are not supported.
- Mutations use optimistic revisions and atomic provider transactions.
- Same-provider subtree move/copy is atomic. Cross-provider move is rejected;
  cross-provider copy commits only the destination from a stable source snapshot.
- Recursive delete is explicit, atomic, does not follow links or cross mounts,
  and cannot remove root or mount roots.

## Remaining validation

Task `P1-FS-010` supplies the shared end-to-end filesystem contract suite.

## Contract validation

The assembled service contract suite covers:

- large streamed binary create/write/read/delete round trips;
- owner/group/other permission denial and authorized mode changes;
- relative symbolic-link reads, no-follow delete, and loop errors;
- atomic subtree move/copy with preserved move IDs and fresh copy IDs;
- optimistic revision conflicts and cancellation before commit;
- canonical settings precedence over an ordinary shadow file and one shared
  revision sequence; and
- idempotent clean-profile initialization.

Focused contract tests remain browser-free and use deterministic IDs, time, and
in-memory providers. The required complete gate is:

```powershell
dotnet test HackerOs.sln --no-restore
```

## Exclusions

This Phase 1 model does not include IndexedDB, native browser files, sync,
encryption, hard links, POSIX ACLs, file locking, gameplay remote filesystems,
or app UI. Chunk sizing, maximum file size, hashing, deduplication, and garbage
collection remain deferred to D-009.

## Key decisions

- Paths are names, not persistent identity.
- Metadata and content storage are separate.
- Settings projections take precedence over ordinary storage.
- Authorization combines app policy with filesystem mode checks.
- Provider boundaries are explicit transaction boundaries.
- The legacy TypeScript filesystem is a behavior reference only.

## Task list

- [x] Draft ADR 0008 with the complete filesystem model.
- [x] Obtain Architecture + product approval for D-001.
- [x] Define immutable entry IDs, names, permissions, timestamps, metadata, and
  directory links.
- [x] Define filesystem operation contracts, transaction outcomes, and stable
  errors.
- [x] Define binary/text descriptors and owned streaming content contracts.
- [x] Define trusted authorization inputs, selected handles, and policy results.
- [x] Define provider and longest-segment mount routing contracts.
- [x] Define path normalization, bounded link traversal, and no-follow semantics.
- [x] Implement the deterministic in-memory repository and mounted service.
- [x] Seed the clean-profile system and per-user directory layout idempotently.
- [x] Mount canonical settings projections with shared revisions.
- [x] Add assembled CRUD, binary, permission, traversal, projection, atomicity,
  conflict, cancellation, and seed contract tests.