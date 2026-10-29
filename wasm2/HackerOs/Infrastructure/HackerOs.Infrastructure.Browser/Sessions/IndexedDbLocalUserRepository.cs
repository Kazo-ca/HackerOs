using System.Text.Json;
using System.Text.Json.Serialization;
using HackerOs.App.Abstractions;
using HackerOs.Infrastructure.Browser.Interop;
using HackerOs.Infrastructure.Browser.Schema;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.JSInterop;

namespace HackerOs.Infrastructure.Browser.Sessions;

/// <summary>Persists local user accounts and password verifiers in the canonical browser database.</summary>
public sealed partial class IndexedDbLocalUserRepository : ILocalUserRepository, IAsyncDisposable
{
    private const string TransactionBoundary = "UserAccountWrite";
    private readonly IndexedDbInteropAdapter _adapter;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a browser-backed local user repository.</summary>
    public IndexedDbLocalUserRepository(IJSRuntime runtime, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<LocalUser> CreateUserAsync(
        LocalLoginName loginName,
        string displayName,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId>? additionalGroupIds = null,
        LocalPasswordCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
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

        await AddAsync(user, cancellationToken).ConfigureAwait(false);
        return user;
    }

    /// <inheritdoc />
    public async ValueTask<LocalUser> CreateGuestUserAsync(
        LocalLoginName loginName,
        string displayName,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId> additionalGroupIds,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        LocalUser guest = new(
            LocalUserId.FromGuid(Guid.NewGuid()),
            loginName,
            displayName,
            enabled: true,
            authority,
            primaryGroupId,
            additionalGroupIds,
            credential: null,
            revision: 1,
            now,
            now)
        {
            IsGuest = true
        };

        await AddAsync(guest, cancellationToken).ConfigureAwait(false);
        return guest;
    }

    /// <inheritdoc />
    public async ValueTask<LocalUser> ConvertGuestUserAsync(
        LocalUserId id,
        LocalLoginName loginName,
        string displayName,
        LocalPasswordCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        LocalUser? current = await FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is not { IsGuest: true })
        {
            throw new InvalidOperationException($"Local user '{id}' is not the guest account.");
        }

        try
        {
            return await MutateAsync(
                id,
                user => user with
                {
                    LoginName = loginName.Value,
                    DisplayName = displayName,
                    Credential = LocalCredentialRecord.FromDomain(credential),
                    IsGuest = null
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception) when (exception.Message.Contains("ConstraintError", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Login name '{loginName}' is already in use.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<LocalUser?> FindByIdAsync(
        LocalUserId id,
        CancellationToken cancellationToken = default) =>
        await FindOneAsync(id.ToString(), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<LocalUser?> FindByLoginNameAsync(
        LocalLoginName loginName,
        CancellationToken cancellationToken = default) =>
        await FindOneAsync(loginName.Value, "loginName", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LocalUser>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("getAll", HackerOsIndexedDbSchema.UserStoreName)],
            cancellationToken).ConfigureAwait(false);

        return results.Single().EnumerateArray()
            .Select(LocalUserRecord.ToDomain)
            .OrderBy(user => user.LoginName.Value, StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> SetEnabledAsync(
        LocalUserId id,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        MutateAsync(id, user => user with { Enabled = enabled }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<LocalUser> SetAuthorityAsync(
        LocalUserId id,
        AppAuthority authority,
        CancellationToken cancellationToken = default)
    {
        if (authority == AppAuthority.System)
        {
            throw new ArgumentException("Local user accounts cannot be granted System authority.", nameof(authority));
        }

        return MutateAsync(id, user => user with { Authority = authority }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<LocalUser> SetCredentialAsync(
        LocalUserId id,
        LocalPasswordCredential? credential,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            id,
            user => user with { Credential = credential is null ? null : LocalCredentialRecord.FromDomain(credential) },
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<LocalUser> SetPasskeysAsync(
        LocalUserId id,
        IReadOnlyList<LocalPasskeyCredential> passkeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkeys);
        LocalPasskeyRecord[] records = [.. passkeys.Select(LocalPasskeyRecord.FromDomain)];
        return MutateAsync(id, user => user with { Passkeys = records }, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DeleteUserAsync(LocalUserId id, CancellationToken cancellationToken = default)
    {
        LocalUser[] users = [.. await GetAllAsync(cancellationToken).ConfigureAwait(false)];
        LocalUser current = users.SingleOrDefault(user => user.Id == id)
            ?? throw new KeyNotFoundException($"No local user with ID '{id}'.");
        if (current.Enabled
            && current.Authority == AppAuthority.Administrator
            && users.Count(user => user.Enabled && user.Authority == AppAuthority.Administrator) == 1)
        {
            throw new InvalidOperationException("Cannot delete the last enabled Administrator.");
        }

        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation("delete", HackerOsIndexedDbSchema.UserStoreName, Key: current.Id.ToString())],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private async ValueTask<LocalUser?> FindOneAsync(
        object key,
        CancellationToken cancellationToken) =>
        await FindOneAsync(key, indexName: null, cancellationToken).ConfigureAwait(false);

    private async ValueTask<LocalUser?> FindOneAsync(
        object key,
        string? indexName,
        CancellationToken cancellationToken)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("get", HackerOsIndexedDbSchema.UserStoreName, Key: key, IndexName: indexName)],
            cancellationToken).ConfigureAwait(false);

        JsonElement result = results.Single();
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : LocalUserRecord.ToDomain(result);
    }

    private async ValueTask<LocalUser> MutateAsync(
        LocalUserId id,
        Func<LocalUserRecord, LocalUserRecord> mutation,
        CancellationToken cancellationToken)
    {
        LocalUser[] users = [.. await GetAllAsync(cancellationToken).ConfigureAwait(false)];
        LocalUser current = users.SingleOrDefault(user => user.Id == id)
            ?? throw new InvalidOperationException($"Local user '{id}' does not exist.");

        LocalUserRecord updated = mutation(LocalUserRecord.FromDomain(current)) with
        {
            Revision = current.Revision + 1,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };

        if ((!updated.Enabled || updated.Authority != AppAuthority.Administrator)
            && current.Enabled
            && current.Authority == AppAuthority.Administrator
            && users.Count(user => user.Enabled && user.Authority == AppAuthority.Administrator) == 1)
        {
            throw new InvalidOperationException("Cannot disable or demote the last enabled Administrator.");
        }

        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation(
                "compareAndPut",
                HackerOsIndexedDbSchema.UserStoreName,
                Key: current.Id.ToString(),
                Value: updated,
                CompareProperty: "revision",
                ExpectedValue: current.Revision)],
            cancellationToken).ConfigureAwait(false);

        JsonElement result = results.Single();
        if (!result.GetProperty("committed").GetBoolean())
        {
            throw new InvalidOperationException("The local user changed in another browser context.");
        }

        return updated.ToDomain();
    }

    private async ValueTask AddAsync(LocalUser user, CancellationToken cancellationToken)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _adapter.ExecuteAsync(
                TransactionBoundary,
                IndexedDbTransactionMode.ReadWrite,
                [new IndexedDbOperation("add", HackerOsIndexedDbSchema.UserStoreName, Value: LocalUserRecord.FromDomain(user))],
                cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception) when (exception.Message.Contains("ConstraintError", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Login name '{user.LoginName}' is already in use.", exception);
        }
    }

    private ValueTask EnsureOpenAsync(CancellationToken cancellationToken) =>
        _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken);

    private sealed record LocalUserRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("loginName")] string LoginName,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("authority")] AppAuthority Authority,
        [property: JsonPropertyName("primaryGroupId")] string PrimaryGroupId,
        [property: JsonPropertyName("additionalGroupIds")] string[] AdditionalGroupIds,
        [property: JsonPropertyName("credential")] LocalCredentialRecord? Credential,
        [property: JsonPropertyName("revision")] long Revision,
        [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
        [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
        [property: JsonPropertyName("passkeys")] LocalPasskeyRecord[]? Passkeys = null,
        [property: JsonPropertyName("isGuest")] bool? IsGuest = null)
    {
        internal static LocalUserRecord FromDomain(LocalUser user) => new(
            user.Id.ToString(),
            user.LoginName.Value,
            user.DisplayName,
            user.Enabled,
            user.Authority,
            user.PrimaryGroupId.ToString(),
            user.AdditionalGroupIds.Select(id => id.ToString()).ToArray(),
            user.Credential is null ? null : LocalCredentialRecord.FromDomain(user.Credential),
            user.Revision,
            user.CreatedAtUtc,
            user.UpdatedAtUtc,
            user.Passkeys.Count == 0 ? null : [.. user.Passkeys.Select(LocalPasskeyRecord.FromDomain)],
            user.IsGuest ? true : null);

        internal static LocalUser ToDomain(JsonElement element) =>
            JsonSerializer.Deserialize(element, LocalUserJsonContext.Default.LocalUserRecord)!.ToDomain();

        internal LocalUser ToDomain() => new(
            LocalUserId.FromGuid(Guid.ParseExact(Id, "N")),
            LocalLoginName.Parse(LoginName),
            DisplayName,
            Enabled,
            Authority,
            LocalGroupId.FromGuid(Guid.ParseExact(PrimaryGroupId, "N")),
            AdditionalGroupIds.Select(id => LocalGroupId.FromGuid(Guid.ParseExact(id, "N"))).ToArray(),
            Credential?.ToDomain(),
            Revision,
            CreatedAtUtc,
            UpdatedAtUtc)
        {
            // Records written before ADR 0168 have no "passkeys" property.
            Passkeys = this.Passkeys is null ? [] : [.. this.Passkeys.Select(static passkey => passkey.ToDomain())],
            // Only the guest account of ADR 0187 has the property.
            IsGuest = this.IsGuest == true
        };
    }

    private sealed record LocalPasskeyRecord(
        [property: JsonPropertyName("credentialId")] string CredentialId,
        [property: JsonPropertyName("publicKey")] string PublicKey,
        [property: JsonPropertyName("algorithm")] int Algorithm,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("signCount")] uint SignCount,
        [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
        [property: JsonPropertyName("lastUsedAtUtc")] DateTimeOffset? LastUsedAtUtc,
        [property: JsonPropertyName("prfSalt")] string? PrfSalt)
    {
        internal static LocalPasskeyRecord FromDomain(LocalPasskeyCredential passkey) => new(
            Convert.ToBase64String(passkey.CredentialId),
            Convert.ToBase64String(passkey.PublicKey),
            passkey.Algorithm,
            passkey.Name,
            passkey.SignCount,
            passkey.CreatedAtUtc,
            passkey.LastUsedAtUtc,
            passkey.PrfSalt is null ? null : Convert.ToBase64String(passkey.PrfSalt));

        internal LocalPasskeyCredential ToDomain() => new(
            Convert.FromBase64String(CredentialId),
            Convert.FromBase64String(PublicKey),
            Algorithm,
            Name,
            SignCount,
            CreatedAtUtc,
            LastUsedAtUtc,
            PrfSalt is null ? null : Convert.FromBase64String(PrfSalt));
    }

    private sealed record LocalCredentialRecord(
        [property: JsonPropertyName("kdfIdentifier")] string KdfIdentifier,
        [property: JsonPropertyName("salt")] string Salt,
        [property: JsonPropertyName("iterations")] int Iterations,
        [property: JsonPropertyName("verifier")] string Verifier)
    {
        internal static LocalCredentialRecord FromDomain(LocalPasswordCredential credential) =>
            new(
                credential.KdfIdentifier,
                Convert.ToBase64String(credential.Salt),
                credential.Iterations,
                Convert.ToBase64String(credential.Verifier));

        internal LocalPasswordCredential ToDomain() => new(
            KdfIdentifier,
            Convert.FromBase64String(Salt),
            Iterations,
            Convert.FromBase64String(Verifier));
    }

    [JsonSerializable(typeof(LocalUserRecord))]
    [JsonSerializable(typeof(LocalCredentialRecord))]
    [JsonSerializable(typeof(LocalPasskeyRecord))]
    private sealed partial class LocalUserJsonContext : JsonSerializerContext;
}