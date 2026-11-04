using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>ADR 0125, section 3: the rules of <c>limits.conf(5)</c> as <c>pam_limits</c> applies them.</summary>
public sealed class ResourceLimitsConfigurationTests
{
    [Fact]
    public void The_shipped_file_is_entirely_commented()
    {
        List<string> warnings = [];
        Assert.Empty(ResourceLimitsConfiguration.Parse(ResourceLimitsConfiguration.DefaultLimitsFileContent, "limits.conf", warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Values_are_converted_to_the_kernel_units()
    {
        IReadOnlyList<ResourceLimitRule> rules = ResourceLimitsConfiguration.Parse(
            "*  hard cpu 2\n*  -    rss 1024\n*  soft nofile unlimited\n*  soft nproc -1\n", "limits.conf");

        Assert.Equal(
        [
            new ResourceLimitRule("*", false, true, ResourceLimitKind.Cpu, 120),
            new ResourceLimitRule("*", true, true, ResourceLimitKind.Rss, 1024 * 1024),
            new ResourceLimitRule("*", true, false, ResourceLimitKind.NoFile, ResourceLimit.Unlimited),
            new ResourceLimitRule("*", true, false, ResourceLimitKind.NProc, ResourceLimit.Unlimited)
        ], rules);
    }

    [Fact]
    public void Invalid_lines_are_skipped_with_a_warning_and_other_linux_items_ignored()
    {
        List<string> warnings = [];
        IReadOnlyList<ResourceLimitRule> rules = ResourceLimitsConfiguration.Parse(
            "* soft core 0\n* maybe nofile 10\n* soft nofile ten\n* soft colour 1\nbroken line\n", "/etc/security/limits.conf", warnings);

        Assert.Empty(rules);
        Assert.Equal(
        [
            "pam_limits: /etc/security/limits.conf:2: unknown limit type 'maybe'",
            "pam_limits: /etc/security/limits.conf:3: wrong limit value 'ten' for limit type 'soft'",
            "pam_limits: /etc/security/limits.conf:4: unknown limit item 'colour'",
            "pam_limits: /etc/security/limits.conf:5: invalid line 'broken line' - skipped"
        ], warnings);
    }

    [Fact]
    public void A_user_line_beats_a_group_line_which_beats_a_wildcard_whatever_the_order()
    {
        IReadOnlyList<ResourceLimitRule> rules = ResourceLimitsConfiguration.Parse(
            "alice soft nofile 100\n@dev soft nofile 200\n* soft nofile 300\n@dev soft nproc 50\n* soft nproc 60\n* soft nproc 70\n", "limits.conf");

        ProcessResourceLimits alice = ResourceLimitsConfiguration.Apply(rules, "alice", ["dev"], ProcessResourceLimits.KernelDefaults);
        ProcessResourceLimits bob = ResourceLimitsConfiguration.Apply(rules, "bob", [], ProcessResourceLimits.KernelDefaults);

        Assert.Equal(100, alice.NoFile.Soft);
        Assert.Equal(50, alice.NProc.Soft);
        Assert.Equal(300, bob.NoFile.Soft);
        // Same kind: the later line wins.
        Assert.Equal(70, bob.NProc.Soft);
    }

    [Fact]
    public void A_soft_value_above_the_hard_one_is_lowered_with_a_warning()
    {
        List<string> warnings = [];
        ProcessResourceLimits limits = ResourceLimitsConfiguration.Apply(
            ResourceLimitsConfiguration.Parse("* hard nofile 64\n* soft nofile 128\n", "limits.conf"),
            "alice", [], ProcessResourceLimits.KernelDefaults, warnings);

        Assert.Equal(new ResourceLimit(64, 64), limits.NoFile);
        Assert.Equal(["pam_limits: alice: soft nofile 128 above hard 64; lowered"], warnings);
    }
}
