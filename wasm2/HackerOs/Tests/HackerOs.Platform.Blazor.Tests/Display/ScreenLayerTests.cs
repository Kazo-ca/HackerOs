using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Blazor.Display;
using HackerOs.Platform.Blazor.Security;
using HackerOs.Platform.Blazor.Tests.Localization;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Policy;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Tests.Display;

/// <summary>
/// <see cref="ScreenLayer"/> (ADR 0178 §1, ADR 0179 §2): each secondary screen shows its desktop,
/// a slot the overlays move to, and a veil that sends the user to screen 1 while a secure dialog
/// waits there. The moves themselves are covered by the UI E2E tests.
/// </summary>
public sealed class ScreenLayerTests
{
    [Fact]
    public async Task A_secondary_screen_shows_its_desktop_and_an_overlay_slot()
    {
        await using Harness harness = await Harness.WithOneScreenAsync();

        string html = await harness.RenderAsync();

        Assert.Matches("class=\"secondary-screen\" data-display=\"2\"", html);
        Assert.Contains("data-desktop-for=\"2\"", html, StringComparison.Ordinal);
        Assert.Contains("data-screen-overlay-slot", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secondary-screen-number", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secondary-screen-secure-veil", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_approval_veils_the_secondary_screens()
    {
        await using Harness harness = await Harness.WithOneScreenAsync();
        using IDisposable host = harness.Approvals.Attach();
        _ = harness.Approvals.RequestAsync(ApprovalRequest());

        string html = System.Net.WebUtility.HtmlDecode(await harness.RenderAsync());

        Assert.Contains("secondary-screen-secure-veil", html, StringComparison.Ordinal);
        Assert.Contains("A security request is waiting on screen 1.", html, StringComparison.Ordinal);
        Assert.Contains("Go to screen 1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_administrator_authentication_veils_the_secondary_screens()
    {
        await using Harness harness = await Harness.WithOneScreenAsync();
        using IDisposable host = harness.Authentications.Attach();
        _ = harness.Authentications.RequestAsync(AuthenticationRequest());

        string html = await harness.RenderAsync();

        Assert.Contains("secondary-screen-secure-veil", html, StringComparison.Ordinal);
    }

    private static AppManifest Manifest() => new()
    {
        Id = "org.example.scanner",
        Name = "Scanner",
        Version = "1.2.0",
        PublisherId = "org.example",
        Description = "Veil fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Scanner", "Scanner.ScannerWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.NetworkRealAccess],
    };

    private static DangerousCapabilityApprovalRequest ApprovalRequest()
    {
        AppManifest manifest = Manifest();
        LocalGroupId group = LocalGroupId.FromGuid(Guid.NewGuid());
        AuthenticatedPrincipal principal = new(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse("root"),
            "Root",
            AppAuthority.Administrator,
            group,
            [group],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UnixEpoch);
        return new DangerousCapabilityApprovalRequest(
            manifest,
            [.. manifest.Capabilities.Select(AppCapabilities.Describe)],
            principal,
            "root",
            (_, _, _) => Task.FromResult(new DangerousCapabilityApprovalAttempt(false, 2, "invalid-credentials")));
    }

    private static AdministratorAuthenticationPromptRequest AuthenticationRequest() =>
        new(
            Manifest(),
            "sudo",
            null,
            "root",
            (_, _, _) => Task.FromException<AdministratorAuthenticationAttempt>(new InvalidOperationException("Not answered in this test.")));

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Harness(ServiceProvider provider)
        {
            _provider = provider;
        }

        public DangerousCapabilityApprovalBroker Approvals => _provider.GetRequiredService<DangerousCapabilityApprovalBroker>();

        public AdministratorAuthenticationBroker Authentications => _provider.GetRequiredService<AdministratorAuthenticationBroker>();

        public static async Task<Harness> WithOneScreenAsync()
        {
            ServiceCollection services = new();
            services.AddEnglishLocalizer();
            services.AddSingleton<IJSRuntime, ScriptedJsRuntime>();
            services.AddSingleton(new WindowRuntime(new WindowBounds(0, 0, 1280, 720)));
            services.AddSingleton(provider => new DisplaySurfaceService(provider.GetRequiredService<IJSRuntime>()));
            services.AddSingleton(provider => new ScreenService(
                provider.GetRequiredService<DisplaySurfaceService>(),
                provider.GetRequiredService<IJSRuntime>(),
                provider.GetRequiredService<WindowRuntime>()));
            services.AddSingleton<DangerousCapabilityApprovalBroker>();
            services.AddSingleton<AdministratorAuthenticationBroker>();
            Harness harness = new(services.BuildServiceProvider());
            ScreenOpenResult opened = await harness._provider.GetRequiredService<ScreenService>().OpenScreenAsync();
            Assert.Equal(ScreenOpenStatus.Opened, opened.Status);
            return harness;
        }

        public async Task<string> RenderAsync()
        {
            await using HtmlRenderer renderer = new(_provider, NullLoggerFactory.Instance);
            RenderFragment<SecondaryScreen> desktop = screen => builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "data-desktop-for", screen.Id.Number);
                builder.CloseElement();
            };
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<ScreenLayer>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(ScreenLayer.ScreenContent)] = desktop }));
                return root.ToHtmlString();
            });
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    /// <summary>Opens every surface at 1024 × 768; every other call answers nothing.</summary>
    private sealed class ScriptedJsRuntime : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                return ValueTask.FromResult((TValue)(object)this);
            }

            string json = identifier == "open" ? """{"status":"opened","width":1024,"height":768}""" : "null";
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
