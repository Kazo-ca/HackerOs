namespace Kazo.Os.Simulation.Abstractions.Events;

/// <summary>Reports that one catalog app has transitioned to disabled.</summary>
/// <param name="AppId">Exact immutable app identifier.</param>
public sealed record AppDisabledEvent(string AppId);