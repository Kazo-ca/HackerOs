using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Tests.Support;

/// <summary>Recording <see cref="IAppLoggingGateway"/> double: keeps every entry so a test can assert on it.</summary>
public sealed class FakeAppLoggingGateway : IAppLoggingGateway
{
    private readonly List<(DiagnosticSeverity Severity, string Message)> _entries = [];

    /// <summary>Gets every entry logged so far, in order.</summary>
    public IReadOnlyList<(DiagnosticSeverity Severity, string Message)> Entries => _entries;

    public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null) =>
        _entries.Add((severity, message));
}
