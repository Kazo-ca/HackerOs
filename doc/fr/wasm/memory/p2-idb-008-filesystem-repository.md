# P2-IDB-008: Filesystem Repository Transactions - Contract & Implementation Analysis

**Task:** Filesystem transactions, streams/chunks, projection routing, clean-profile seed idempotence.

**Status:** Phase 2, ready for implementation slice.

---

## 1. Owning Files, Types, and Method Signatures

### Contracts (Abstractions)
**Location:** `wasm2/HackerOs/Shared/HackerOs.Simulation.Abstractions/FileSystem/`

- **IFileSystemProvider** — Core provider interface
  - All mutation methods return: `ValueTask<FileSystemMutationResult>`
  - Methods: `ReadAsync`, `EnumerateAsync`, `CreateAsync`, `WriteAsync`, `MoveAsync`, `CopyAsync`, `DeleteAsync`, `StatAsync`, `SetPermissionsAsync`

- **FileSystemMutationResult** (record)
  - Properties: `Transaction: FileSystemTransactionResult`, `Entry: FileSystemEntrySnapshot?`
  - Property: `Succeeded => Transaction.Status == FileSystemTransactionStatus.Committed`

- **FileSystemTransactionResult** (record)
  - Properties: `TransactionId: Guid`, `Status: FileSystemTransactionStatus`, `Error: FileSystemError?`
  - Static methods: `Committed(Guid id, FileSystemError? error)`, `Rejected(Guid id, FileSystemError error)`, `Cancelled(Guid id)`

- **FileSystemTransactionStatus** (enum)
  - `Committed = 1`, `Rejected = 2`, `Cancelled = 3`

- **FileSystemEntryMetadata** (abstract record)
  - Shared: `Id: FileSystemEntryId`, `OwnerId`, `GroupId`, `Permissions`, `Timestamps`, `Revision: long`
  - Sealed subclasses: `FileSystemFileMetadata`, `FileSystemDirectoryMetadata`, `FileSystemSymbolicLinkMetadata`

- **FileSystemContentSource** (interface)
  - `Descriptor: FileSystemContentDescriptor` (kind, mediaType, encodingName)
  - `Length: long?`
  - `OpenReadAsync(CancellationToken): ValueTask<Stream>`

- **FileSystemContentReadHandle** (class)
  - Properties: `Entry: FileSystemEntrySnapshot`, `Descriptor`, `Content: Stream`
  - IAsyncDisposable

### In-Memory Implementation
**Location:** `wasm2/HackerOs/Platform/HackerOs.Platform.Core/FileSystem/`

- **InMemoryFileSystemRepository** — Full provider implementation
  - Lock-based, stores entries in `Dictionary<FileSystemEntryId, EntryRecord>` + `Dictionary<string, FileSystemEntryId>` for path lookup
  - Generates new IDs via `_entryIdFactory`, transaction IDs via `_transactionIdFactory`
  - Uses `TimeProvider` for timestamps

- **FileSystemSeeder** — Idempotent directory provisioning
  - Method: `SeedAsync(userId, primaryGroupId, cancellationToken)`
  - Pattern: `EnsureDirectoryAsync` checks if path exists before creating

### Schema & Transaction Boundaries
**Location:** `wasm2/HackerOs/Infrastructure/HackerOs.Infrastructure.Browser/Schema/`

- **HackerOsIndexedDbSchema** (static class)
  - Constants: DatabaseName = "hackeros", CurrentVersion = 1
  - Object stores:
    - `fsEntries`: `[id]` ← entry metadata
    - `fsLinks`: `[parentId, name]` ← directory structure
    - `fsContent`: `[contentHash, chunkIndex]` ← deduplicated chunks
  - **Transaction boundaries:**
    - `FileSystemMetadataMutation`: [fsEntries, fsLinks] — atomic metadata + link changes
    - `FileSystemContentWrite`: [fsContent] — independent content chunks

- **FileContentStoragePolicy** (record)
  - Properties: MaxFileSizeBytes (16 MiB), MaxChunkSizeBytes (256 KiB), ContentHashAlgorithm ("SHA-256"), DeduplicateChunks (true), OrphanRetention (30 days)
  - Method: `RequiresChunking(long fileSizeBytes): bool`

### Interop Adapter
**Location:** `wasm2/HackerOs/Infrastructure/HackerOs.Infrastructure.Browser/Interop/`

- **IndexedDbInteropAdapter** (internal sealed class)
  - Methods:
    - `OpenAsync(IndexedDbMigrationPlan, CancellationToken): ValueTask`
    - `ExecuteAsync(boundaryName, mode, operations, CancellationToken): ValueTask<IReadOnlyList<JsonElement>>`
  - Validates that operations stay within declared boundary before JS interop

---

## 2. Smallest Coherent Implementation Slice

**Create:** `IndexedDbFileSystemRepository.cs` in `Infrastructure/HackerOs.Infrastructure.Browser/FileSystem/`

### Phase 1: Serialization Layer
1. **FileSystemEntryMetadata ↔ JsonElement** converters (per entry kind)
2. **FileSystemDirectoryEntry ↔ JsonElement** converters
3. **Content chunk hashing** — compute SHA-256, determine chunk boundaries per FileContentStoragePolicy

### Phase 2: Core Read Operation
```csharp
public async ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(...)
{
  // 1. Transaction: Read fsEntries[id] → entry metadata
  // 2. Validate authorization
  // 3. Stream fsContent chunks [contentHash, 0..N] as MemoryStream
  // 4. Return FileSystemContentReadHandle with entry snapshot
}
```

### Phase 3: Create Operation (Single Metadata Transaction)
```csharp
public ValueTask<FileSystemMutationResult> CreateAsync(...)
{
  // 1. Transaction: FileSystemMetadataMutation
  //    - PUT fsEntries[newId] with entry metadata
  //    - PUT fsLinks[parentId, name] → newId
  //    - Increment parent directory revision
  // 2. Return new entry snapshot
}
```
**Idempotence:** Check NotFound before creating; reject AlreadyExists (matches seeder pattern)

### Phase 4: Write Operation (Two-Phase)
```csharp
public async ValueTask<FileSystemMutationResult> WriteAsync(...)
{
  // Phase 1: Content
  // - Stream IFileSystemContentSource to buffer (or upload in chunks)
  // - Compute content hash, determine chunk split
  // - Transaction: FileSystemContentWrite
  //   - PUT fsContent[hash, 0..N] for each chunk (if deduplicateChunks, check exists first)
  
  // Phase 2: Metadata
  // - Transaction: FileSystemMetadataMutation
  //   - UPDATE fsEntries[id] with new content hash, size, timestamps, revision++
  //   - Touch parent directory revision
  
  // Ordering concern: If Phase 1 succeeds but Phase 2 fails,
  // orphaned content chunks are cleaned up by retention policy (P2-IDB-010)
}
```

### Phase 5: Clean-Profile Seed Idempotence
```csharp
private async ValueTask EnsureDirectoryAsync(string pathValue, ushort mode, ...)
{
  // Already implemented in FileSystemSeeder.cs:
  // 1. Stat path → if exists and is Directory, return (idempotent)
  // 2. If error != NotFound, throw
  // 3. Otherwise create
}
```

---

## 3. Reusable Contract Tests

Located in `Tests/HackerOs.Platform.Core.Tests/FileSystem/`:

- **FileSystemContractSuiteTests.cs**
  - `Crud_round_trip_streams_large_binary_content` — tests 192 KiB chunks
  - `Mode_permissions_deny_other_user_until_owner_grants_read` — authorization
  - `Relative_symlink_reads_target_but_delete_removes_only_link` — symlink semantics

- **InMemoryFileSystemRepositoryTests.cs**
  - `Create_write_and_read_round_trip_streamed_content` — basic CRUD
  - `Enumeration_is_unique_and_ordinal_sorted` — directory ordering
  - `Stale_write_revision_rejects_without_changing_content` — revision conflict semantics

- **FileSystemSeederTests.cs**
  - `Seed_is_idempotent` — running seed twice doesn't fail

**Pattern:** All use a `Fixture` helper that provides `FileSystemService` + multiple `FileSystemAuthorizationContext` instances (alice, bob). Can be adapted to run against IndexedDB implementation by substituting the repository.

---

## 4. Blockers or Architectural Mismatches

**None identified.** The architecture cleanly separates:
- ✅ Contracts (abstractions) from implementations
- ✅ Metadata mutations (FileSystemMetadataMutation) from content writes (FileSystemContentWrite)
- ✅ Authorization from storage
- ✅ Streaming (IFileSystemContentSource) from in-memory buffering
- ✅ Seeding logic (FileSystemSeeder) from mutation logic

**Notable:** The schema comments explicitly state:
> "Write a file's content independently of its metadata transaction; ordering between the two is a repository-level concern (P2-IDB-008), not this boundary's."

This means the implementation must handle:
- Content write succeeds, metadata fails → orphaned chunks (cleaned by retention, P2-IDB-010)
- Metadata fails before content write → skip content entirely
- Metadata succeeds, content fails → file exists but is "empty" or incorrect hash (detected on read/verify)

These are transactional consistency concerns, not blockers; they're already designed into the separation.

---

## References
- `docs/virtual-filesystem.md` — filesystem design
- `docs/browser-storage.md` — IndexedDB schema and boundaries
- `Infrastructure/HackerOs.Infrastructure.Browser/Schema/HackerOsIndexedDbSchema.cs` — schema declaration
- `Platform/HackerOs.Platform.Core/FileSystem/InMemoryFileSystemRepository.cs` — in-memory reference
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/` — contract suites
