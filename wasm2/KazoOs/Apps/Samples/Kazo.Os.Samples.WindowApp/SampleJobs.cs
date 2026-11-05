using System.Text.Json.Serialization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Samples.WindowApp;

// ADR 0128 section 2: the inputs and results of this app's background tasks cross as JSON, through
// this context. The KOSBG analyzer refuses a task whose types it does not declare.
[assembly: KazoWireContext(typeof(SampleJobsJsonContext))]

namespace Kazo.Os.Samples.WindowApp;

/// <summary>The input of <see cref="SampleJobs.CountPrimes"/>: count the primes below <paramref name="Limit"/>.</summary>
/// <param name="Limit">Exclusive upper bound.</param>
public sealed record PrimeCountInput(int Limit);

/// <summary>The result of <see cref="SampleJobs.CountPrimes"/>.</summary>
/// <param name="Count">How many primes are below the limit.</param>
/// <param name="Largest">The largest of them, or 0.</param>
public sealed record PrimeCountResult(int Count, int Largest);

/// <summary>The serializer context of the sample's background tasks (ADR 0128, ADR 0124 section 2).</summary>
[JsonSerializable(typeof(PrimeCountInput))]
[JsonSerializable(typeof(PrimeCountResult))]
internal sealed partial class SampleJobsJsonContext : JsonSerializerContext;

/// <summary>
/// The sample's background tasks: static methods, so the OS can run them in a Web Worker (ADR 0128).
/// They see nothing of the window: only their input, their context and the SDK.
/// </summary>
public static class SampleJobs
{
    /// <summary>
    /// Counts primes by trial division, on purpose slow: a few seconds for a few million, the kind of
    /// loop that would freeze the desktop if the window ran it itself. Reports its progress and its
    /// process id, and reaches a checkpoint every block so it can be cancelled.
    /// </summary>
    /// <param name="input">The limit.</param>
    /// <param name="context">The task's context: progress, cancellation, checkpoints.</param>
    /// <returns>How many primes, and the largest.</returns>
    public static async Task<PrimeCountResult> CountPrimes(PrimeCountInput input, KazoBackgroundContext context)
    {
        const int Block = 20_000;
        int count = 0;
        int largest = 0;
        for (int start = 2; start < input.Limit; start += Block)
        {
            int end = Math.Min(input.Limit, start + Block);
            for (int candidate = start; candidate < end; candidate++)
            {
                if (IsPrime(candidate))
                {
                    count++;
                    largest = candidate;
                }
            }

            context.Report((double)end / input.Limit, $"pid {context.ProcessId} · {count} primes so far");
            await context.CheckpointAsync();
        }

        return new PrimeCountResult(count, largest);
    }

    private static bool IsPrime(int value)
    {
        if (value < 4)
        {
            return value >= 2;
        }

        if (value % 2 == 0)
        {
            return false;
        }

        for (int divisor = 3; (long)divisor * divisor <= value; divisor += 2)
        {
            if (value % divisor == 0)
            {
                return false;
            }
        }

        return true;
    }
}
