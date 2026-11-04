using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.Tests;

/// <summary>ADR 0125: <see cref="KazoResourceLimits.Parse"/> reads <c>/proc/&lt;pid&gt;/limits</c>.</summary>
public sealed class KazoResourceLimitsTests
{
    [Fact]
    public void Parse_reads_the_table_and_the_enforcement_comments()
    {
        const string text =
            "Limit                     Soft Limit           Hard Limit           Units     \n" +
            "Max cpu time              60                   unlimited            seconds   \n" +
            "Max resident set          unlimited            unlimited            bytes     \n" +
            "Max processes             4096                 4096                 processes \n" +
            "Max open files            1024                 524288               files     \n" +
            "# enforcement: strict (worker)\n" +
            "# rss: inactive (not measured)\n";

        KazoResourceLimits limits = KazoResourceLimits.Parse(text);

        Assert.Equal(new ResourceLimit(60, ResourceLimit.Unlimited), limits.Cpu);
        Assert.Equal(ResourceLimit.Infinite, limits.Rss);
        Assert.Equal(new ResourceLimit(4096, 4096), limits[ResourceLimitKind.NProc]);
        Assert.Equal(new ResourceLimit(1024, 524288), limits.NoFile);
        Assert.Equal(KazoResourceLimitEnforcement.Strict, limits.Enforcement);
        Assert.True(limits.IsCpuMeasured);
        Assert.False(limits.IsMemoryMeasured);
    }

    [Fact]
    public void Parse_reads_limits_turned_off_by_the_host()
    {
        KazoResourceLimits limits = KazoResourceLimits.Parse(
            "Max open files            unlimited            unlimited            files     \n# enforcement: off (HackerOs:ResourceLimits:Enabled=false)\n");

        Assert.Equal(KazoResourceLimitEnforcement.Off, limits.Enforcement);
        Assert.Equal(ResourceLimit.Infinite, limits.NoFile);
    }
}
