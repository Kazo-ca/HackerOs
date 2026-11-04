using System.Collections.Concurrent;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;

namespace Kazo.Os.Platform.Core.Elevation;

/// <summary>
/// In-memory, single-use elevation ticket store (`ADR 0042`). A ticket is minted after live
/// Administrator re-authentication and consumed exactly once by <see cref="TryConsume"/>, with a
/// bounded TTL so an unused ticket cannot linger indefinitely.
/// </summary>
public sealed class InMemoryElevationTicketStore : IElevationTicketStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, ElevationTicket> _tickets = new();
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Initializes the store, optionally overriding the clock for tests.</summary>
    public InMemoryElevationTicketStore(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public ElevationTicket Issue(
        ElevationScope scope,
        string authorizedByUserId,
        string authorizedByLoginName,
        string? capability = null,
        VirtualPath? path = null)
    {
        ElevationTicket ticket = new(Guid.NewGuid(), scope, authorizedByUserId, authorizedByLoginName, _clock(), capability, path);
        _tickets[ticket.Token] = ticket;
        return ticket;
    }

    /// <inheritdoc />
    public bool TryConsume(Guid token, ElevationScope expectedScope, out ElevationTicket? ticket)
    {
        if (_tickets.TryRemove(token, out ElevationTicket? found)
            && found.Scope == expectedScope
            && _clock() - found.IssuedAtUtc <= Ttl)
        {
            ticket = found;
            return true;
        }

        ticket = null;
        return false;
    }
}
