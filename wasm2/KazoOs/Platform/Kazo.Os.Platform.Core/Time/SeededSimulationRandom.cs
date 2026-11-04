using System.Text;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Time;

/// <summary>
/// Deterministic <see cref="ISimulationRandom"/> that derives one independent stream per stable
/// domain key from a single recorded root seed.
/// </summary>
public sealed class SeededSimulationRandom(long rootSeed) : ISimulationRandom
{
    /// <inheritdoc />
    public ISimulationRandomStream GetStream(string domainKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainKey);

        int streamSeed = unchecked((int)CombineSeed(rootSeed, domainKey));
        return new RandomStream(new Random(streamSeed));
    }

    /// <summary>
    /// Combines the root seed and domain key using FNV-1a so the same pair always derives the
    /// same stream seed, independent of process hash randomization.
    /// </summary>
    private static long CombineSeed(long rootSeed, string domainKey)
    {
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        ulong hash = FnvOffsetBasis;
        foreach (byte b in Encoding.UTF8.GetBytes(rootSeed.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            hash = (hash ^ b) * FnvPrime;
        }

        foreach (byte b in Encoding.UTF8.GetBytes(domainKey))
        {
            hash = (hash ^ b) * FnvPrime;
        }

        return unchecked((long)hash);
    }

    private sealed class RandomStream(Random random) : ISimulationRandomStream
    {
        public int Next(int minInclusive, int maxExclusive) => random.Next(minInclusive, maxExclusive);

        public double NextDouble() => random.NextDouble();
    }
}
