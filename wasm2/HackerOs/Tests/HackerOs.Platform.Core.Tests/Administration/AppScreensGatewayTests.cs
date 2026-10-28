using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Administration;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Tests.Support;

namespace HackerOs.Platform.Core.Tests.Administration;

/// <summary>ADR 0184: <see cref="HackScreens"/> over the trusted screens gateway.</summary>
public sealed class AppScreensGatewayTests
{
    private static readonly Guid Caller = Guid.NewGuid();

    private static readonly AppManifest Manifest = new()
    {
        Id = "com.example.screens",
        Name = "Screens test",
        Version = "1.0.0",
        PublisherId = "com.example",
        Description = "Screens gateway test application.",
        Kind = AppKind.Terminal,
        EntryPoint = new AppEntryPointManifest("HackerOs.Platform.Core.Tests", "ScreensApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("screens", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
    };

    [Fact]
    public async Task Every_call_requires_screens_manage()
    {
        FakeControl control = new();
        AppScreensGateway gateway = new(() => control, new FixedCapabilities(), Caller);

        Assert.True(gateway.IsSupported);
        Assert.Throws<AppGatewayAccessDeniedException>(() => gateway.GetState());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.OpenScreenAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.WatchAsync(() => ValueTask.CompletedTask).AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.ToggleFullScreenAsync().AsTask());
        Assert.Empty(control.Calls);
    }

    [Fact]
    public async Task A_host_without_screens_is_unsupported()
    {
        AppScreensGateway gateway = new(null, new FixedCapabilities(AppCapabilities.ScreensManage), Caller);
        AppScreensGateway stopped = new(() => new FakeControl { IsAvailable = false }, new FixedCapabilities(AppCapabilities.ScreensManage), Caller);

        Assert.False(gateway.IsSupported);
        Assert.False(stopped.IsSupported);
        Assert.Throws<NotSupportedException>(() => gateway.GetState());
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.IdentifyAsync().AsTask());
    }

    [Fact]
    public async Task The_calls_reach_the_screens_with_the_caller_instance()
    {
        FakeControl control = new();
        AppScreensGateway gateway = new(() => control, new FixedCapabilities(AppCapabilities.ScreensManage), Caller);

        Assert.Equal(2, gateway.GetState().CallerScreen);
        Assert.Equal(HackScreenOpenStatus.LimitReached, await gateway.OpenScreenAsync());
        Assert.Equal(2, await gateway.RestoreScreensAsync());
        await gateway.CloseScreenAsync(3);
        await gateway.IdentifyAsync();
        Assert.True(await gateway.ToggleFullScreenAsync());
        Assert.Equal(HackScreenPermission.Granted, await gateway.RequestPermissionAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.CloseScreenAsync(1).AsTask());

        Assert.Equal(["state", "open", "restore", "close 3", "identify", "fullscreen", "permission"], control.Calls);
        Assert.All(control.Instances, instance => Assert.Equal(Caller, instance));
    }

    [Fact]
    public async Task A_watch_follows_the_screens_until_disposed()
    {
        FakeControl control = new();
        AppScreensGateway gateway = new(() => control, new FixedCapabilities(AppCapabilities.ScreensManage), Caller);
        int changes = 0;

        IAsyncDisposable watch = await gateway.WatchAsync(() =>
        {
            changes++;
            return ValueTask.CompletedTask;
        });
        control.Raise();
        await watch.DisposeAsync();
        control.Raise();

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task The_facade_turns_a_denial_into_unauthorized_access_and_checks_the_screen_number()
    {
        FakeControl control = new();
        MinimalAppExecutionContext denied = Context(new AppScreensGateway(() => control, new FixedCapabilities(), Caller));
        MinimalAppExecutionContext granted = Context(new AppScreensGateway(() => control, new FixedCapabilities(AppCapabilities.ScreensManage), Caller));

        Assert.True(HackScreens.IsSupportedFor(denied));
        Assert.Throws<HackUnauthorizedAccessException>(() => HackScreens.GetState(denied));
        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(() => HackScreens.OpenScreenAsync(denied));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HackScreens.CloseScreenAsync(granted, 10));
        await HackScreens.CloseScreenAsync(granted, 9);

        Assert.Equal(["close 9"], control.Calls);
    }

    [Fact]
    public void A_context_without_screens_is_unsupported_through_the_facade()
    {
        MinimalAppExecutionContext context = new(Manifest);

        Assert.False(HackScreens.IsSupportedFor(context));
        Assert.Throws<NotSupportedException>(() => HackScreens.GetState(context));
    }

    private static MinimalAppExecutionContext Context(IAppScreensGateway screens) =>
        new(Manifest, screens: screens);

    private sealed class FakeControl : IScreenControl
    {
        public event Action? Changed;

        public bool IsAvailable { get; init; } = true;

        public List<string> Calls { get; } = [];

        public List<Guid> Instances { get; } = [];

        public HackScreensState GetState(Guid appInstanceId)
        {
            Calls.Add("state");
            Instances.Add(appInstanceId);
            return new([new HackScreenInfo(1, null, 1920, 1080, false)], HackScreenPermission.Granted, false, [], 2);
        }

        public Task<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken)
        {
            Calls.Add("open");
            return Task.FromResult(HackScreenOpenStatus.LimitReached);
        }

        public Task<int> RestoreScreensAsync(CancellationToken cancellationToken)
        {
            Calls.Add("restore");
            return Task.FromResult(2);
        }

        public Task CloseScreenAsync(int number)
        {
            Calls.Add($"close {number}");
            return Task.CompletedTask;
        }

        public Task IdentifyAsync(CancellationToken cancellationToken)
        {
            Calls.Add("identify");
            return Task.CompletedTask;
        }

        public Task<bool> ToggleFullScreenAsync(Guid appInstanceId)
        {
            Calls.Add("fullscreen");
            Instances.Add(appInstanceId);
            return Task.FromResult(true);
        }

        public Task<HackScreenPermission> RequestPermissionAsync()
        {
            Calls.Add("permission");
            return Task.FromResult(HackScreenPermission.Granted);
        }

        public void Raise() => Changed?.Invoke();
    }

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
