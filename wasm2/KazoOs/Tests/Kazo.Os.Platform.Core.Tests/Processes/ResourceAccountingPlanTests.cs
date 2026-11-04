using Kazo.Os.Platform.Core.Processes.Accounting;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>ADR 0066: a measurement runs only when built in and enabled; the build always wins.</summary>
public sealed class ResourceAccountingPlanTests
{
    public static TheoryData<bool, bool, bool, bool> AllCombinations()
    {
        TheoryData<bool, bool, bool, bool> data = [];
        foreach (bool buildCpu in new[] { false, true })
        foreach (bool buildMemory in new[] { false, true })
        foreach (bool enabledCpu in new[] { false, true })
        foreach (bool enabledMemory in new[] { false, true })
        {
            data.Add(buildCpu, buildMemory, enabledCpu, enabledMemory);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Each_measurement_needs_both_the_build_and_the_setting(bool buildCpu, bool buildMemory, bool enabledCpu, bool enabledMemory)
    {
        ResourceAccountingPlan plan = ResourceAccountingPlan.Resolve(
            new ResourceAccountingBuildSupport(buildCpu, buildMemory),
            ResourceAccountingSettings.Default with { CpuEnabled = enabledCpu, MemoryEnabled = enabledMemory });

        Assert.Equal(buildCpu && enabledCpu, plan.CpuActive);
        Assert.Equal(buildMemory && enabledMemory, plan.MemoryActive);
        Assert.Equal(plan.CpuActive || plan.MemoryActive, plan.RequiresContextSwitchHook);
        Assert.Equal(!buildCpu && enabledCpu, plan.Notices.Any(static line => line.Contains("cpu measurement is not built", StringComparison.Ordinal)));
        Assert.Equal(!buildMemory && enabledMemory, plan.Notices.Any(static line => line.Contains("memory measurement is not built", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_ignored_setting_is_explained_for_syslog()
    {
        ResourceAccountingPlan plan = ResourceAccountingPlan.Resolve(
            new ResourceAccountingBuildSupport(Cpu: false, Memory: true),
            ResourceAccountingSettings.Default);

        Assert.Equal(
            [
                "resource accounting: cpu measurement is not built into this host (HackerOsCpuAccounting=false); " +
                "HackerOs:ResourceAccounting:Cpu:Enabled=true ignored, cpu stays simulated",
                "resource accounting: cpu off (not built), memory on"
            ],
            plan.Notices);
    }

    [Fact]
    public void The_summary_says_why_a_measurement_is_off()
    {
        ResourceAccountingPlan plan = ResourceAccountingPlan.Resolve(
            new ResourceAccountingBuildSupport(Cpu: true, Memory: true),
            ResourceAccountingSettings.Default with { MemoryEnabled = false });

        Assert.Equal(["resource accounting: cpu on, memory off (appsettings)"], plan.Notices);
    }

    [Fact]
    public void The_disabled_plan_installs_nothing()
    {
        Assert.False(ResourceAccountingPlan.Disabled.CpuActive);
        Assert.False(ResourceAccountingPlan.Disabled.MemoryActive);
        Assert.False(ResourceAccountingPlan.Disabled.RequiresContextSwitchHook);
    }

    [Fact]
    public void Null_accountants_never_measure()
    {
        Assert.False(NullCpuTimeAccountant.Instance.IsMeasuring);
        Assert.False(NullCpuTimeAccountant.Instance.TryGetCpuUsage(Simulation.Abstractions.Processes.ProcessId.FromInt64(1), out _));
        Assert.False(NullMemoryAccountant.Instance.IsMeasuring);
        Assert.False(NullMemoryAccountant.Instance.TryGetSystemMemory([], out _));
        Assert.False(NullKernelMemoryLedger.Instance.IsTracking);
        Assert.Same(
            Simulation.Abstractions.Processes.KernelMemoryCharge.None,
            NullKernelMemoryLedger.Instance.Charge(Simulation.Abstractions.Processes.ProcessId.FromInt64(1), Simulation.Abstractions.Processes.KernelMemoryKind.Pipe, 10));
    }
}
