namespace Kazo.Os.Simulation.Abstractions.Time;

/// <summary>
/// Provides a stable, seeded, domain-keyed source of pseudo-random values for simulation.
/// </summary>
/// <remarks>
/// Never use this abstraction for cryptographic IDs, tokens, signatures, or secrets; use a
/// reviewed cryptographic RNG for those instead, per ADR 0012.
/// </remarks>
public interface ISimulationRandom
{
    /// <summary>
    /// Gets an independent, deterministic random stream for one stable domain key, such as
    /// <c>process:{pid}:resources</c>.
    /// </summary>
    /// <param name="domainKey">Stable, non-empty key identifying the calling subsystem's stream.</param>
    /// <returns>A random stream whose sequence depends only on the root seed and this key.</returns>
    ISimulationRandomStream GetStream(string domainKey);
}

/// <summary>One independent deterministic pseudo-random sequence.</summary>
public interface ISimulationRandomStream
{
    /// <summary>Returns a random integer in <c>[minInclusive, maxExclusive)</c>.</summary>
    int Next(int minInclusive, int maxExclusive);

    /// <summary>Returns a random double in <c>[0.0, 1.0)</c>.</summary>
    double NextDouble();
}
