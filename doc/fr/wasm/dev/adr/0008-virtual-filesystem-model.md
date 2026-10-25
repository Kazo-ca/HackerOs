# ADR 0008: Virtual Filesystem Model

## Status

Accepted on 2026-08-01.

## Context

HackerOS needs one browser-independent filesystem model for the in-memory
reference implementation, IndexedDB persistence, settings projections, terminal
commands, file dialogs, and later synchronization. The existing `VirtualPath`
contract canonicalizes absolute Linux-style paths, and ADR 0004 requires settings
files to remain projections over canonical settings records.

The legacy `src/core/filesystem.ts` demonstrates useful user behavior, including
Linux-like paths, text and binary files, directories, aliases, symbolic links,
metadata, and initial directory creation. It is a behavioral reference only. Its
path-keyed records, dynamic alias callbacks, and non-atomic multi-entry operations
are not implementation dependencies for v3.

## Decision

### Entries and identity

Every stored entry has an immutable opaque 128-bit `FileSystemEntryId`. ID
generation is injectable so tests are deterministic. IDs have no ordering or
path semantics and are never exposed as browser storage keys through the App SDK.

Directories relate a parent entry ID and one name to a child entry ID. The root
has a well-known ID and no parent. Moving or renaming an entry preserves its ID;
copying creates new IDs for the complete copied subtree. Hard links are excluded
from the initial model.

Entries have one of three kinds:

- regular file;
- directory; or
- symbolic link.

Common metadata contains owner ID, group ID, Unix-like permission mode, creation
time, content-modification time, metadata-change time, and an optimistic
revision. File logical size is the content byte length, symbolic-link size is the
UTF-8 target length, and directory logical size is zero. Access time is omitted
because browser persistence would make reads write-heavy and nondeterministic.
All timestamps are UTC values supplied by the platform clock.

### Names and paths

The virtual filesystem is case-sensitive and compares canonical names with
ordinal semantics. Names are normalized to Unicode Normalization Form C before
comparison and persistence. A normalized segment is limited to 255 UTF-8 bytes,
and a canonical absolute path is limited to 4,096 UTF-8 bytes.

Empty names, `.`, `..`, nulls, path separators, control characters, invalid
Unicode, and names exceeding those limits are rejected. `/` is the only root
representation. Backslashes are not separators. These rules extend the existing
`VirtualPath` contract and will be enforced centrally there.

### Permissions and authorization

Each entry stores owner/group/other read, write, and execute bits. Set-user-ID,
set-group-ID, sticky bits, POSIX ACLs, and platform-native permissions are
excluded from the first model.

Permission meaning is Linux-inspired rather than a claim of POSIX compliance:

- directory read permits enumeration;
- directory execute permits traversal and metadata lookup;
- directory write plus execute permits child creation, removal, and rename;
- file read permits content reads;
- file write permits content replacement; and
- file execute is retained for future command/script policy but does not itself
  launch code.

Every operation evaluates both filesystem mode and trusted
`AppOperationContext` capability/authority policy. System authority never
bypasses an exact capability check. The filesystem does not accept owner,
authority, or grant claims supplied by app code.

### Content

File metadata and content are separate records. Files reference an immutable
content descriptor; a successful write atomically swaps that descriptor and
updates size, timestamps, and revision. Readers and writers use asynchronous
binary streams so the public contract never requires a complete file in memory.
Text encoding is an application concern except for typed projections that define
their own encoding, such as UTF-8 JSON settings documents.

Chunk size, maximum file size, hashing, deduplication, and content garbage
collection are deferred to D-009. The headless contracts must permit chunked
implementations without exposing chunk IDs.

### Symbolic links and aliases

A symbolic link is a persisted entry whose target is an absolute or relative
virtual path. Relative targets resolve from the link's containing directory.
Normal traversal follows links, while stat and delete operations can explicitly
act on the link itself. Deleting, moving, or copying a link never follows its
target. Recursive deletion never crosses a symbolic link.

Resolution is contained within the virtual root, detects repeated link IDs, and
fails after 40 traversals. Dangling links are valid entries but fail operations
that require the target. Link resolution re-enters mount routing after every
target expansion.

Presentation aliases such as `~` are not filesystem entries and are not accepted
by `VirtualPath`. A caller-aware shell or dialog path resolver expands them
before canonical path parsing. v3 does not carry forward arbitrary dynamic alias
callbacks inside the filesystem. Persistent path indirection uses symbolic links;
provider indirection uses mounts.

### Mounts and settings projections

One filesystem router owns the global namespace. It selects a mount or projection
using the longest matching canonical path on a complete segment boundary. Equal
specificity is a configuration error. Mount registrations are trusted boot-time
configuration and cannot be changed by app code.

The ordinary repository owns `/` and any path not claimed by a more specific
mount. A mounted provider owns its mount point and descendants, so ordinary
entries cannot shadow projected paths. Synthetic ancestor directories may expose
mounted descendants during enumeration.

Settings projections, including `/etc/hackeros/*`, delegate to the canonical
settings service before ordinary storage as required by ADR 0004. They do not
create duplicate file content records. Providers return the same stable result
and error contracts as ordinary storage.

### Transactions and revisions

Mutating service calls execute in an explicit transaction with snapshot reads,
read-your-writes behavior, optimistic revision preconditions, and an atomic
commit. The in-memory implementation serializes commits deterministically.
IndexedDB later maps the same boundary to one database transaction where its
schema permits.

A committed create, write, metadata change, move, copy, or delete is fully
visible; a conflict, cancellation before commit, validation failure, or provider
failure leaves no partial mutation. Cancellation observed after a successful
commit does not rewrite the result as cancelled.

Move and recursive copy within one provider are atomic for the whole subtree.
Cross-provider move is rejected with a stable cross-mount error because the
browser has no distributed transaction. Cross-provider copy reads one source
snapshot and atomically commits the destination; the source is unchanged. A
provider may reject cross-provider copy when it cannot provide a stable snapshot.

Directory revisions change when their immediate children change. Expected entry
and parent revisions detect concurrent content, metadata, and namespace changes.
Transaction events are published only after commit and share one correlation ID.

### Delete behavior

Deleting a file or symbolic link removes that entry. Deleting a directory
requires it to be empty unless recursive deletion is explicitly requested.
Recursive deletion is all-or-nothing, does not follow symbolic links or cross
mount boundaries, and cannot delete `/` or a mount root. Protected projections
may reject deletion even when the parent mode would otherwise allow it.

## Consequences

- Rename preserves stable identity while copy has independent identity and
  revisions.
- Case behavior and path limits are deterministic across browsers and hosts.
- File content can become chunked in IndexedDB without changing SDK contracts.
- Canonical settings remain authoritative and cannot be shadowed by ordinary
  files.
- Shell aliases are separated from security-sensitive filesystem traversal.
- Cross-mount moves are explicit failures instead of partially completed copies
  followed by failed deletes.
- The initial model is Linux-like but intentionally does not promise full POSIX
  behavior, hard links, ACLs, or native filesystem interoperability.

## Implementation constraints

The Phase 1 filesystem contracts and in-memory implementation must encode these
rules and provide stable errors for invalid paths/names, missing entries, type
mismatches, conflicts, permission/capability denial, link loops, cross-mount
operations, non-empty directories, protected roots, cancellation, and provider
failures.

Contract tests must cover identity preservation, copy identity, path limits,
ordinal case sensitivity, permission traversal, link loops and dangling links,
projection precedence, transaction rollback, revision conflicts, atomic subtree
operations, cancellation, and recursive delete boundaries.

The first two contract slices implement opaque entry IDs, normalized directory
names, owner/group/other permission triples, UTC timestamps, immutable metadata,
directory links, validated operation requests, provider-neutral errors, generic
operation results, and all-or-nothing transaction outcomes. Content streams,
authorization, repositories, traversal, and routing remain in later `P1-FS`
tasks.

`P1-FS-003` represents content with owned asynchronous `Stream` instances.
Binary and text files share byte streaming; text descriptors declare a media type
and encoding web name so consumers can wrap the stream in a streaming decoder.
Write sources open a fresh caller-independent stream, and read handles dispose
their owned stream. Seek support is never required.

`P1-FS-004` evaluates trusted authorization inputs in a fixed order: minimum
authority, exact broad capability or a matching selected-resource handle, then
owner/group/other mode bits. Explicit System operations still require the exact
capability. Selected handles are app-, user-, path-, access-, policy-revision-,
and lifetime-bound inputs created by trusted platform code; they never bypass
filesystem mode checks.

`P1-FS-005` expresses storage and projections through one
`IFileSystemProvider` contract. A mount router always registers `/` and selects
the longest canonical mount path that matches on a complete segment boundary.
Duplicate paths are configuration errors. Providers receive canonical absolute
paths, preserving one namespace while allowing settings projections to shadow
ordinary storage.

`P1-FS-006` normalizes path segments to Unicode Form C and enforces the 255-byte
segment and 4,096-byte path limits in `VirtualPath`. Resolution follows at most
40 distinct symbolic links, detects repeated entry IDs, rejects root escape,
re-enters mount routing after expansion, and supports no-follow behavior for
operations that act on the link itself. Presentation aliases remain outside the
filesystem and must expand before `VirtualPath` parsing.

`P1-FS-007` separates `InMemoryFileSystemRepository`, which owns stable entries,
directory path links, content, revisions, and atomic provider mutations, from
`FileSystemService`, which owns traversal, mount selection, capability/authority/
mode authorization, protected mount roots, and cross-provider rejection. Writes
stream into a candidate before locking, then recheck revisions immediately before
commit. Failed validation and cancellation before commit leave storage unchanged.

`P1-FS-008` seeds required directories through the authorized service using
explicit kernel operation contexts. System authority may bypass owner/group/other
mode bits for OS-owned provisioning, matching root-like maintenance behavior, but
only after the minimum-authority and exact-capability checks pass. Repeated seed
runs stat before create and preserve existing IDs and revisions.

`P1-FS-009` adapts canonical settings through `IFileSystemProvider` rather than
copying records. Projected files derive stable IDs from canonical paths, expose
the settings revision unchanged, stream UTF-8 JSON, and delegate writes to
`ISettingsFileProjection`. Protected files are owned by `system`, grouped for
Administrators, and still require service authority plus exact filesystem and
document capabilities. Ordinary shadow entries remain unreachable behind the
more-specific mount.

`P1-FS-010` validates the assembled mounted service, not only individual
classes. The suite covers large streamed binary CRUD, permission transitions,
relative links, delete-no-follow, loops, atomic subtree move/copy, optimistic
conflicts, cancellation rollback, projection shadow precedence/shared revisions,
and clean-profile idempotence.

## References

- ADR 0002: Authority Comes from Trusted Policy
- ADR 0003: Exact Capability Matching
- ADR 0004: Settings Files Are Canonical Projections
- `docs/settings-system.md`
- `doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md`
- `src/core/filesystem.ts` (behavioral reference only)