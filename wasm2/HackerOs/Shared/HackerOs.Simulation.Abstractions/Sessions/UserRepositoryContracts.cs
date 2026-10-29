using HackerOs.App.Abstractions;

namespace HackerOs.Simulation.Abstractions.Sessions;

/// <summary>Stores and mutates local user accounts, per ADR 0013.</summary>
public interface ILocalUserRepository
{
    /// <summary>Creates and stores one new, enabled local user account.</summary>
    /// <param name="loginName">Normalized, unique, case-insensitive login name.</param>
    /// <param name="displayName">Mutable display name shown in the UI.</param>
    /// <param name="authority">Login authority granted to sessions started by this user.</param>
    /// <param name="primaryGroupId">The user's primary group.</param>
    /// <param name="additionalGroupIds">Additional groups the user belongs to; defaults to none.</param>
    /// <param name="credential">Optional local password credential.</param>
    /// <param name="cancellationToken">Cancels the write to the user store.</param>
    /// <returns>The newly created user record.</returns>
    /// <exception cref="InvalidOperationException">The login name is already in use.</exception>
    ValueTask<LocalUser> CreateUserAsync(
        LocalLoginName loginName,
        string displayName,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId>? additionalGroupIds = null,
        LocalPasswordCredential? credential = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates and stores the guest account of ADR 0187: enabled, without a password, marked
    /// <see cref="LocalUser.IsGuest"/>. Callers check that no other account exists.
    /// </summary>
    /// <param name="loginName">Login name of the guest (<c>guest</c>).</param>
    /// <param name="displayName">Display name, in the system language.</param>
    /// <param name="authority">Login authority (Administrator).</param>
    /// <param name="primaryGroupId">The guest's own group.</param>
    /// <param name="additionalGroupIds">Additional groups (<c>administrators</c>).</param>
    /// <param name="cancellationToken">Cancels the write to the user store.</param>
    /// <returns>The guest account.</returns>
    /// <exception cref="InvalidOperationException">The login name is already in use.</exception>
    /// <exception cref="NotSupportedException">The store cannot keep a guest account (the default).</exception>
    ValueTask<LocalUser> CreateGuestUserAsync(
        LocalLoginName loginName,
        string displayName,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId> additionalGroupIds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This user store cannot keep a guest account.");

    /// <summary>
    /// Turns the guest account into a registered account in place (ADR 0187 section 4), as
    /// <c>usermod -l NEW -c NAME</c> then <c>passwd</c>: same <see cref="LocalUser.Id"/>, new login and
    /// display name, the password set and the guest mark removed. Callers move the home directory.
    /// </summary>
    /// <param name="id">The guest account.</param>
    /// <param name="loginName">The new login name.</param>
    /// <param name="displayName">The new display name.</param>
    /// <param name="credential">The new password credential.</param>
    /// <param name="cancellationToken">Cancels the write to the user store.</param>
    /// <returns>The registered account.</returns>
    /// <exception cref="InvalidOperationException">No such guest account, or the login name is in use.</exception>
    /// <exception cref="NotSupportedException">The store cannot convert the guest (the default).</exception>
    ValueTask<LocalUser> ConvertGuestUserAsync(
        LocalUserId id,
        LocalLoginName loginName,
        string displayName,
        LocalPasswordCredential credential,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This user store cannot convert a guest account.");

    /// <summary>Attempts to find a user by opaque ID.</summary>
    ValueTask<LocalUser?> FindByIdAsync(LocalUserId id, CancellationToken cancellationToken = default);

    /// <summary>Attempts to find a user by normalized login name.</summary>
    ValueTask<LocalUser?> FindByLoginNameAsync(
        LocalLoginName loginName,
        CancellationToken cancellationToken = default);

    /// <summary>Gets every stored user.</summary>
    ValueTask<IReadOnlyList<LocalUser>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables one user. The last enabled Administrator can never be disabled.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="enabled"/> is <see langword="false"/> and the user is the last enabled Administrator.
    /// </exception>
    ValueTask<LocalUser> SetEnabledAsync(
        LocalUserId id,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes one user's login authority. The last enabled Administrator can never be demoted.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The user is the last enabled Administrator and <paramref name="authority"/> is not Administrator.
    /// </exception>
    ValueTask<LocalUser> SetAuthorityAsync(
        LocalUserId id,
        AppAuthority authority,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces one user's password credential, as <c>passwd</c> rewrites <c>/etc/shadow</c>
    /// (ADR 0120). <see langword="null"/> removes the password. Callers verify who may do it.
    /// </summary>
    /// <param name="id">The user to change.</param>
    /// <param name="credential">The new credential, or <see langword="null"/> for no password.</param>
    /// <param name="cancellationToken">Cancels the write to the user store.</param>
    /// <returns>The updated user record.</returns>
    /// <exception cref="NotSupportedException">The store cannot change credentials (the default).</exception>
    ValueTask<LocalUser> SetCredentialAsync(
        LocalUserId id,
        LocalPasswordCredential? credential,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This user store cannot change passwords.");

    /// <summary>
    /// Replaces one user's passkeys (ADR 0168). Callers verify who may do it.
    /// </summary>
    /// <param name="id">The user to change.</param>
    /// <param name="passkeys">The complete new list of passkeys.</param>
    /// <param name="cancellationToken">Cancels the write to the user store.</param>
    /// <returns>The updated user record.</returns>
    /// <exception cref="NotSupportedException">The store cannot keep passkeys (the default).</exception>
    ValueTask<LocalUser> SetPasskeysAsync(
        LocalUserId id,
        IReadOnlyList<LocalPasskeyCredential> passkeys,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This user store cannot keep passkeys.");

    /// <summary>
    /// Deletes one user account, as <c>userdel</c> removes its <c>/etc/passwd</c> line (ADR 0169).
    /// The last enabled Administrator can never be deleted. Callers verify who may do it and handle
    /// the home directory.
    /// </summary>
    /// <param name="id">The user to delete.</param>
    /// <param name="cancellationToken">Cancels the write to the user store.</param>
    /// <exception cref="KeyNotFoundException">No such user.</exception>
    /// <exception cref="InvalidOperationException">The user is the last enabled Administrator.</exception>
    /// <exception cref="NotSupportedException">The store cannot delete accounts (the default).</exception>
    ValueTask DeleteUserAsync(LocalUserId id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This user store cannot delete accounts.");
}

/// <summary>Stores local user groups, per ADR 0013.</summary>
public interface ILocalGroupRepository
{
    /// <summary>Creates and stores one new local group.</summary>
    ValueTask<LocalGroup> CreateGroupAsync(
        LocalLoginName name,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to find a group by opaque ID.</summary>
    ValueTask<LocalGroup?> FindByIdAsync(LocalGroupId id, CancellationToken cancellationToken = default);

    /// <summary>Attempts to find a group by its normalized unique name.</summary>
    ValueTask<LocalGroup?> FindByNameAsync(
        LocalLoginName name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames one group, keeping its <see cref="LocalGroup.Id"/>, as <c>groupmod -n</c> (used when
    /// the guest account becomes a registered one, ADR 0187 section 4).
    /// </summary>
    /// <param name="id">The group to rename.</param>
    /// <param name="name">Its new name.</param>
    /// <param name="cancellationToken">Cancels the write to the group store.</param>
    /// <returns>The renamed group.</returns>
    /// <exception cref="InvalidOperationException">No such group, or the name is in use.</exception>
    /// <exception cref="NotSupportedException">The store cannot rename groups (the default).</exception>
    ValueTask<LocalGroup> RenameGroupAsync(
        LocalGroupId id,
        LocalLoginName name,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This group store cannot rename groups.");

    /// <summary>Deletes one group, as <c>groupdel</c>; deleting a missing group does nothing.</summary>
    /// <param name="id">The group to delete.</param>
    /// <param name="cancellationToken">Cancels the write to the group store.</param>
    /// <exception cref="NotSupportedException">The store cannot delete groups (the default).</exception>
    ValueTask DeleteGroupAsync(LocalGroupId id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This group store cannot delete groups.");
}
