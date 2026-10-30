using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.ServiceProcess;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Lifecycle;

/// <summary>
/// ADR 0097 on a real kernel: every transition of a service's state (activating, active,
/// deactivating, inactive, failed with its result), its health, <c>reset-failed</c>, and when
/// services start and stop (boot, login, logout).
/// </summary>
public sealed partial class AppLifecycleOrchestratorTests
{
    private const string StateServiceId = "org.hackeros.statesvc";

    [Fact]
    public async Task A_service_is_activating_until_its_start_returns_then_active_and_healthy()
    {
        StateProbe.Reset();
        Fixture fixture = new(StateManifest(typeof(GatedStartServiceApp)));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppLaunchResult launch = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(StateServiceId, principal, []));
        await StateProbe.Starting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ServiceStatusSnapshot starting = fixture.Orchestrator.GetServiceStatus(StateServiceId);
        StateProbe.ReleaseStart.SetResult();
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus(StateServiceId).ActiveState == ServiceActiveState.Active);
        ServiceStatusSnapshot active = fixture.Orchestrator.GetServiceStatus(StateServiceId);

        Assert.Equal(ServiceActiveState.Activating, starting.ActiveState);
        Assert.Equal(launch.Process!.Pid.Value, starting.MainPid);
        Assert.Equal(ServiceHealth.Healthy, active.Health);
        Assert.Equal(ServiceRunResult.Success, active.Result);
    }

    [Fact]
    public async Task A_service_reports_its_health_and_status_text_to_the_os_and_proc()
    {
        StateProbe.Reset();
        Fixture fixture = new(StateManifest(typeof(DegradedServiceApp)));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppLaunchResult launch = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(StateServiceId, principal, []));
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus(StateServiceId).Health == ServiceHealth.Degraded);
        ServiceStatusSnapshot status = fixture.Orchestrator.GetServiceStatus(StateServiceId);

        Assert.Equal(ServiceActiveState.Active, status.ActiveState);
        Assert.Equal("cache cold", status.StatusText);
        Assert.True(fixture.Manager.TryGetActive(launch.Process!.Pid, out ProcessRecord record));
        Assert.Equal(ServiceHealth.Degraded, record.ServiceHealth);
    }

    [Fact]
    public async Task A_service_that_throws_is_failed_with_result_exception_until_reset()
    {
        StateProbe.Reset();
        Fixture fixture = new(StateManifest(typeof(ThrowingServiceApp)));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(StateServiceId, principal, []));
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus(StateServiceId).ActiveState == ServiceActiveState.Failed);
        ServiceStatusSnapshot failed = fixture.Orchestrator.GetServiceStatus(StateServiceId);

        Assert.Equal(ServiceRunResult.Exception, failed.Result);
        Assert.Null(failed.MainPid);
        Assert.True(fixture.Orchestrator.ResetFailedService(StateServiceId));
        Assert.Equal(ServiceActiveState.Inactive, fixture.Orchestrator.GetServiceStatus(StateServiceId).ActiveState);
        Assert.Equal(ServiceRunResult.Success, fixture.Orchestrator.GetServiceStatus(StateServiceId).Result);
        Assert.False(fixture.Orchestrator.ResetFailedService(StateServiceId));
    }

    [Fact]
    public async Task A_killed_service_is_failed_with_result_killed_and_a_new_start_clears_it()
    {
        Fixture fixture = new(WaiterManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppLaunchResult first = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.waiter", principal, []));
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter").ActiveState == ServiceActiveState.Active);
        fixture.Manager.Kill(first.Process!.Pid);
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter").ActiveState == ServiceActiveState.Failed);
        ServiceStatusSnapshot killed = fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter");

        await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.waiter", principal, []));
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter").ActiveState == ServiceActiveState.Active);

        Assert.Equal(ServiceRunResult.Killed, killed.Result);
        Assert.Equal(ServiceRunResult.Success, fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter").Result);
    }

    [Fact]
    public async Task A_service_stopped_on_request_is_inactive_not_failed()
    {
        Fixture fixture = new(WaiterManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.waiter", principal, []));
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter").ActiveState == ServiceActiveState.Active);
        await fixture.Orchestrator.StopServiceAsync("org.hackeros.waiter");
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter").ActiveState != ServiceActiveState.Deactivating);

        ServiceStatusSnapshot stopped = fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter");
        Assert.Equal(ServiceActiveState.Inactive, stopped.ActiveState);
        Assert.Equal(ServiceRunResult.Success, stopped.Result);
    }

    [Fact]
    public async Task A_service_that_returns_by_itself_is_inactive_with_success()
    {
        StateProbe.Reset();
        Fixture fixture = new(StateManifest(typeof(OneShotServiceApp)));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(StateServiceId, principal, []));
        await WaitUntilAsync(() => !fixture.Manager.TryGetSingleton(StateServiceId, out _));
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus(StateServiceId).ActiveState == ServiceActiveState.Inactive);

        Assert.Equal(ServiceRunResult.Success, fixture.Orchestrator.GetServiceStatus(StateServiceId).Result);
    }

    [Fact]
    public async Task A_start_that_outlives_the_start_timeout_is_killed_and_failed_with_result_timeout()
    {
        StateProbe.Reset();
        Fixture fixture = new(StateManifest(typeof(GatedStartServiceApp)));
        fixture.Orchestrator.ServiceStartTimeout = TimeSpan.FromMilliseconds(200);
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppLaunchResult launch = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(StateServiceId, principal, []));
        await WaitUntilAsync(() => fixture.Orchestrator.GetServiceStatus(StateServiceId).ActiveState == ServiceActiveState.Failed);

        Assert.Equal(ServiceRunResult.Timeout, fixture.Orchestrator.GetServiceStatus(StateServiceId).Result);
        Assert.False(fixture.Manager.TryGetActive(launch.Process!.Pid, out _));
        StateProbe.ReleaseStart.TrySetResult();
    }

    [Fact]
    public async Task Stopping_a_session_stops_its_services()
    {
        Fixture fixture = new(WaiterManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppLaunchResult launch = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.waiter", principal, []));

        int stopped = await fixture.Orchestrator.StopSessionServicesAsync(principal.SessionId);

        Assert.Equal(1, stopped);
        Assert.False(fixture.Manager.TryGetActive(launch.Process!.Pid, out _));
        Assert.Equal(ProcessExitReason.Logout, fixture.Manager.GetHistory().Last(r => r.Pid == launch.Process.Pid).ExitReason);
    }

    [Fact]
    public async Task Signing_out_stops_the_window_apps_of_the_session_too()
    {
        // Services are covered above; a window app is stopped as well, so no window outlives its session.
        Fixture fixture = new(SingletonWindowManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppLaunchResult window = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.notes", principal, []));

        Assert.Equal(0, await fixture.Orchestrator.StopSessionServicesAsync(principal.SessionId));
        int stopped = await fixture.Orchestrator.StopSessionAppsAsync(principal.SessionId);

        Assert.Equal(1, stopped);
        Assert.False(fixture.Manager.TryGetActive(window.Process!.Pid, out _));
        Assert.Equal(ProcessExitReason.Logout, fixture.Manager.GetHistory().Last(r => r.Pid == window.Process.Pid).ExitReason);
    }

    [Fact]
    public async Task A_system_service_starts_at_boot_as_the_service_account_and_survives_the_logout()
    {
        AppManifest boot = WaiterManifest() with { ServiceStartup = AppServiceStartup.Boot, AutoStart = true };
        Fixture fixture = new(["org.hackeros.waiter"], boot);
        AuthenticatedPrincipal account = ServiceAccount.CreatePrincipal(
            InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), fixture.Clock.UtcNow);

        IReadOnlyList<AppLaunchResult> started = await fixture.Orchestrator.StartBootServicesAsync(account);
        AuthenticatedPrincipal alice = await fixture.LoginAsync();
        IReadOnlyList<AppLaunchResult> atLogin = await fixture.Orchestrator.StartAllServicesAsync(alice);
        await fixture.Session.LogoutAsync();
        await fixture.Orchestrator.StopSessionServicesAsync(alice.SessionId);
        await Task.Delay(50);

        AppLaunchResult result = Assert.Single(started);
        Assert.Equal(AppLaunchStatus.Launched, result.Status);
        Assert.Equal(ServiceAccount.UserId, result.Process!.UserId);
        Assert.Empty(atLogin);
        Assert.True(fixture.Manager.TryGetActive(result.Process.Pid, out _));
        Assert.Equal(ServiceActiveState.Active, fixture.Orchestrator.GetServiceStatus("org.hackeros.waiter").ActiveState);

        fixture.Manager.CancelSystemScope();
        await WaitUntilAsync(() => !fixture.Manager.TryGetActive(result.Process.Pid, out _));
    }

    [Fact]
    public async Task A_boot_service_that_is_not_a_system_app_starts_at_login_instead()
    {
        AppManifest boot = WaiterManifest() with { ServiceStartup = AppServiceStartup.Boot, AutoStart = true };
        Fixture fixture = new(boot);
        AuthenticatedPrincipal account = ServiceAccount.CreatePrincipal(
            InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), fixture.Clock.UtcNow);

        IReadOnlyList<AppLaunchResult> atBoot = await fixture.Orchestrator.StartBootServicesAsync(account);
        AuthenticatedPrincipal alice = await fixture.LoginAsync();
        IReadOnlyList<AppLaunchResult> atLogin = await fixture.Orchestrator.StartAllServicesAsync(alice);

        Assert.Empty(atBoot);
        Assert.Equal(AppServiceStartup.Login, fixture.Orchestrator.GetEffectiveStartup(boot));
        Assert.Equal(alice.UserId, Assert.Single(atLogin).Process!.UserId);
    }

    [Fact]
    public async Task The_startup_coordinator_starts_login_services_after_the_login_and_stops_them_at_the_logout()
    {
        Fixture fixture = new(WaiterManifest() with { AutoStart = true });
        using ServiceStartupCoordinator startup = new(fixture.Orchestrator, fixture.EventBus, fixture.Session, fixture.Manager);

        AuthenticatedPrincipal alice = await fixture.LoginAsync();
        await startup.LastEventWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Manager.TryGetSingleton("org.hackeros.waiter", out ProcessRecord running));
        Assert.Equal(alice.UserId, running.UserId);

        await fixture.Session.LogoutAsync();
        await startup.LastEventWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Manager.TryGetActive(running.Pid, out _));
    }

    private static AppManifest StateManifest(Type serviceType) => WaiterManifest() with
    {
        Id = StateServiceId,
        EntryPoint = new AppEntryPointManifest("HackerOs.Platform.Core.Tests", serviceType.FullName!)
    };

    private static class StateProbe
    {
        public static TaskCompletionSource Starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static TaskCompletionSource ReleaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
        {
            Starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Its start waits for the test: the unit stays <c>activating</c> meanwhile.</summary>
    private sealed class GatedStartServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        protected override async Task OnStartAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
        {
            StateProbe.Starting.TrySetResult();
            await StateProbe.ReleaseStart.Task.WaitAsync(sessionCancellationToken);
        }

        protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellationToken);
    }

    private sealed class DegradedServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
        {
            ReportHealth(HackServiceHealth.Degraded, "cache cold");
            return Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellationToken);
        }
    }

    private sealed class ThrowingServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("Simulated service crash.");
        }
    }

    private sealed class OneShotServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken) =>
            Task.CompletedTask;
    }
}
