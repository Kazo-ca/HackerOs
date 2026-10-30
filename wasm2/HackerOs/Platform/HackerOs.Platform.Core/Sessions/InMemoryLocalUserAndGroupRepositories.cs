using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Sessions;

/// <summary>In-memory <see cref="ILocalUserRepository"/> enforcing the ADR 0013 last-administrator rule.</summary>
public sealed class InMemoryLocalUserRepository : ILocalUserRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LocalUserId, LocalUser> _usersById = [];
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Initializes an in-memory user repository.</summary>
    /// <param name="clock">Clock used for created/updated timestamps; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryLocalUserRepository(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public LocalUser CreateUser(
        LocalLoginName loginName,
        string displayName,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId>? additionalGroupIds = null,
        LocalPasswordCredential? credential = null)
    {
        lock (_gate)
        {
            if (_usersById.Values.Any(existing => existing.LoginName == loginName))
            {
                throw new InvalidOperationException($"Login name '{loginName}' is already in use.");
            }

            DateTimeOffset now = _clock();
            LocalUser user = new(
                LocalUserId.FromGuid(Guid.NewGuid()),
                loginName,
                displayName,
                enabled: true,
                authority,
                primaryGroupId,
                additionalGroupIds ?? [],
                credential,
                revision: 1,
                now,
                now);

            _usersById[user.Id] = user;
            return user;
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> CreateUserAsync(
        LocalLoginName loginName,
        string displayName,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId>? additionalGroupIds = null,
        LocalPasswordCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateUser(
            loginName, displayName, authority, primaryGroupId, additionalGroupIds, credential));
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> CreateGuestUserAsync(
        LocalLoginName loginName,
        string displayName,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId> additionalGroupIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            LocalUser created = CreateUser(loginName, displayName, authority, primaryGroupId, additionalGroupIds) with { IsGuest = true };
            _usersById[created.Id] = created;
            return ValueTask.FromResult(created);
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> ConvertGuestUserAsync(
        LocalUserId id,
        LocalLoginName loginName,
        string displayName,
        LocalPasswordCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_usersById.TryGetValue(id, out LocalUser? guest) || !guest.IsGuest)
            {
                throw new InvalidOperationException($"Local user '{id}' is not the guest account.");
            }

            if (_usersById.Values.Any(existing => existing.Id != id && existing.LoginName == loginName))
            {
                throw new InvalidOperationException($"Login name '{loginName}' is already in use.");
            }

            LocalUser converted = new(
                guest.Id,
                loginName,
                displayName,
                guest.Enabled,
                guest.Authority,
                guest.PrimaryGroupId,
                guest.AdditionalGroupIds,
                credential,
                guest.Revision + 1,
                guest.CreatedAtUtc,
                _clock())
            {
                Passkeys = guest.Passkeys,
                IsGuest = false
            };
            _usersById[id] = converted;
            return ValueTask.FromResult(converted);
        }
    }

    /// <inheritdoc />
    public bool TryGetById(LocalUserId id, out LocalUser user)
    {
        lock (_gate)
        {
            return _usersById.TryGetValue(id, out user!);
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalUser?> FindByIdAsync(
        LocalUserId id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryGetById(id, out LocalUser? user) ? user : null);
    }

    /// <inheritdoc />
    public bool TryGetByLoginName(LocalLoginName loginName, out LocalUser user)
    {
        lock (_gate)
        {
            user = _usersById.Values.FirstOrDefault(existing => existing.LoginName == loginName)!;
            return user is not null;
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalUser?> FindByLoginNameAsync(
        LocalLoginName loginName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryGetByLoginName(loginName, out LocalUser? user) ? user : null);
    }

    /// <inheritdoc />
    public IReadOnlyList<LocalUser> GetAll()
    {
        lock (_gate)
        {
            return [.. _usersById.Values];
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LocalUser>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetAll());
    }

    /// <inheritdoc />
    public LocalUser SetEnabled(LocalUserId id, bool enabled)
    {
        lock (_gate)
        {
            LocalUser user = GetRequired(id);
            if (!enabled && IsLastEnabledAdministrator(user))
            {
                throw new InvalidOperationException("Cannot disable the last enabled Administrator.");
            }

            LocalUser updated = WithMutation(user, enabled: enabled);
            _usersById[id] = updated;
            return updated;
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> SetEnabledAsync(
        LocalUserId id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(SetEnabled(id, enabled));
    }

    /// <inheritdoc />
    public LocalUser SetAuthority(LocalUserId id, AppAuthority authority)
    {
        lock (_gate)
        {
            LocalUser user = GetRequired(id);
            if (authority != AppAuthority.Administrator && IsLastEnabledAdministrator(user))
            {
                throw new InvalidOperationException("Cannot demote the last enabled Administrator.");
            }

            LocalUser updated = WithMutation(user, authority: authority);
            _usersById[id] = updated;
            return updated;
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> SetAuthorityAsync(
        LocalUserId id,
        AppAuthority authority,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(SetAuthority(id, authority));
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> SetCredentialAsync(
        LocalUserId id,
        LocalPasswordCredential? credential,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            LocalUser user = GetRequired(id);
            LocalUser updated = WithMutation(user, credential: new CredentialChange(credential));
            _usersById[id] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> SetPasskeysAsync(
        LocalUserId id,
        IReadOnlyList<LocalPasskeyCredential> passkeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkeys);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            LocalUser user = GetRequired(id);
            LocalUser updated = WithMutation(user) with { Passkeys = [.. passkeys] };
            _usersById[id] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteUserAsync(LocalUserId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            LocalUser user = GetRequired(id);
            if (IsLastEnabledAdministrator(user))
            {
                throw new InvalidOperationException("Cannot delete the last enabled Administrator.");
            }

            _usersById.Remove(id);
            return ValueTask.CompletedTask;
        }
    }

    private bool IsLastEnabledAdministrator(LocalUser user) =>
        user.Enabled
        && user.Authority == AppAuthority.Administrator
        && _usersById.Values.Count(existing => existing.Enabled && existing.Authority == AppAuthority.Administrator) <= 1;

    private LocalUser GetRequired(LocalUserId id) => _usersById.TryGetValue(id, out LocalUser? user)
        ? user
        : throw new KeyNotFoundException($"No local user with ID '{id}'.");

    private LocalUser WithMutation(
        LocalUser user,
        bool? enabled = null,
        AppAuthority? authority = null,
        CredentialChange? credential = null) => new(
        user.Id,
        user.LoginName,
        user.DisplayName,
        enabled ?? user.Enabled,
        authority ?? user.Authority,
        user.PrimaryGroupId,
        user.AdditionalGroupIds,
        credential is { } change ? change.Credential : user.Credential,
        user.Revision + 1,
        user.CreatedAtUtc,
        _clock())
    {
        Passkeys = user.Passkeys,
        IsGuest = user.IsGuest
    };

    /// <summary>A credential to set, possibly <see langword="null"/> (no password), as opposed to no change.</summary>
    private readonly record struct CredentialChange(LocalPasswordCredential? Credential);
}

/// <summary>In-memory <see cref="ILocalGroupRepository"/>.</summary>
public sealed class InMemoryLocalGroupRepository : ILocalGroupRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LocalGroupId, LocalGroup> _groupsById = [];

    /// <inheritdoc />
    public LocalGroup CreateGroup(LocalLoginName name)
    {
        lock (_gate)
        {
            LocalGroup group = new(LocalGroupId.FromGuid(Guid.NewGuid()), name);
            _groupsById[group.Id] = group;
            return group;
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalGroup> CreateGroupAsync(
        LocalLoginName name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateGroup(name));
    }

    /// <inheritdoc />
    public bool TryGetById(LocalGroupId id, out LocalGroup group)
    {
        lock (_gate)
        {
            return _groupsById.TryGetValue(id, out group!);
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalGroup?> FindByIdAsync(
        LocalGroupId id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryGetById(id, out LocalGroup? group) ? group : null);
    }

    /// <inheritdoc />
    public ValueTask<LocalGroup?> FindByNameAsync(
        LocalLoginName name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<LocalGroup?>(
                _groupsById.Values.FirstOrDefault(group => group.Name == name));
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalGroup> RenameGroupAsync(
        LocalGroupId id,
        LocalLoginName name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_groupsById.ContainsKey(id))
            {
                throw new InvalidOperationException($"Local group '{id}' does not exist.");
            }

            if (_groupsById.Values.Any(group => group.Id != id && group.Name == name))
            {
                throw new InvalidOperationException($"Group name '{name}' is already in use.");
            }

            LocalGroup renamed = new(id, name);
            _groupsById[id] = renamed;
            return ValueTask.FromResult(renamed);
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteGroupAsync(LocalGroupId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _groupsById.Remove(id);
            return ValueTask.CompletedTask;
        }
    }
}
