using Kazo.Os.Platform.Core.Time;

namespace Kazo.Os.Platform.Core.Tests.Time;

public sealed class SeededSimulationRandomTests
{
    [Fact]
    public void The_same_seed_and_domain_key_always_produce_the_same_sequence()
    {
        SeededSimulationRandom first = new(42);
        SeededSimulationRandom second = new(42);

        int[] firstValues = [.. Enumerable.Range(0, 5).Select(_ => first.GetStream("process:1:resources").Next(0, 1000))];
        int[] secondValues = [.. Enumerable.Range(0, 5).Select(_ => second.GetStream("process:1:resources").Next(0, 1000))];

        Assert.Equal(firstValues, secondValues);
    }

    [Fact]
    public void Different_domain_keys_from_the_same_seed_produce_independent_sequences()
    {
        SeededSimulationRandom random = new(42);

        int[] processValues = [.. Enumerable.Range(0, 5).Select(_ => random.GetStream("process:1:resources").Next(0, 1000))];
        int[] networkValues = [.. Enumerable.Range(0, 5).Select(_ => random.GetStream("process:1:network").Next(0, 1000))];

        Assert.NotEqual(processValues, networkValues);
    }

    [Fact]
    public void Adding_a_new_domain_key_does_not_change_an_existing_domain_keys_sequence()
    {
        SeededSimulationRandom before = new(42);
        int[] beforeValues = [.. Enumerable.Range(0, 5).Select(_ => before.GetStream("process:1:resources").Next(0, 1000))];

        SeededSimulationRandom after = new(42);
        after.GetStream("process:99:resources").Next(0, 1000);
        int[] afterValues = [.. Enumerable.Range(0, 5).Select(_ => after.GetStream("process:1:resources").Next(0, 1000))];

        Assert.Equal(beforeValues, afterValues);
    }
}
