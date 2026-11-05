using System.Collections.Frozen;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// Redacts structured property values whose key matches a known sensitive-data name, such as
/// <c>password</c> or <c>token</c>, independent of letter case.
/// </summary>
public sealed class SensitiveKeyDiagnosticRedactor : IDiagnosticRedactor
{
    private const string RedactionPlaceholder = "***redacted***";

    private static readonly FrozenSet<string> SensitiveKeys = new[]
    {
        "password",
        "secret",
        "token",
        "credential",
        "verifier",
        "salt",
        "authorization",
        "cookie",
        "apikey",
        "api-key"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string Redact(string propertyKey, string value) =>
        SensitiveKeys.Contains(propertyKey) ? RedactionPlaceholder : value;
}
