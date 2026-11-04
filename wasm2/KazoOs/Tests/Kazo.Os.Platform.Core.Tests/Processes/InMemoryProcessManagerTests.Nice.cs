using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>ADR 0127 §1: the niceness of a process, its inheritance and who may change it.</summary>
public sealed partial class InMemoryProcessManagerTests
{
    [Fact]
    public void A_new_process_starts_at_niceness_zero()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));

        Assert.Equal(ProcessNice.Default, fixture.Manager.GetNice(process.Pid));
    }

    [Fact]
    public void A_child_inherits_the_niceness_of_its_parent_unless_the_request_sets_one()
    {
        Fixture fixture = new();
        ProcessRecord parent = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.SetNice(parent.Pid, 7);

        ProcessRecord inherited = fixture.Manager.Start(fixture.RequestFor("com.hackeros.ls", parent.Pid));
        ProcessRecord explicitNice = fixture.Manager.Start(fixture.RequestFor("com.hackeros.ls", parent.Pid) with { Nice = -3 });

        Assert.Equal(7, fixture.Manager.GetNice(inherited.Pid));
        Assert.Equal(-3, fixture.Manager.GetNice(explicitNice.Pid));
    }

    [Theory]
    [InlineData(-100, -20)]
    [InlineData(-20, -20)]
    [InlineData(19, 19)]
    [InlineData(42, 19)]
    public void Niceness_is_clamped_to_the_linux_range(int requested, int expected)
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal") with { Nice = requested });

        Assert.Equal(expected, fixture.Manager.GetNice(process.Pid));
        Assert.Equal(expected, fixture.Manager.SetNice(process.Pid, requested));
        Assert.Equal(20 + expected, ProcessNice.ToPriority(requested));
    }

    [Fact]
    public void Niceness_of_an_ended_process_is_unknown()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);
        fixture.Manager.Complete(process.Pid, exitCode: 0);

        Assert.Throws<KeyNotFoundException>(() => fixture.Manager.GetNice(process.Pid));
        Assert.Throws<KeyNotFoundException>(() => fixture.Manager.SetNice(process.Pid, 5));
    }

    [Fact]
    public void A_user_may_raise_the_niceness_of_their_own_process_but_not_lower_it()
    {
        Fixture fixture = new();
        ProcessRecord shell = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        ProcessRecord job = fixture.Manager.Start(fixture.RequestFor("com.hackeros.ls", shell.Pid));
        AppProcessGateway gateway = new(fixture.Manager, new FixedCapabilities(AppCapabilities.ProcessList, AppCapabilities.ProcessManage), shell.Pid);

        Assert.Equal(10, gateway.SetNice(job.Pid, 10));
        UnauthorizedAccessException denied = Assert.Throws<UnauthorizedAccessException>(() => gateway.SetNice(job.Pid, 5));

        Assert.Equal("Permission denied", denied.Message);
        Assert.Equal(10, gateway.GetNice(job.Pid));
    }

    [Fact]
    public void Root_may_lower_a_niceness_and_renice_another_users_process()
    {
        Fixture fixture = new();
        ProcessRecord shell = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        ProcessRecord other = fixture.Manager.Start(fixture.RequestFor("com.hackeros.ls") with { UserId = LocalUserId.FromGuid(Guid.NewGuid()) });
        FixedCapabilities capabilities = new(AppCapabilities.ProcessList, AppCapabilities.ProcessManage);
        AppProcessGateway user = new(fixture.Manager, capabilities, shell.Pid);
        AppProcessGateway root = new(fixture.Manager, capabilities, shell.Pid) { IsRootElevated = true };

        UnauthorizedAccessException denied = Assert.Throws<UnauthorizedAccessException>(() => user.SetNice(other.Pid, 5));
        Assert.Equal("Operation not permitted", denied.Message);
        Assert.Equal(-5, root.SetNice(other.Pid, -5));
    }

    [Fact]
    public void Reading_or_changing_another_process_needs_the_process_capabilities()
    {
        Fixture fixture = new();
        ProcessRecord shell = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor("com.hackeros.ls", shell.Pid));
        ProcessRecord other = fixture.Manager.Start(fixture.RequestFor("com.hackeros.ls"));
        AppProcessGateway gateway = new(fixture.Manager, new FixedCapabilities(), shell.Pid);

        Assert.Equal(0, gateway.GetNice(shell.Pid));
        Assert.Equal(3, gateway.SetNice(shell.Pid, 3));
        Assert.Throws<AppGatewayAccessDeniedException>(() => gateway.GetNice(other.Pid));
        Assert.Throws<AppGatewayAccessDeniedException>(() => gateway.SetNice(other.Pid, 3));

        // ADR 0140: like kill, a parent renices its own children without process.manage.
        Assert.Equal(7, gateway.SetNice(child.Pid, 7));
    }

    /// <summary>Grants exactly the listed capabilities (the priority calls only use <see cref="Require"/>).</summary>
    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }
}
