namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>The runtime view of HackerOsCpuAccounting / HackerOsMemoryAccounting (ADR 0066, section 2).</summary>
public sealed class ResourceAccountingFeaturesTests
{
    [Fact]
    public void A_missing_switch_means_the_code_is_present()
    {
        // This test assembly references the project without the host targets: no switch is set
        // (or the theory below reset it to true).
        Assert.True(ResourceAccountingFeatures.IsCpuAccountingSupported);
        Assert.True(ResourceAccountingFeatures.IsMemoryAccountingSupported);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Each_switch_is_independent(bool cpu, bool memory)
    {
        try
        {
            AppContext.SetSwitch(ResourceAccountingFeatures.CpuSwitchName, cpu);
            AppContext.SetSwitch(ResourceAccountingFeatures.MemorySwitchName, memory);

            Assert.Equal(cpu, ResourceAccountingFeatures.IsCpuAccountingSupported);
            Assert.Equal(memory, ResourceAccountingFeatures.IsMemoryAccountingSupported);
        }
        finally
        {
            AppContext.SetSwitch(ResourceAccountingFeatures.CpuSwitchName, true);
            AppContext.SetSwitch(ResourceAccountingFeatures.MemorySwitchName, true);
        }
    }
}
