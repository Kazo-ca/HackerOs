using HackerOs.App.Abstractions;

namespace HackerOs.Simulation.Abstractions.Sessions;

/// <summary>
/// Represents one salted, versioned password credential for local authentication.
/// </summary>
/// <remarks>
/// The verifier is computed by a reviewed platform key-derivation function; this record never
/// stores the plaintext password. Comparison during login must use a constant-time algorithm.
/// </remarks>
public sealed record LocalPasswordCredential
{
    /// <summary>Initializes a validated password credential.</summary>
    /// <param name="kdfIdentifier">Versioned key-derivation function identifier, e.g. <c>pbkdf2-sha256-v1</c>.</param>
    /// <param name="salt">Random per-credential salt.</param>
    /// <param name="iterations">Work factor passed to the key-derivation function.</param>
    /// <param name="verifier">Derived verifier bytes compared at login time.</param>
    /// <exception cref="ArgumentException">A field is missing or out of range.</exception>
    public LocalPasswordCredential(string kdfIdentifier, byte[] salt, int iterations, byte[] verifier)
    {
        if (string.IsNullOrWhiteSpace(kdfIdentifier))
        {
            throw new ArgumentException("A KDF identifier is required.", nameof(kdfIdentifier));
        }

        if (salt is not { Length: > 0 })
        {
            throw new ArgumentException("A non-empty salt is required.", nameof(salt));
        }

        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "Iterations must be positive.");
        }

        if (verifier is not { Length: > 0 })
        {
            throw new ArgumentException("A non-empty verifier is required.", nameof(verifier));
        }

        KdfIdentifier = kdfIdentifier;
        Salt = salt;
        Iterations = iterations;
        Verifier = verifier;
    }

    /// <summary>Gets the versioned key-derivation function identifier.</summary>
    public string KdfIdentifier { get; }

    /// <summary>Gets the random per-credential salt.</summary>
    public byte[] Salt { get; }

    /// <summary>Gets the work factor passed to the key-derivation function.</summary>
    public int Iterations { get; }

    /// <summary>Gets the derived verifier bytes compared at login time.</summary>
    public byte[] Verifier { get; }
}

/// <summary>Represents one local user group.</summary>
/// <param name="Id">Opaque, immutable group identifier.</param>
/// <param name="Name">Normalized group name.</param>
public sealed record LocalGroup(LocalGroupId Id, LocalLoginName Name);

/// <summary>
/// Represents one local user account, per ADR 0013. Only <see cref="AppAuthority.User"/> and
/// <see cref="AppAuthority.Administrator"/> are valid login authorities; <see cref="AppAuthority.System"/>
/// is never assignable to a login-capable account.
/// </summary>
public sealed record LocalUser
{
    /// <summary>Initializes a validated local user account.</summary>
    /// <param name="id">Opaque, immutable user identifier.</param>
    /// <param name="loginName">Normalized, unique, case-insensitive login name.</param>
    /// <param name="displayName">Mutable display name shown in the UI.</param>
    /// <param name="enabled">Whether the account can start a new session.</param>
    /// <param name="authority">Login authority granted to sessions started by this user.</param>
    /// <param name="primaryGroupId">The user's primary group.</param>
    /// <param name="additionalGroupIds">Additional groups the user belongs to.</param>
    /// <param name="credential">Optional local password credential; <see langword="null"/> means no password is set.</param>
    /// <param name="revision">Monotonic revision incremented on every mutation, for optimistic concurrency.</param>
    /// <param name="createdAtUtc">UTC simulation time the account was created.</param>
    /// <param name="updatedAtUtc">UTC simulation time of the account's last mutation.</param>
    /// <exception cref="ArgumentException">An invariant is violated.</exception>
    public LocalUser(
        LocalUserId id,
        LocalLoginName loginName,
        string displayName,
        bool enabled,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId> additionalGroupIds,
        LocalPasswordCredential? credential,
        long revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A display name is required.", nameof(displayName));
        }

        if (authority == AppAuthority.System)
        {
            throw new ArgumentException(
                "Local user accounts cannot be granted System authority.", nameof(authority));
        }

        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Revision must be positive.");
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException(
                "Updated timestamp cannot precede the created timestamp.", nameof(updatedAtUtc));
        }

        Id = id;
        LoginName = loginName;
        DisplayName = displayName;
        Enabled = enabled;
        Authority = authority;
        PrimaryGroupId = primaryGroupId;
        AdditionalGroupIds = [.. additionalGroupIds];
        Credential = credential;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Gets the opaque, immutable user identifier.</summary>
    public LocalUserId Id { get; }

    /// <summary>Gets the normalized, unique, case-insensitive login name.</summary>
    public LocalLoginName LoginName { get; }

    /// <summary>Gets the mutable display name shown in the UI.</summary>
    public string DisplayName { get; }

    /// <summary>Gets whether the account can start a new session.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the login authority granted to sessions started by this user.</summary>
    public AppAuthority Authority { get; }

    /// <summary>Gets the user's primary group.</summary>
    public LocalGroupId PrimaryGroupId { get; }

    /// <summary>Gets the additional groups the user belongs to.</summary>
    public IReadOnlyCollection<LocalGroupId> AdditionalGroupIds { get; }

    /// <summary>Gets the optional local password credential; <see langword="null"/> means no password is set.</summary>
    public LocalPasswordCredential? Credential { get; }

    /// <summary>Gets the monotonic revision incremented on every mutation, for optimistic concurrency.</summary>
    public long Revision { get; }

    /// <summary>Gets the UTC simulation time the account was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the UTC simulation time of the account's last mutation.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Gets the WebAuthn passkeys that can sign this user in (ADR 0168); empty by default.</summary>
    public IReadOnlyList<LocalPasskeyCredential> Passkeys { get; init; } = [];

    /// <summary>
    /// Gets whether this is the guest account of ADR 0187: the passwordless administrator offered
    /// while no registered account exists. The mark, not the login name, identifies it.
    /// </summary>
    public bool IsGuest { get; init; }

    /// <summary>Gets the user's home directory path, derived from the login name.</summary>
    public string HomePath => $"/home/{LoginName.Value}";
}
