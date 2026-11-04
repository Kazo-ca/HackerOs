using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>Identifies the filesystem operation that produced a result.</summary>
public enum FileSystemOperation
{
    /// <summary>Read regular-file content.</summary>
    Read = 1,

    /// <summary>Enumerate immediate directory children.</summary>
    Enumerate = 2,

    /// <summary>Create a file, directory, or symbolic link.</summary>
    Create = 3,

    /// <summary>Replace regular-file content.</summary>
    Write = 4,

    /// <summary>Move or rename an entry.</summary>
    Move = 5,

    /// <summary>Copy an entry or subtree.</summary>
    Copy = 6,

    /// <summary>Delete an entry or subtree.</summary>
    Delete = 7,

    /// <summary>Read entry metadata.</summary>
    Stat = 8,

    /// <summary>Replace entry permission bits.</summary>
    SetPermissions = 9,

    /// <summary>Commit a group of filesystem mutations.</summary>
    Transaction = 10
}

/// <summary>
/// Provides provider-neutral filesystem failures with stable numeric values.
/// </summary>
public enum FileSystemErrorCode
{
    /// <summary>A path is malformed or exceeds a supported path limit.</summary>
    InvalidPath = 1000,

    /// <summary>An entry name is malformed or exceeds its encoded limit.</summary>
    InvalidName = 1001,

    /// <summary>Streamed content is malformed for its declared representation.</summary>
    InvalidContent = 1002,

    /// <summary>No entry exists at the requested path.</summary>
    NotFound = 1100,

    /// <summary>An entry already occupies the requested destination.</summary>
    AlreadyExists = 1101,

    /// <summary>The operation requires a regular file.</summary>
    NotFile = 1102,

    /// <summary>The operation requires a directory.</summary>
    NotDirectory = 1103,

    /// <summary>A non-recursive delete targeted a non-empty directory.</summary>
    DirectoryNotEmpty = 1104,

    /// <summary>Owner/group/other mode checks denied the operation.</summary>
    PermissionDenied = 1200,

    /// <summary>The app lacks the exact capability required by policy.</summary>
    CapabilityDenied = 1201,

    /// <summary>The acting principal lacks the required authority.</summary>
    AuthorityDenied = 1202,

    /// <summary>An optimistic entry or parent revision is stale.</summary>
    RevisionConflict = 1300,

    /// <summary>Symbolic-link traversal encountered a repeated link.</summary>
    SymbolicLinkLoop = 1400,

    /// <summary>Symbolic-link traversal exceeded the supported hop limit.</summary>
    SymbolicLinkLimitExceeded = 1401,

    /// <summary>Traversal attempted to escape the virtual root.</summary>
    RootContainmentViolation = 1402,

    /// <summary>The requested atomic operation spans incompatible providers.</summary>
    CrossMountOperation = 1403,

    /// <summary>The requested root, mount, or projected entry is protected.</summary>
    ProtectedEntry = 1500,

    /// <summary>The selected provider does not support the requested operation.</summary>
    UnsupportedOperation = 1501,

    /// <summary>Cancellation was observed before commit.</summary>
    Cancelled = 1600,

    /// <summary>The selected provider is temporarily unavailable.</summary>
    ProviderUnavailable = 1700,

    /// <summary>The provider failed without exposing infrastructure details.</summary>
    ProviderFailure = 1701,

    /// <summary>
    /// The storage is full, or the free space left is reserved for the administrator and the
    /// system (ADR 0099, section 2): Linux <c>ENOSPC</c>, "No space left on device". Nothing of
    /// the operation was committed.
    /// </summary>
    NoSpace = 1800,

    /// <summary>
    /// The content exceeds the largest file the storage accepts, 16 MiB (ADR 0094): Linux
    /// <c>EFBIG</c>, "File too large". Nothing of the operation was committed.
    /// </summary>
    FileTooLarge = 1801,

    /// <summary>
    /// The process already holds as many open descriptors as its <c>nofile</c> soft limit allows
    /// (ADR 0125): Linux <c>EMFILE</c>, "Too many open files". Nothing was opened.
    /// </summary>
    TooManyOpenFiles = 1802
}

/// <summary>Describes one unsuccessful filesystem operation.</summary>
public sealed record FileSystemError
{
    /// <summary>Initializes a stable filesystem error.</summary>
    /// <param name="operation">Operation that failed.</param>
    /// <param name="code">Provider-neutral failure code.</param>
    /// <param name="path">Primary path involved, when available.</param>
    /// <param name="relatedPath">Secondary source or destination path, when available.</param>
    /// <param name="requiredCapability">
    /// The exact broad capability policy required for this operation, when known — populated for
    /// authority/capability/permission denials so an admin-approval prompt (`ADR 0042`) knows exactly
    /// what to grant.
    /// </param>
    public FileSystemError(
        FileSystemOperation operation,
        FileSystemErrorCode code,
        VirtualPath? path = null,
        VirtualPath? relatedPath = null,
        string? requiredCapability = null)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown filesystem operation.");
        }

        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown filesystem error code.");
        }

        Operation = operation;
        Code = code;
        Path = path;
        RelatedPath = relatedPath;
        RequiredCapability = requiredCapability;
    }

    /// <summary>Gets the operation that failed.</summary>
    public FileSystemOperation Operation { get; }

    /// <summary>Gets the stable provider-neutral failure code.</summary>
    public FileSystemErrorCode Code { get; }

    /// <summary>Gets the primary path involved, when available.</summary>
    public VirtualPath? Path { get; }

    /// <summary>Gets the secondary source or destination path, when available.</summary>
    public VirtualPath? RelatedPath { get; }

    /// <summary>Gets the exact broad capability policy required for this operation, when known.</summary>
    public string? RequiredCapability { get; }
}

/// <summary>Contains either one successful filesystem value or one stable error.</summary>
/// <typeparam name="T">Successful value type.</typeparam>
public sealed record FileSystemResult<T>
    where T : notnull
{
    private FileSystemResult(bool succeeded, T? value, FileSystemError? error)
    {
        Succeeded = succeeded;
        Value = value;
        Error = error;
    }

    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the successful value, or the default value after failure.</summary>
    public T? Value { get; }

    /// <summary>Gets the stable error after failure, or <see langword="null"/> after success.</summary>
    public FileSystemError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">Operation value.</param>
    /// <returns>A successful result with no error.</returns>
    public static FileSystemResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new FileSystemResult<T>(true, value, null);
    }

    /// <summary>Creates an unsuccessful result.</summary>
    /// <param name="error">Stable operation error.</param>
    /// <returns>A failed result with no value.</returns>
    public static FileSystemResult<T> Failure(FileSystemError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new FileSystemResult<T>(false, default, error);
    }
}

/// <summary>Describes the outcome of an atomic filesystem transaction.</summary>
public enum FileSystemTransactionStatus
{
    /// <summary>Every mutation committed and is visible.</summary>
    Committed = 1,

    /// <summary>No mutation committed because validation or a precondition failed.</summary>
    Rejected = 2,

    /// <summary>No mutation committed because cancellation was observed.</summary>
    Cancelled = 3
}

/// <summary>Contains the all-or-nothing result of a filesystem transaction.</summary>
public sealed record FileSystemTransactionResult
{
    private FileSystemTransactionResult(
        Guid transactionId,
        FileSystemTransactionStatus status,
        IReadOnlyList<FileSystemEntryId> affectedEntryIds,
        FileSystemError? error)
    {
        TransactionId = transactionId;
        Status = status;
        AffectedEntryIds = affectedEntryIds;
        Error = error;
    }

    /// <summary>Gets the non-empty transaction correlation ID.</summary>
    public Guid TransactionId { get; }

    /// <summary>Gets the atomic transaction outcome.</summary>
    public FileSystemTransactionStatus Status { get; }

    /// <summary>Gets committed affected entry IDs in deterministic operation order.</summary>
    public IReadOnlyList<FileSystemEntryId> AffectedEntryIds { get; }

    /// <summary>Gets the failure when rejected or cancelled.</summary>
    public FileSystemError? Error { get; }

    /// <summary>Creates a committed transaction result.</summary>
    /// <param name="transactionId">Non-empty transaction correlation ID.</param>
    /// <param name="affectedEntryIds">Affected entries in deterministic operation order.</param>
    /// <returns>The committed transaction result.</returns>
    public static FileSystemTransactionResult Committed(
        Guid transactionId,
        IEnumerable<FileSystemEntryId> affectedEntryIds)
    {
        EnsureTransactionId(transactionId);
        ArgumentNullException.ThrowIfNull(affectedEntryIds);

        FileSystemEntryId[] entries = affectedEntryIds.ToArray();
        if (entries.Any(static entryId => entryId.IsEmpty))
        {
            throw new ArgumentException("Affected entry IDs cannot be empty.", nameof(affectedEntryIds));
        }

        return new FileSystemTransactionResult(
            transactionId,
            FileSystemTransactionStatus.Committed,
            Array.AsReadOnly(entries),
            null);
    }

    /// <summary>Creates a rejected transaction result with no committed mutations.</summary>
    /// <param name="transactionId">Non-empty transaction correlation ID.</param>
    /// <param name="error">Failure that rejected the transaction.</param>
    /// <returns>The rejected transaction result.</returns>
    public static FileSystemTransactionResult Rejected(Guid transactionId, FileSystemError error)
    {
        EnsureTransactionId(transactionId);
        ArgumentNullException.ThrowIfNull(error);

        if (error.Code == FileSystemErrorCode.Cancelled)
        {
            throw new ArgumentException("Use the cancelled result for cancellation errors.", nameof(error));
        }

        return new FileSystemTransactionResult(
            transactionId,
            FileSystemTransactionStatus.Rejected,
            Array.Empty<FileSystemEntryId>(),
            error);
    }

    /// <summary>Creates a cancelled transaction result with no committed mutations.</summary>
    /// <param name="transactionId">Non-empty transaction correlation ID.</param>
    /// <param name="error">Cancellation error.</param>
    /// <returns>The cancelled transaction result.</returns>
    public static FileSystemTransactionResult Cancelled(Guid transactionId, FileSystemError error)
    {
        EnsureTransactionId(transactionId);
        ArgumentNullException.ThrowIfNull(error);

        if (error.Code != FileSystemErrorCode.Cancelled)
        {
            throw new ArgumentException("Cancelled transactions require the cancellation error code.", nameof(error));
        }

        return new FileSystemTransactionResult(
            transactionId,
            FileSystemTransactionStatus.Cancelled,
            Array.Empty<FileSystemEntryId>(),
            error);
    }

    private static void EnsureTransactionId(Guid transactionId)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("Transaction IDs cannot be empty.", nameof(transactionId));
        }
    }
}