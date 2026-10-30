using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;

namespace HackerOs.Platform.Core.Diagnostics;

/// <summary>
/// Decorates an <see cref="IDiagnosticSink"/> so every recorded entry also publishes a
/// <see cref="DiagnosticEntryRecordedEvent"/>, letting live viewers (e.g. Error Log Viewer)
/// update without polling, and mirrors entries above <see cref="DiagnosticSeverity.Information"/>
/// to the browser devtools console. Every existing producer (<c>AppLoggingGateway</c>,
/// <c>HostExceptionReporter</c>, the diagnostic <c>ILogger</c>) already depends on
/// <see cref="IDiagnosticSink"/> through DI rather than constructing the inner sink directly,
/// so this is the single interception point for all of them.
/// </summary>
public sealed class EventPublishingDiagnosticSink : IDiagnosticSink
{
    private readonly IDiagnosticSink _inner;
    private readonly IEventBus _eventBus;

    /// <summary>Initializes a decorator that publishes through <paramref name="eventBus"/>.</summary>
    public EventPublishingDiagnosticSink(IDiagnosticSink inner, IEventBus eventBus)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    }

    /// <inheritdoc />
    public void Record(DiagnosticEntry entry)
    {
        _inner.Record(entry);

        if (entry.Severity > DiagnosticSeverity.Information)
        {
            // In Blazor WebAssembly, Console.Error is redirected to the browser's console.error by
            // the runtime, so this needs no IJSRuntime/JS interop. Blazor also shows its "An
            // unhandled error has occurred" bar for every standard error line: a warning, which is
            // no failure, goes to standard output (console.log) instead.
            TextWriter writer = entry.Severity == DiagnosticSeverity.Warning ? Console.Out : Console.Error;
            writer.WriteLine($"[{entry.Category}] {entry.Severity}: {entry.Message}");
        }

        _eventBus.Publish(new DiagnosticEntryRecordedEvent(entry));
    }

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticEntry> Entries => _inner.Entries;

    /// <inheritdoc />
    public void Clear()
    {
        _inner.Clear();
        _eventBus.Publish(new DiagnosticLogClearedEvent(DateTimeOffset.UtcNow));
    }
}
