namespace Kazo.Os.Platform.Blazor.Hosting;

/// <summary>Render-mode-agnostic host environment classification, supplied by each host's own Program.cs.</summary>
public interface IEcosystemHostEnvironment
{
    /// <summary>True when the host runs in its Development environment, which enables developer-only diagnostics.</summary>
    bool IsDevelopment { get; }
}
