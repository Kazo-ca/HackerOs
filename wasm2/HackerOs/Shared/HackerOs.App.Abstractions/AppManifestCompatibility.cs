namespace HackerOs.App.Abstractions;

/// <summary>
/// Declares an optional bound on compatible KazoOS operating system versions.
/// </summary>
/// <param name="MinimumVersion">Lowest compatible OS semantic version.</param>
/// <param name="MaximumVersion">Highest compatible OS semantic version, or no upper bound.</param>
public sealed record OsCompatibilityManifest(string MinimumVersion, string? MaximumVersion = null);
