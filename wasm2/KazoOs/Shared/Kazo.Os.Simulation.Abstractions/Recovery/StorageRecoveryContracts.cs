namespace Kazo.Os.Simulation.Abstractions.Recovery;

/// <summary>Identifies the user-visible storage recovery condition.</summary>
public enum StorageRecoveryState
{
    /// <summary>Browser storage cannot currently be opened or accessed.</summary>
    StorageUnavailable = 1,

    /// <summary>An operation could not commit because browser quota is exhausted.</summary>
    QuotaExhausted = 2,

    /// <summary>A database migration failed while the prior committed version remains authoritative.</summary>
    MigrationFailed = 3,

    /// <summary>Persisted records are invalid or only partially readable.</summary>
    ContentInvalid = 4,

    /// <summary>A supplied backup is malformed, incompatible, corrupt, or conflicting.</summary>
    BackupValidationFailed = 5,

    /// <summary>An optimistic operation conflict can be retried or resolved without destructive repair.</summary>
    RecoverableConflict = 6,

    /// <summary>A destructive operation is waiting for explicit targeted confirmation.</summary>
    AwaitingDestructiveConfirmation = 7
}

/// <summary>Identifies where a storage failure occurred.</summary>
public enum StorageRecoveryContext
{
    /// <summary>Storage readiness validation during deterministic boot.</summary>
    Boot = 1,

    /// <summary>An ordinary operation after successful boot.</summary>
    Operation = 2,

    /// <summary>An IndexedDB schema migration.</summary>
    Migration = 3,

    /// <summary>Backup validation or restore.</summary>
    Restore = 4
}

/// <summary>Lists commands a recovery renderer may offer for one state.</summary>
[Flags]
public enum StorageRecoveryActions
{
    /// <summary>No recovery action is currently safe.</summary>
    None = 0,

    /// <summary>Retry the failed non-destructive operation.</summary>
    Retry = 1 << 0,

    /// <summary>Run read-only diagnosis without changing persisted data.</summary>
    DiagnoseReadOnly = 1 << 1,

    /// <summary>Export every still-readable record before further repair.</summary>
    Export = 1 << 2,

    /// <summary>Run explicitly requested bounded orphan cleanup.</summary>
    Cleanup = 1 << 3,

    /// <summary>Restore a validated backup using additive merge.</summary>
    RestoreMerge = 1 << 4,

    /// <summary>Request replacement from a validated backup after confirmation.</summary>
    RestoreReplace = 1 << 5,

    /// <summary>Request a full storage reset after confirmation.</summary>
    Reset = 1 << 6
}

/// <summary>Describes a destructive action that requires an exact phrase.</summary>
/// <param name="Action">The single destructive action being confirmed.</param>
/// <param name="RequiredPhrase">Exact case-sensitive phrase the user must enter.</param>
/// <param name="AffectedData">Non-empty user-safe description of data that will be affected.</param>
public sealed record StorageDestructiveConfirmation(
    StorageRecoveryActions Action,
    string RequiredPhrase,
    string AffectedData)
{
    /// <summary>Returns whether input exactly matches the required phrase.</summary>
    public bool IsConfirmedBy(string? input) =>
        StringComparer.Ordinal.Equals(RequiredPhrase, input);
}

/// <summary>Defines one renderer-independent storage recovery presentation.</summary>
/// <param name="State">Typed recovery condition.</param>
/// <param name="ErrorCode">Stable machine-readable error code.</param>
/// <param name="BlocksBoot">Whether ordinary OS readiness must remain blocked.</param>
/// <param name="CanExport">Whether read-only export should remain offered.</param>
/// <param name="Actions">Only actions safe for this state.</param>
/// <param name="CorrelationId">Identifier linking UI, diagnostics, and support export.</param>
/// <param name="Confirmation">Required targeted confirmation for a destructive action.</param>
public sealed record StorageRecoveryPresentation(
    StorageRecoveryState State,
    string ErrorCode,
    bool BlocksBoot,
    bool CanExport,
    StorageRecoveryActions Actions,
    Guid CorrelationId,
    StorageDestructiveConfirmation? Confirmation = null);