using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>Applies an <see cref="IDiagnosticRedactor"/> to a diagnostic entry before it is stored or written.</summary>
internal static class DiagnosticRedaction
{
    /// <summary>Returns <paramref name="entry"/> with every property value passed through <paramref name="redactor"/>.</summary>
    public static DiagnosticEntry Apply(DiagnosticEntry entry, IDiagnosticRedactor redactor)
    {
        if (entry.Properties.Count == 0)
        {
            return entry;
        }

        Dictionary<string, string> properties = entry.Properties.ToDictionary(
            pair => pair.Key,
            pair => redactor.Redact(pair.Key, pair.Value),
            StringComparer.Ordinal);
        return new DiagnosticEntry(
            entry.TimestampUtc,
            entry.Severity,
            entry.Category,
            entry.Message,
            entry.CorrelationId,
            properties);
    }
}
