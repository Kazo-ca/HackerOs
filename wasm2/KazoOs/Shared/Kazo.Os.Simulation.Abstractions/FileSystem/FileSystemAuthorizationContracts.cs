using System.Collections.Frozen;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>Defines operations delegated by a selected-resource handle.</summary>
[Flags]
public enum FileSystemHandleAccess
{
    /// <summary>No delegated access.</summary>
    None = 0,

    /// <summary>Read regular-file content.</summary>
    Read = 1,

    /// <summary>Write or replace regular-file content.</summary>
    Write = 2,

    /// <summary>Enumerate or traverse a selected directory.</summary>
    Enumerate = 4,

    /// <summary>Read metadata for the selected resource.</summary>
    Metadata = 8
}

/// <summary>Represents a trusted, short-lived delegated filesystem grant.</summary>
public sealed record FileSystemSelectedResourceHandle
{
    private const FileSystemHandleAccess AllAccess = FileSystemHandleAccess.Read
        | FileSystemHandleAccess.Write
        | FileSystemHandleAccess.Enumerate
        | FileSystemHandleAccess.Metadata;

    /// <summary>Initializes an immutable selected-resource handle.</summary>
    public FileSystemSelectedResourceHandle(
        Guid id,
        string appId,
        string userId,
        VirtualPath path,
        FileSystemHandleAccess access,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        long policyRevision,
        bool revoked = false)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Selected-resource handle IDs cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        ValidateAccess(access);
        EnsureUtc(issuedAtUtc, nameof(issuedAtUtc));
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= issuedAtUtc)
        {
            throw new ArgumentException("Selected-resource handles must expire after issue time.", nameof(expiresAtUtc));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(policyRevision, 1);

        Id = id;
        AppId = appId;
        UserId = userId;
        Path = path;
        Access = access;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        PolicyRevision = policyRevision;
        Revoked = revoked;
    }

    /// <summary>Gets the opaque handle ID.</summary>
    public Guid Id { get; }

    /// <summary>Gets the app to which the handle was issued.</summary>
    public string AppId { get; }

    /// <summary>Gets the user to which the handle was issued.</summary>
    public string UserId { get; }

    /// <summary>Gets the selected path root.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets delegated operations.</summary>
    public FileSystemHandleAccess Access { get; }

    /// <summary>Gets the UTC issue time.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>Gets the exclusive UTC expiry time.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Gets the policy revision under which the handle was issued.</summary>
    public long PolicyRevision { get; }

    /// <summary>Gets whether trusted policy explicitly revoked the handle.</summary>
    public bool Revoked { get; }

    /// <summary>Determines whether this handle delegates the requested access.</summary>
    public bool Allows(
        string appId,
        string userId,
        VirtualPath path,
        FileSystemHandleAccess requiredAccess,
        DateTimeOffset evaluatedAtUtc)
    {
        ValidateAccess(requiredAccess);
        EnsureUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));

        return !Revoked
            && evaluatedAtUtc >= IssuedAtUtc
            && evaluatedAtUtc < ExpiresAtUtc
            && string.Equals(AppId, appId, StringComparison.Ordinal)
            && string.Equals(UserId, userId, StringComparison.Ordinal)
            && (Access & requiredAccess) == requiredAccess
            && IsPathWithin(path, Path);
    }

    private static bool IsPathWithin(VirtualPath path, VirtualPath root)
    {
        if (path == root || root.Value == "/")
        {
            return true;
        }

        return path.Value.StartsWith($"{root.Value}/", StringComparison.Ordinal);
    }

    private static void ValidateAccess(FileSystemHandleAccess access)
    {
        if (access == FileSystemHandleAccess.None || (access & ~AllAccess) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(access), access, "Handle access must contain known operations.");
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Selected-resource handle times must use UTC.", parameterName);
        }
    }
}

/// <summary>Provides trusted app, user, group, handle, and evaluation-time inputs.</summary>
public sealed record FileSystemAuthorizationContext
{
    /// <summary>Initializes a trusted filesystem authorization context.</summary>
    public FileSystemAuthorizationContext(
        AppOperationContext operationContext,
        IEnumerable<string> groupIds,
        DateTimeOffset evaluatedAtUtc,
        FileSystemSelectedResourceHandle? selectedHandle = null)
    {
        ArgumentNullException.ThrowIfNull(operationContext);
        ArgumentNullException.ThrowIfNull(groupIds);
        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Authorization evaluation time must use UTC.", nameof(evaluatedAtUtc));
        }

        string[] groups = groupIds.ToArray();
        if (groups.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Group IDs cannot be blank.", nameof(groupIds));
        }

        OperationContext = operationContext;
        GroupIds = groups.ToFrozenSet(StringComparer.Ordinal);
        EvaluatedAtUtc = evaluatedAtUtc;
        SelectedHandle = selectedHandle;
    }

    /// <summary>Gets the trusted app operation context.</summary>
    public AppOperationContext OperationContext { get; }

    /// <summary>Gets exact group memberships for the acting user.</summary>
    public IReadOnlySet<string> GroupIds { get; }

    /// <summary>Gets deterministic UTC evaluation time.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; }

    /// <summary>Gets an optional trusted selected-resource handle.</summary>
    public FileSystemSelectedResourceHandle? SelectedHandle { get; }
}

/// <summary>Contains all inputs needed for one filesystem policy decision.</summary>
public sealed record FileSystemAuthorizationRequest
{
    /// <summary>Initializes a complete authorization request.</summary>
    public FileSystemAuthorizationRequest(
        FileSystemOperation operation,
        VirtualPath path,
        FileSystemEntryMetadata metadata,
        FileSystemAccess requiredModeAccess,
        string requiredCapability,
        FileSystemHandleAccess requiredHandleAccess,
        AppAuthority minimumAuthority,
        FileSystemAuthorizationContext context)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown filesystem operation.");
        }

        FileSystemContractGuard.ValidatePath(path, nameof(path));
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredCapability);
        ArgumentNullException.ThrowIfNull(context);

        Operation = operation;
        Path = path;
        Metadata = metadata;
        RequiredModeAccess = requiredModeAccess;
        RequiredCapability = requiredCapability;
        RequiredHandleAccess = requiredHandleAccess;
        MinimumAuthority = minimumAuthority;
        Context = context;
    }

    /// <summary>Gets the requested filesystem operation.</summary>
    public FileSystemOperation Operation { get; }

    /// <summary>Gets the authorized path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets entry or parent metadata used for mode checks.</summary>
    public FileSystemEntryMetadata Metadata { get; }

    /// <summary>Gets required owner/group/other access.</summary>
    public FileSystemAccess RequiredModeAccess { get; }

    /// <summary>Gets the exact broad capability accepted by policy.</summary>
    public string RequiredCapability { get; }

    /// <summary>Gets selected-handle access that may replace the broad capability.</summary>
    public FileSystemHandleAccess RequiredHandleAccess { get; }

    /// <summary>Gets minimum acting authority.</summary>
    public AppAuthority MinimumAuthority { get; }

    /// <summary>Gets trusted identity and grant inputs.</summary>
    public FileSystemAuthorizationContext Context { get; }
}

/// <summary>Contains an allow decision or a stable denial error.</summary>
public sealed record FileSystemAuthorizationResult
{
    private FileSystemAuthorizationResult(bool allowed, FileSystemError? error)
    {
        Allowed = allowed;
        Error = error;
    }

    /// <summary>Gets whether all authority, capability, and mode checks passed.</summary>
    public bool Allowed { get; }

    /// <summary>Gets the stable denial error, or <see langword="null"/> when allowed.</summary>
    public FileSystemError? Error { get; }

    /// <summary>Creates an allowed result.</summary>
    public static FileSystemAuthorizationResult Permit() => new(true, null);

    /// <summary>Creates a denied result.</summary>
    public static FileSystemAuthorizationResult Deny(FileSystemError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new FileSystemAuthorizationResult(false, error);
    }
}

/// <summary>Evaluates trusted filesystem authorization inputs.</summary>
public interface IFileSystemAuthorizer
{
    /// <summary>Evaluates authority, exact capability or handle, and mode access.</summary>
    FileSystemAuthorizationResult Authorize(FileSystemAuthorizationRequest request);
}