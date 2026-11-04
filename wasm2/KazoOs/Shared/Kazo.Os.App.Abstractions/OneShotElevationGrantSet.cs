using System.Collections.Concurrent;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// A small, per-launch, thread-safe set of one-shot elevation tickets minted after live
/// Administrator approval. Each ticket unblocks exactly one subsequent filesystem authorization
/// decision matching its (capability, path), then is immediately consumed. Never persisted;
/// scoped to the lifetime of one <see cref="AppOperationContext"/>.
/// </summary>
public sealed class OneShotElevationGrantSet
{
    private readonly ConcurrentDictionary<(string Capability, string? Path), byte> _tickets = new();
    private readonly ConcurrentDictionary<string, byte> _blocked = new(StringComparer.Ordinal);

    /// <summary>
    /// Grants a one-shot ticket for the exact capability/path combination. Ignored for a capability
    /// blocked with <see cref="Block"/>.
    /// </summary>
    public void Grant(string capability, VirtualPath? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (IsBlocked(capability))
        {
            return;
        }

        _tickets[(capability, path?.Value)] = 0;
    }

    /// <summary>Attempts to consume a matching ticket; a ticket unblocks exactly one call.</summary>
    public bool TryConsume(string capability, VirtualPath? path) =>
        !IsBlocked(capability) && _tickets.TryRemove((capability, path?.Value), out _);

    /// <summary>
    /// Refuses every one-shot approval of <paramref name="capability"/> for this instance. Set at
    /// launch for the dangerous capabilities of a non-system app when
    /// <c>/etc/hackeros/security.config</c> says <c>dangerousCapabilities=block</c> (ADR 0049
    /// section 3); <c>sudo</c> is not concerned.
    /// </summary>
    /// <param name="capability">Capability whose one-shot approval is refused.</param>
    public void Block(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        _blocked[capability] = 0;
        foreach ((string Capability, string? Path) key in _tickets.Keys)
        {
            if (string.Equals(key.Capability, capability, StringComparison.Ordinal))
            {
                _tickets.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Gets whether one-shot approval of <paramref name="capability"/> is refused (see <see cref="Block"/>).</summary>
    /// <param name="capability">Capability to test.</param>
    public bool IsBlocked(string capability) => capability is not null && _blocked.ContainsKey(capability);
}
