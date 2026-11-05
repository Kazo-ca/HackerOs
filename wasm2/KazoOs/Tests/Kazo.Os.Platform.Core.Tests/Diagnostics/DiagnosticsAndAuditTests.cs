using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;

namespace Kazo.Os.Platform.Core.Tests.Diagnostics;

public sealed class BoundedDiagnosticSinkTests
{
    [Fact]
    public void Recording_beyond_capacity_evicts_the_oldest_entry()
    {
        BoundedDiagnosticSink sink = new(maxEntries: 2);

        sink.Record(Entry("first"));
        sink.Record(Entry("second"));
        sink.Record(Entry("third"));

        Assert.Equal(["second", "third"], sink.Entries.Select(e => e.Message));
    }

    [Fact]
    public void Clear_discards_every_retained_entry()
    {
        BoundedDiagnosticSink sink = new(maxEntries: 10);
        sink.Record(Entry("first"));
        sink.Record(Entry("second"));

        sink.Clear();

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void Sensitive_property_values_are_redacted_before_storage()
    {
        BoundedDiagnosticSink sink = new(maxEntries: 10);

        sink.Record(new DiagnosticEntry(
            DateTimeOffset.UnixEpoch,
            DiagnosticSeverity.Information,
            "auth",
            "login attempt",
            Guid.NewGuid(),
            new Dictionary<string, string> { ["password"] = "hunter2", ["user"] = "alice" }));

        DiagnosticEntry stored = Assert.Single(sink.Entries);
        Assert.Equal("***redacted***", stored.Properties["password"]);
        Assert.Equal("alice", stored.Properties["user"]);
    }

    [Fact]
    public void Category_and_message_cannot_be_empty()
    {
        Assert.Throws<ArgumentException>(() => new DiagnosticEntry(
            DateTimeOffset.UnixEpoch, DiagnosticSeverity.Information, "", "message", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new DiagnosticEntry(
            DateTimeOffset.UnixEpoch, DiagnosticSeverity.Information, "category", "", Guid.NewGuid()));
    }

    private static DiagnosticEntry Entry(string message) => new(
        DateTimeOffset.UnixEpoch, DiagnosticSeverity.Information, "test", message, Guid.NewGuid());
}

public sealed class EventPublishingDiagnosticSinkTests
{
    [Fact]
    public void Clear_delegates_to_the_inner_sink_and_publishes_a_cleared_event()
    {
        BoundedDiagnosticSink inner = new(maxEntries: 10);
        InMemoryEventBus eventBus = new();
        EventPublishingDiagnosticSink sink = new(inner, eventBus);
        sink.Record(new DiagnosticEntry(
            DateTimeOffset.UnixEpoch, DiagnosticSeverity.Information, "test", "hello", Guid.NewGuid()));

        List<DiagnosticLogClearedEvent> observed = [];
        using IDisposable subscription = eventBus.Subscribe<DiagnosticLogClearedEvent>(observed.Add);

        sink.Clear();

        Assert.Empty(inner.Entries);
        Assert.Empty(sink.Entries);
        Assert.Single(observed);
    }
}

public sealed class BoundedAuditLogTests
{
    [Fact]
    public void Recording_beyond_capacity_evicts_the_oldest_entry()
    {
        BoundedAuditLog log = new(maxEntries: 2);

        log.Record(Entry("subject-1"));
        log.Record(Entry("subject-2"));
        log.Record(Entry("subject-3"));

        Assert.Equal(["subject-2", "subject-3"], log.Entries.Select(e => e.Subject));
    }

    [Fact]
    public void Sensitive_property_values_are_redacted_before_storage()
    {
        BoundedAuditLog log = new(maxEntries: 10);

        log.Record(new AuditEntry(
            DateTimeOffset.UnixEpoch,
            Guid.NewGuid(),
            "alice",
            "credential.set",
            "alice",
            AuditOutcome.Success,
            new Dictionary<string, string> { ["verifier"] = "abc123", ["kdf"] = "pbkdf2-sha256-v1" }));

        AuditEntry stored = Assert.Single(log.Entries);
        Assert.Equal("***redacted***", stored.Properties["verifier"]);
        Assert.Equal("pbkdf2-sha256-v1", stored.Properties["kdf"]);
    }

    [Fact]
    public void Actor_action_and_subject_cannot_be_empty()
    {
        Assert.Throws<ArgumentException>(() => new AuditEntry(
            DateTimeOffset.UnixEpoch, Guid.NewGuid(), "", "action", "subject", AuditOutcome.Success));
        Assert.Throws<ArgumentException>(() => new AuditEntry(
            DateTimeOffset.UnixEpoch, Guid.NewGuid(), "actor", "", "subject", AuditOutcome.Success));
        Assert.Throws<ArgumentException>(() => new AuditEntry(
            DateTimeOffset.UnixEpoch, Guid.NewGuid(), "actor", "action", "", AuditOutcome.Success));
    }

    private static AuditEntry Entry(string subject) => new(
        DateTimeOffset.UnixEpoch, Guid.NewGuid(), "actor", "action", subject, AuditOutcome.Success);
}
