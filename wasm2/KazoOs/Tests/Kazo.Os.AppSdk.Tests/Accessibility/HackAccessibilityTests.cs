using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Accessibility;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.AppSdk.Tests.Accessibility;

/// <summary>Tests of <see cref="HackAccessibility"/> (ADR 0105 section 3).</summary>
public sealed class HackAccessibilityTests
{
    [Fact]
    public async Task A_context_without_an_accessibility_service_answers_the_defaults()
    {
        using IDisposable scope = HackExecutionContext.Enter(new Context(gateway: null));

        Assert.Equal(HackAccessibilityPreferences.Default, await HackAccessibility.GetPreferencesAsync());
    }

    [Fact]
    public async Task Preferences_and_their_changes_come_from_the_app_s_gateway()
    {
        FakeGateway gateway = new() { Preferences = new HackAccessibilityPreferences(true, 1.5, true) };
        using IDisposable scope = HackExecutionContext.Enter(new Context(gateway));
        int changes = 0;
        EventHandler handler = (_, _) => changes++;
        HackAccessibility.PreferencesChanged += handler;

        gateway.Raise();
        HackAccessibility.PreferencesChanged -= handler;
        gateway.Raise();

        Assert.Equal(new HackAccessibilityPreferences(true, 1.5, true), await HackAccessibility.GetPreferencesAsync());
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task No_ambient_app_means_no_preferences()
    {
        await Assert.ThrowsAsync<HackExecutionContextMissingException>(() => HackAccessibility.GetPreferencesAsync());
    }

    [Theory]
    [InlineData(1.0, 14, 14)]
    [InlineData(1.25, 14, 18)]
    [InlineData(1.5, 13, 20)]
    [InlineData(2.0, 14, 28)]
    public void Scale_enlarges_a_pixel_size_by_the_text_scale(double textScale, double pixels, int expected)
    {
        Assert.Equal(expected, (HackAccessibilityPreferences.Default with { TextScale = textScale }).Scale(pixels));
        Assert.Equal([1.0, 1.25, 1.5, 2.0], HackAccessibilityPreferences.TextScales);
    }

    private sealed class FakeGateway : IAppAccessibilityGateway
    {
        public HackAccessibilityPreferences Preferences { get; set; } = HackAccessibilityPreferences.Default;

        public event EventHandler? Changed;

        public ValueTask<HackAccessibilityPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Preferences);

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Context(IAppAccessibilityGateway? gateway) : IAppExecutionContext
    {
        public AppManifest Manifest => throw new NotSupportedException();

        public Guid InstanceId { get; } = Guid.NewGuid();

        public string UserId => "u-1";

        public AppAuthority UserAuthority => AppAuthority.User;

        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();

        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());

        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);

        public CancellationToken CancellationToken => CancellationToken.None;

        public ICapabilityChecker Capabilities => throw new NotSupportedException();

        public IAppFileSystemGateway FileSystem => throw new NotSupportedException();

        public IAppEventGateway Events => throw new NotSupportedException();

        public IAppNotificationGateway Notifications => throw new NotSupportedException();

        public IAppLoggingGateway Logging => throw new NotSupportedException();

        public IAppClockGateway Clock => throw new NotSupportedException();

        public IAppProcessGateway Processes => throw new NotSupportedException();

        public IAppAccessibilityGateway Accessibility => gateway ?? DefaultAppAccessibilityGateway.Instance;
    }
}
