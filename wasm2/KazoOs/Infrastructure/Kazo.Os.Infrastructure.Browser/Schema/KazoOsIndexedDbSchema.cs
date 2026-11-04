namespace Kazo.Os.Infrastructure.Browser.Schema;

/// <summary>
/// Declares the canonical Kazo OS browser-storage IndexedDB database: its name, schema version,
/// object stores, and cross-store transaction boundaries, per <c>P2-IDB-002</c> and ADR 0015
/// (<c>D-008</c>).
/// </summary>
/// <remarks>
/// This class only declares the schema; it opens no connection and runs no transaction. The JS
/// interop module and C# repositories that read this declaration are implemented by
/// <c>P2-IDB-004</c> and <c>P2-IDB-005</c>. File-content chunking, hashing, deduplication, and
/// retention are defined by <see cref="FileContentStoragePolicy"/> per <c>P2-IDB-003</c>
/// (<c>D-009</c>).
/// </remarks>
public static class KazoOsIndexedDbSchema
{
    /// <summary>Gets the single IndexedDB database name used by Kazo OS.</summary>
    public const string DatabaseName = "hackeros";

    /// <summary>Gets the current schema version passed to <c>indexedDB.open</c>.</summary>
    /// <remarks>
    /// Incrementing this value is the only way to add/remove/change an object store or index;
    /// migration steps for each prior version are implemented by <c>P2-IDB-006</c>.
    /// </remarks>
    public const int CurrentVersion = 4;

    /// <summary>Object store persisting one record per <c>LocalUser</c> account.</summary>
    public const string UserStoreName = "users";

    /// <summary>Object store persisting one record per local user group.</summary>
    public const string GroupStoreName = "groups";

    /// <summary>Object store persisting one record per active/recent local session.</summary>
    public const string SessionStoreName = "sessions";

    /// <summary>Object store persisting one record per canonical settings document.</summary>
    public const string SettingsStoreName = "settings";

    /// <summary>Object store persisting filesystem entry metadata (files, directories, symlinks).</summary>
    public const string FileSystemEntryStoreName = "fsEntries";

    /// <summary>Object store persisting directory-to-child-name links, separate from entry metadata.</summary>
    public const string FileSystemLinkStoreName = "fsLinks";

    /// <summary>Object store persisting file content chunks, separate from entry metadata.</summary>
    public const string FileContentStoreName = "fsContent";

    /// <summary>Object store persisting one record per catalog (installed app) entry.</summary>
    public const string CatalogStoreName = "catalog";

    /// <summary>Object store persisting one record per immutable capability grant.</summary>
    public const string GrantStoreName = "grants";

    /// <summary>Object store persisting append-only security/policy audit entries.</summary>
    public const string AuditStoreName = "audit";

    /// <summary>Object store persisting bounded structured diagnostic entries.</summary>
    public const string DiagnosticsStoreName = "diagnostics";

    /// <summary>
    /// Object store persisting small local bookkeeping values (e.g. the current policy revision,
    /// this installation's ID). This is local revision/consistency bookkeeping only — it is not
    /// multi-device network sync, which remains excluded per section 1.2 and Phase 5.
    /// </summary>
    public const string LocalBookkeepingStoreName = "syncMetadata";

    /// <summary>
    /// Object store persisting the single per-device optional-server connection record (ADR 0028):
    /// account ID, device ID, server base URL, and opaque refresh token. Not the local-only
    /// <see cref="LocalBookkeepingStoreName"/> store, and never exported through the ordinary
    /// settings `.config` projection.
    /// </summary>
    public const string ServerConnectionStoreName = "serverConnection";

    /// <summary>
    /// Object store persisting one opaque pull cursor per sync domain (ADR 0029). Domain-agnostic —
    /// reused by every sync-domain pass (Settings, FileSystem, Grants, AppCatalog, FileAssociations),
    /// not specific to Settings.
    /// </summary>
    public const string SyncCursorStoreName = "syncCursors";

    /// <summary>
    /// Object store persisting the last-synced sync-<c>Revision</c>/<c>ContentHash</c> per
    /// <c>(domain, recordId)</c> pair (ADR 0029). Independent of any document's own local
    /// optimistic-concurrency revision; domain-agnostic like <see cref="SyncCursorStoreName"/>.
    /// </summary>
    public const string SyncRecordStateStoreName = "syncRecordState";

    /// <summary>Gets every object store declared for <see cref="CurrentVersion"/>, in creation order.</summary>
    public static IReadOnlyList<IndexedDbObjectStoreDefinition> ObjectStores { get; } =
    [
        new(
            UserStoreName,
            keyPath: ["id"],
            autoIncrement: false,
            indexes: [new IndexedDbIndexDefinition("loginName", ["loginName"], unique: true)],
            purpose: "One record per LocalUser account, keyed by its opaque LocalUserId."),

        new(
            GroupStoreName,
            keyPath: ["id"],
            autoIncrement: false,
            indexes: [new IndexedDbIndexDefinition("name", ["name"], unique: true)],
            purpose: "One record per LocalGroup, keyed by its opaque LocalGroupId."),

        new(
            SessionStoreName,
            keyPath: ["id"],
            autoIncrement: false,
            indexes: [new IndexedDbIndexDefinition("userId", ["userId"])],
            purpose: "One record per local session, keyed by its opaque session ID."),

        new(
            SettingsStoreName,
            keyPath: ["id"],
            autoIncrement: false,
            indexes:
            [
                new IndexedDbIndexDefinition("scope", ["scope"]),
                new IndexedDbIndexDefinition("scopeAndApp", ["scope", "appId"])
            ],
            purpose:
                "One record per canonical settings document, keyed by the SettingsDocumentKey's " +
                "delimited composite string (scope|appId|userId|installationId|documentId)."),

        new(
            FileSystemEntryStoreName,
            keyPath: ["id"],
            autoIncrement: false,
            indexes:
            [
                new IndexedDbIndexDefinition("ownerId", ["ownerId"]),
                new IndexedDbIndexDefinition("contentHash", ["contentHash"])
            ],
            purpose:
                "One record per FileSystemEntryMetadata (file, directory, or symlink), keyed by " +
                "its stable FileSystemEntryId. Directory structure is not stored here."),

        new(
            FileSystemLinkStoreName,
            keyPath: ["parentId", "name"],
            autoIncrement: false,
            indexes:
            [
                new IndexedDbIndexDefinition("parentId", ["parentId"]),
                new IndexedDbIndexDefinition("entryId", ["entryId"])
            ],
            purpose:
                "One record per FileSystemDirectoryEntry, keyed by the compound (parentId, name) " +
                "pair; parentId enumerates immediate children and entryId supports reverse " +
                "lookups during move/rename/delete."),

        new(
            FileContentStoreName,
            keyPath: ["contentHash", "chunkIndex"],
            autoIncrement: false,
            indexes: [new IndexedDbIndexDefinition("contentHash", ["contentHash"])],
            purpose:
                "One record per deduplicated content chunk, keyed by SHA-256 content hash and " +
                "zero-based chunk index according to P2-IDB-003 (D-009)."),

        new(
            CatalogStoreName,
            keyPath: ["appId"],
            autoIncrement: false,
            indexes: [new IndexedDbIndexDefinition("enabledFlag", ["enabledFlag"])],
            purpose:
                "One record per installed app (manifest plus enablement), keyed by app ID. " +
                "enabledFlag is stored as 0/1 because IndexedDB index keys exclude booleans."),

        new(
            GrantStoreName,
            keyPath: ["id"],
            autoIncrement: false,
            indexes:
            [
                new IndexedDbIndexDefinition("appUserCapability", ["appId", "userId", "capability"]),
                new IndexedDbIndexDefinition("userId", ["userId"])
            ],
            purpose:
                "One record per immutable CapabilityGrant, keyed by its opaque grant ID. The " +
                "compound index matches the shape of a routine capability evaluation lookup."),

        new(
            AuditStoreName,
            keyPath: ["id"],
            autoIncrement: true,
            indexes:
            [
                new IndexedDbIndexDefinition("timestampUtcMs", ["timestampUtcMs"]),
                new IndexedDbIndexDefinition("action", ["action"])
            ],
            purpose:
                "Append-only AuditEntry records. timestampUtcMs stores epoch milliseconds (a " +
                "number) rather than a Date so ordering is identical across browsers."),

        new(
            DiagnosticsStoreName,
            keyPath: ["id"],
            autoIncrement: true,
            indexes:
            [
                new IndexedDbIndexDefinition("timestampUtcMs", ["timestampUtcMs"]),
                new IndexedDbIndexDefinition("severity", ["severity"])
            ],
            purpose:
                "Bounded DiagnosticEntry records. Retention/eviction of the oldest entries is " +
                "enforced by the C# repository (P2-IDB-009), not by an IndexedDB-native limit."),

        new(
            LocalBookkeepingStoreName,
            keyPath: ["key"],
            autoIncrement: false,
            indexes: [],
            purpose:
                "Small local bookkeeping values (current policy revision, installation ID, last " +
                "backup time). Not multi-device sync; see this member's XML summary."),

        new(
            ServerConnectionStoreName,
            keyPath: ["key"],
            autoIncrement: false,
            indexes: [],
            purpose:
                "The single per-device optional-server connection record (account ID, device ID, " +
                "server base URL, opaque refresh token), keyed by a fixed constant key. Added by " +
                "ADR 0028; distinct from the local-only syncMetadata store."),

        new(
            SyncCursorStoreName,
            keyPath: ["domain"],
            autoIncrement: false,
            indexes: [],
            purpose:
                "One opaque pull cursor per sync domain (ADR 0029). Domain-agnostic scaffolding " +
                "reused by every sync-domain pass, not specific to Settings."),

        new(
            SyncRecordStateStoreName,
            keyPath: ["id"],
            autoIncrement: false,
            indexes: [new IndexedDbIndexDefinition("domain", ["domain"])],
            purpose:
                "Last-synced sync-Revision/ContentHash per (domain, recordId) pair, id = " +
                "\"domain|recordId\" (ADR 0029). Separate from any document's own local " +
                "optimistic-concurrency revision.")
    ];

    /// <summary>
    /// Gets every named cross-store atomic transaction boundary. A transaction never spans more
    /// stores than one boundary lists here; enforcing that is a repository responsibility, not an
    /// IndexedDB-native guarantee.
    /// </summary>
    public static IReadOnlyList<IndexedDbTransactionBoundary> TransactionBoundaries { get; } =
    [
        new(
            "UserAccountWrite",
            [UserStoreName],
            "Create/update one local user account atomically; no other store participates."),

        new(
            "GroupWrite",
            [GroupStoreName],
            "Create or read one local group independently of user-account mutation."),

        new(
            "SessionLifecycle",
            [SessionStoreName],
            "Create/update one session record atomically for login/logout/shutdown transitions."),

        new(
            "SettingsDocumentWrite",
            [SettingsStoreName],
            "Write one settings document and bump its revision in a single atomic put."),

        new(
            "LegacySettingsRead",
            [SettingsStoreName],
            "Read every record of the retired settings store, read-only, so the one-time " +
                "settings-to-files migration can copy them into real files (ADR 0044)."),

        new(
            "FileSystemMetadataMutation",
            [FileSystemEntryStoreName, FileSystemLinkStoreName],
            "Create/move/rename/delete an entry and its directory link(s) atomically together. " +
                "Content chunks are excluded; see FileSystemContentWrite."),

        new(
            "FileSystemContentWrite",
            [FileContentStoreName],
            "Write a file's content independently of its metadata transaction; ordering between " +
                "the two is a repository-level concern (P2-IDB-008), not this boundary's."),

        new(
            "FileSystemContentCleanup",
            [FileSystemEntryStoreName, FileContentStoreName],
            "Delete retained orphan chunks only after atomically rechecking that no filesystem " +
                "entry references their content hash."),

        new(
            "CatalogEnablementChange",
            [CatalogStoreName],
            "Install/uninstall/enable/disable one catalog entry atomically."),

        new(
            "PolicyGrantMutation",
            [GrantStoreName, AuditStoreName, LocalBookkeepingStoreName],
            "Grant/revoke one capability, append its audit entry, and advance the policy revision " +
                "in the same atomic transaction."),

        new(
            "AuditAppend",
            [AuditStoreName],
            "Append one audit entry on its own when it is not already covered by another named " +
                "boundary (e.g. login/logout)."),

        new(
            "DiagnosticsAppend",
            [DiagnosticsStoreName],
            "Append (and possibly evict) one diagnostic entry independently; never combined with " +
                "another store's transaction."),

        new(
            "LocalBookkeepingUpdate",
            [LocalBookkeepingStoreName],
            "Update one local bookkeeping value independently of every other store."),

        new(
            "BackupRestore",
            ObjectStores.Select(store => store.Name).ToArray(),
            "Read a consistent export snapshot or restore every validated store atomically."),

        new(
            "ServerConnectionUpdate",
            [ServerConnectionStoreName],
            "Create/update/clear the single per-device server connection record atomically, " +
                "independent of every other store (ADR 0028)."),

        new(
            "SyncCursorUpdate",
            [SyncCursorStoreName],
            "Create/update one sync domain's pull cursor atomically, independent of every other " +
                "store (ADR 0029)."),

        new(
            "SyncRecordStateUpdate",
            [SyncRecordStateStoreName],
            "Create/update one (domain, recordId) sync tracking record atomically, independent of " +
                "every other store (ADR 0029).")
    ];
}
