using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Elevation;

namespace Kazo.Os.Platform.Core.Tests.Elevation;

/// <summary>Tests for `ADR 0042`'s single-use, short-lived elevation ticket store.</summary>
public sealed class InMemoryElevationTicketStoreTests
{
    [Fact]
    public void A_freshly_issued_ticket_is_consumed_exactly_once()
    {
        InMemoryElevationTicketStore store = new();
        ElevationTicket ticket = store.Issue(ElevationScope.SudoRoot, "admin-id", "admin");

        bool first = store.TryConsume(ticket.Token, ElevationScope.SudoRoot, out ElevationTicket? firstResult);
        bool second = store.TryConsume(ticket.Token, ElevationScope.SudoRoot, out ElevationTicket? secondResult);

        Assert.True(first);
        Assert.Equal(ticket.Token, firstResult?.Token);
        Assert.False(second);
        Assert.Null(secondResult);
    }

    [Fact]
    public void An_unknown_token_is_never_consumable()
    {
        InMemoryElevationTicketStore store = new();

        bool consumed = store.TryConsume(Guid.NewGuid(), ElevationScope.SudoRoot, out ElevationTicket? ticket);

        Assert.False(consumed);
        Assert.Null(ticket);
    }

    [Fact]
    public void A_ticket_is_not_consumable_under_the_wrong_scope()
    {
        InMemoryElevationTicketStore store = new();
        ElevationTicket ticket = store.Issue(ElevationScope.SudoRoot, "admin-id", "admin");

        bool consumed = store.TryConsume(ticket.Token, ElevationScope.CapabilityApproval, out ElevationTicket? result);

        Assert.False(consumed);
        Assert.Null(result);
    }

    [Fact]
    public void A_ticket_older_than_the_ttl_is_denied()
    {
        DateTimeOffset issuedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = issuedAt;
        InMemoryElevationTicketStore store = new(() => now);
        ElevationTicket ticket = store.Issue(ElevationScope.SudoRoot, "admin-id", "admin");

        now = issuedAt.AddSeconds(31);
        bool consumed = store.TryConsume(ticket.Token, ElevationScope.SudoRoot, out ElevationTicket? result);

        Assert.False(consumed);
        Assert.Null(result);
    }

    [Fact]
    public void A_capability_approval_ticket_carries_its_capability_and_path()
    {
        InMemoryElevationTicketStore store = new();
        ElevationTicket ticket = store.Issue(
            ElevationScope.CapabilityApproval, "admin-id", "admin",
            "filesystem.system.read", VirtualPath.Parse("/etc"));

        Assert.True(store.TryConsume(ticket.Token, ElevationScope.CapabilityApproval, out ElevationTicket? result));
        Assert.Equal("filesystem.system.read", result?.Capability);
        Assert.Equal(VirtualPath.Parse("/etc"), result?.Path);
    }
}
