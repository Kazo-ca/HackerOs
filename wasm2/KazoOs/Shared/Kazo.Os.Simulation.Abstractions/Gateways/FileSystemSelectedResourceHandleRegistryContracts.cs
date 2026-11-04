using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Simulation.Abstractions.Gateways;

/// <summary>
/// Issues, tracks, and revokes short-lived <see cref="FileSystemSelectedResourceHandle"/>
/// grants (e.g. from a file-open/save dialog), per `P1-EXEC-005`/`P1-EXEC-006`.
/// </summary>
/// <remarks>
/// A handle is automatically revoked when it expires, when the issuing process exits, when the
/// issuing user's session logs out or shuts down, or when the issuing app is disabled/uninstalled.
/// Trusted policy code may additionally revoke a handle explicitly at any time.
/// </remarks>
public interface IFileSystemSelectedResourceHandleRegistry
{
    /// <summary>Issues a new handle valid from the current simulated time.</summary>
    /// <param name="appId">Exact app ID the handle is issued to.</param>
    /// <param name="userId">Exact user ID the handle is issued to.</param>
    /// <param name="path">Selected canonical path root the handle delegates.</param>
    /// <param name="access">Delegated operations.</param>
    /// <param name="validFor">Duration the handle remains valid from issue time.</param>
    /// <param name="issuedToProcessId">
    /// Optional owning process; when set, the handle is automatically revoked once that process
    /// leaves the active process table.
    /// </param>
    FileSystemSelectedResourceHandle Issue(
        string appId,
        string userId,
        VirtualPath path,
        FileSystemHandleAccess access,
        TimeSpan validFor,
        ProcessId? issuedToProcessId = null);

    /// <summary>Attempts to find one still-tracked handle by ID, regardless of revoked/expired state.</summary>
    bool TryGet(Guid handleId, out FileSystemSelectedResourceHandle handle);

    /// <summary>Explicitly revokes one handle.</summary>
    /// <returns><see langword="true"/> if a tracked, not-yet-revoked handle was found.</returns>
    bool Revoke(Guid handleId);

    /// <summary>Revokes every handle issued to one process. Used on process exit.</summary>
    /// <returns>The number of handles revoked.</returns>
    int RevokeAllForProcess(ProcessId processId);

    /// <summary>Revokes every handle issued to one user. Used on logout/shutdown.</summary>
    /// <returns>The number of handles revoked.</returns>
    int RevokeAllForUser(string userId);

    /// <summary>Revokes every handle issued to one app. Used on disable/uninstall.</summary>
    /// <returns>The number of handles revoked.</returns>
    int RevokeAllForApp(string appId);
}
