using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.Platform.Blazor.Security;
using Kazo.Os.Platform.Blazor.Tests.Localization;
using Kazo.Os.Platform.Core.Globalization;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests.Security;

/// <summary>Render contract of the secure-desktop approval overlay (ADR 0049 sections 6 and 9).</summary>
public sealed class DangerousCapabilityApprovalOverlayTests
{
    [Fact]
    public async Task Overlay_shows_the_app_its_capabilities_with_their_risks_and_disables_approve_without_password()
    {
        DangerousCapabilityApprovalBroker broker = new();
        using IDisposable host = broker.Attach();
        Task<DangerousCapabilityApprovalOutcome> outcome = broker.RequestAsync(CreateRequest(suggestedLogin: "root"));

        string html = await RenderAsync(broker);
        string text = System.Net.WebUtility.HtmlDecode(html);

        Assert.Contains("Administrator authorization required", html, StringComparison.Ordinal);
        Assert.Contains("Scanner", html, StringComparison.Ordinal);
        Assert.Contains("org.example.scanner", html, StringComparison.Ordinal);
        Assert.Contains("Non-system app", text, StringComparison.Ordinal);
        Assert.Contains("1.2.0", html, StringComparison.Ordinal);
        foreach (string capability in new[] { AppCapabilities.NetworkRealAccess, AppCapabilities.ProcessManage })
        {
            // The texts are translated (ADR 0104): English before any session language.
            CapabilityDescription description = CapabilityText.Describe(capability, language: null);
            Assert.Contains(capability, html, StringComparison.Ordinal);
            Assert.Contains(description.Title, text, StringComparison.Ordinal);
            Assert.Contains(description.RiskDescription!, text, StringComparison.Ordinal);
        }

        Assert.Contains("value=\"root\"", html, StringComparison.Ordinal);
        Assert.Matches("<button type=\"submit\" class=\"dca-approve\" disabled", html);
        Assert.Contains("/var/log/auth.log", html, StringComparison.Ordinal);
        Assert.False(outcome.IsCompleted);
    }

    [Fact]
    public async Task Overlay_speaks_the_session_language_French_after_sign_in()
    {
        DangerousCapabilityApprovalBroker broker = new();
        using IDisposable host = broker.Attach();
        _ = broker.RequestAsync(CreateRequest(suggestedLogin: "root"));

        string text = System.Net.WebUtility.HtmlDecode(
            await RenderAsync(broker, TestLocalization.CreateSessionLocale("fr_FR.UTF-8")));

        Assert.Contains("Autorisation administrateur requise", text, StringComparison.Ordinal);
        Assert.Contains("App non système", text, StringComparison.Ordinal);
        Assert.Contains("Nom d'administrateur", text, StringComparison.Ordinal);
        Assert.Contains("Mot de passe", text, StringComparison.Ordinal);
        Assert.Contains(CapabilityText.Describe(AppCapabilities.NetworkRealAccess, new CultureInfo("fr")).Title, text, StringComparison.Ordinal);
        Assert.Contains(">Refuser</button>", text, StringComparison.Ordinal);
        Assert.Contains("Autoriser", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrator authorization required", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlay_renders_nothing_without_a_pending_request()
    {
        DangerousCapabilityApprovalBroker broker = new();

        string html = await RenderAsync(broker);

        Assert.DoesNotContain("dca-veil", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Broker_without_an_overlay_reports_the_prompt_unavailable()
    {
        DangerousCapabilityApprovalBroker broker = new();

        DangerousCapabilityApprovalOutcome outcome = await broker.RequestAsync(CreateRequest(suggestedLogin: null));

        Assert.Equal(DangerousCapabilityApprovalOutcome.Unavailable, outcome);
    }

    [Fact]
    public async Task Removing_the_overlay_dismisses_the_pending_request()
    {
        DangerousCapabilityApprovalBroker broker = new();
        IDisposable host = broker.Attach();
        Task<DangerousCapabilityApprovalOutcome> outcome = broker.RequestAsync(CreateRequest(suggestedLogin: null));

        host.Dispose();

        Assert.Equal(DangerousCapabilityApprovalOutcome.Dismissed, await outcome);
        Assert.Null(broker.Current);
    }

    private static async Task<string> RenderAsync(DangerousCapabilityApprovalBroker broker, SessionLocaleService? locale = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(broker);
        if (locale is null)
        {
            services.AddEnglishLocalizer();
        }
        else
        {
            services.AddSessionLocalizer(locale);
        }

        services.AddSingleton<IIconCatalog, IconCatalog>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<DangerousCapabilityApprovalOverlay>(ParameterView.Empty);
            return root.ToHtmlString();
        });
    }

    [Fact]
    public async Task Overlay_lists_the_real_network_ports_the_app_declares_under_network_real_access()
    {
        DangerousCapabilityApprovalBroker broker = new();
        using IDisposable host = broker.Attach();
        _ = broker.RequestAsync(CreateRequest(suggestedLogin: "root", realNetworkPorts: ["tcp/6697", "tcp/6667"]));

        string english = System.Net.WebUtility.HtmlDecode(await RenderAsync(broker));
        string french = System.Net.WebUtility.HtmlDecode(
            await RenderAsync(broker, TestLocalization.CreateSessionLocale("fr_FR.UTF-8")));

        // ADR 0235 section 3: ordered TCP first then by port, once, under the capability.
        Assert.Contains("Real servers on ports: TCP 6667, TCP 6697", english, StringComparison.Ordinal);
        Assert.Contains("Vrais serveurs sur les ports : TCP 6667, TCP 6697", french, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(english, "dca-capability-ports"));
    }

    [Fact]
    public async Task Overlay_shows_no_port_line_when_the_app_declares_none()
    {
        DangerousCapabilityApprovalBroker broker = new();
        using IDisposable host = broker.Attach();
        _ = broker.RequestAsync(CreateRequest(suggestedLogin: "root"));

        Assert.DoesNotContain("dca-capability-ports", await RenderAsync(broker), StringComparison.Ordinal);
    }

    private static DangerousCapabilityApprovalRequest CreateRequest(string? suggestedLogin, IReadOnlyList<string>? realNetworkPorts = null)
    {
        AppManifest manifest = new()
        {
            Id = "org.example.scanner",
            Name = "Scanner",
            Version = "1.2.0",
            PublisherId = "org.example",
            Description = "Overlay fixture.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Scanner", "Scanner.ScannerWindow"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
            Capabilities = [AppCapabilities.NetworkRealAccess, AppCapabilities.ProcessManage],
            RealNetworkPorts = realNetworkPorts
        };
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
            suggestedLogin,
            (_, _, _) => Task.FromResult(new DangerousCapabilityApprovalAttempt(false, 2, "invalid-credentials")));
    }
}
