namespace Kazo.Os.Platform.RemoteRendering.Host;

/// <summary>
/// Host setting of the trial of window apps rendered in a worker (ADR 0129, ADR 0133), read from
/// <c>HackerOs:RemoteWindowRendering</c> in <c>appsettings.json</c>. Off by default: no app changes
/// where it renders unless a host turns it on.
/// </summary>
public sealed record RemoteWindowRenderingOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "HackerOs:RemoteWindowRendering";

    /// <summary>Gets whether the trial is on.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the ids of the apps rendered in a worker when the trial is on. The trial's subject is the
    /// Calculator (<c>org.hackeros.calculator</c>, ADR 0129 section 1).
    /// </summary>
    public IReadOnlyList<string> Apps { get; init; } = ["org.hackeros.calculator"];
}
