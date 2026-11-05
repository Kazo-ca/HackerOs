using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Keyring;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Keyring;

/// <summary>
/// Trusted <see cref="IAppKeyringGateway"/> of one app instance (ADR 0100 section 2): the session
/// user's keyring, confined to the app's own entries; another app's entries need the dangerous
/// capability <c>keyring.read-all</c>.
/// </summary>
internal sealed class AppKeyringGateway : IAppKeyringGateway
{
    private readonly UserKeyringService _keyring;
    private readonly LocalUserId _userId;
    private readonly string _appId;
    private readonly ICapabilityChecker _capabilities;

    /// <summary>Initializes the gateway.</summary>
    /// <param name="keyring">The session keyring.</param>
    /// <param name="userId">The user the app runs for; another user's keyring is never reachable.</param>
    /// <param name="appId">Calling app.</param>
    /// <param name="capabilities">The app's capability checker.</param>
    public AppKeyringGateway(UserKeyringService keyring, LocalUserId userId, string appId, ICapabilityChecker capabilities)
    {
        _keyring = keyring ?? throw new ArgumentNullException(nameof(keyring));
        _userId = userId;
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _appId = appId;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <inheritdoc />
    public bool IsUnlocked => _keyring.IsUnlockedFor(_userId);

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string? appId, string id, CancellationToken cancellationToken = default)
    {
        string owner = appId ?? _appId;
        if (!string.Equals(owner, _appId, StringComparison.Ordinal))
        {
            RequireReadAll();
        }

        return _keyring.GetAsync(_userId, owner, id, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string id, string secret, string? label, CancellationToken cancellationToken = default) =>
        _keyring.SetAsync(_userId, _appId, id, secret, label, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        _keyring.DeleteAsync(_userId, _appId, id, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoKeyringEntry>> ListAsync(bool allApps, CancellationToken cancellationToken = default)
    {
        if (allApps)
        {
            RequireReadAll();
        }

        IReadOnlyList<KeyringEntryRecord> entries = await _keyring
            .ListAsync(_userId, allApps ? null : _appId, cancellationToken)
            .ConfigureAwait(false);
        return [.. entries.Select(static entry => new KazoKeyringEntry(entry.AppId, entry.Id, entry.Label, entry.UpdatedAtUtc))];
    }

    private void RequireReadAll()
    {
        try
        {
            _capabilities.Require(AppCapabilities.KeyringReadAll);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
    }
}
