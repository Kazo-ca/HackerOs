using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Icons;
using HackerOs.Platform.Blazor.Security;
using HackerOs.Platform.Blazor.Tests.Localization;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Blazor.Tests.Security;

/// <summary>The approval window of ADR 0189 §4, step 2: its broker and what it shows.</summary>
public sealed class AppInstallApprovalOverlayTests
{
    [Fact]
    public async Task Without_an_overlay_the_request_is_unavailable_at_once()
    {
        AppInstallApprovalBroker broker = new();

        Assert.Equal(AppInstallApprovalOutcome.Unavailable, await broker.RequestAsync(CreateRequest(AppSourceTrust.Trusted)));
        Assert.Null(broker.Current);
    }

    [Fact]
    public async Task Requests_are_shown_one_at_a_time_and_end_with_the_answer()
    {
        AppInstallApprovalBroker broker = new();
        using IDisposable host = broker.Attach();
        Task<AppInstallApprovalOutcome> first = broker.RequestAsync(CreateRequest(AppSourceTrust.Trusted));
        Task<AppInstallApprovalOutcome> second = broker.RequestAsync(CreateRequest(AppSourceTrust.Untrusted));

        Assert.Equal(AppSourceTrust.Trusted, broker.Current!.Request.Trust);
        broker.Current.Complete(AppInstallApprovalOutcome.Approved);
        Assert.Equal(AppSourceTrust.Untrusted, broker.Current!.Request.Trust);
        broker.Current.Complete(AppInstallApprovalOutcome.Refused);

        Assert.Equal(AppInstallApprovalOutcome.Approved, await first);
        Assert.Equal(AppInstallApprovalOutcome.Refused, await second);
        Assert.Null(broker.Current);
    }

    [Fact]
    public async Task Removing_the_overlay_or_cancelling_refuses()
    {
        AppInstallApprovalBroker broker = new();
        IDisposable host = broker.Attach();
        using CancellationTokenSource cancel = new();
        Task<AppInstallApprovalOutcome> cancelled = broker.RequestAsync(CreateRequest(AppSourceTrust.Trusted), cancel.Token);
        Task<AppInstallApprovalOutcome> orphaned = broker.RequestAsync(CreateRequest(AppSourceTrust.Trusted));

        await cancel.CancelAsync();
        host.Dispose();

        Assert.Equal(AppInstallApprovalOutcome.Refused, await cancelled);
        Assert.Equal(AppInstallApprovalOutcome.Refused, await orphaned);
    }

    [Fact]
    public async Task The_window_shows_the_app_its_source_trust_recipient_and_dangerous_capabilities()
    {
        AppInstallApprovalBroker broker = new();
        using IDisposable host = broker.Attach();
        _ = broker.RequestAsync(CreateRequest(AppSourceTrust.Untrusted) with
        {
            SystemWide = false,
            BlockReason = new AppBlockReason(AppBlockKind.Category, "social"),
        });

        string text = System.Net.WebUtility.HtmlDecode(await RenderAsync(broker));

        Assert.Contains("Approve an app update", text, StringComparison.Ordinal);
        Assert.Contains("Webamp", text, StringComparison.Ordinal);
        Assert.Contains("org.kazo.webamp", text, StringComparison.Ordinal);
        Assert.Contains("github:Kazo-ca/HackerOs-App-Webamp@1.1.0", text, StringComparison.Ordinal);
        Assert.Contains("1.0.0", text, StringComparison.Ordinal);
        Assert.Contains("Untrusted", text, StringComparison.Ordinal);
        Assert.Contains("nobody reviewed this app", text, StringComparison.Ordinal);
        Assert.Contains("blocked-categories: social", text, StringComparison.Ordinal);
        Assert.Contains("- only", text, StringComparison.Ordinal);
        Assert.Contains(AppCapabilities.NetworkRealAccess, text, StringComparison.Ordinal);
        Assert.Contains(">Update</button>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_window_speaks_French()
    {
        AppInstallApprovalBroker broker = new();
        using IDisposable host = broker.Attach();
        _ = broker.RequestAsync(CreateRequest(AppSourceTrust.Trusted));

        string text = System.Net.WebUtility.HtmlDecode(
            await RenderAsync(broker, TestLocalization.CreateSessionLocale("fr_CA.UTF-8")));

        Assert.Contains("De confiance", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve an app", text, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(AppInstallApprovalBroker broker, SessionLocaleService? locale = null)
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
            var root = await renderer.RenderComponentAsync<AppInstallApprovalOverlay>(ParameterView.Empty);
            return root.ToHtmlString();
        });
    }

    private static AppInstallApprovalRequest CreateRequest(AppSourceTrust trust)
    {
        AppManifest store = new()
        {
            Id = "org.hackeros.commands.hackpkg",
            Name = "hackpkg",
            Version = "1.0.0",
            PublisherId = "pub.hackeros",
            Description = "Overlay fixture.",
            Kind = AppKind.Terminal,
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("system", AppLaunchVisibility.Hidden, []),
            Resources = AppResourceProfileManifest.None
        };
        return new AppInstallApprovalRequest(
            "org.kazo.webamp",
            "Webamp",
            "1.1.0",
            "1.0.0",
            2_400_000,
            "github:Kazo-ca/HackerOs-App-Webamp@1.1.0",
            trust,
            [AppCapabilities.NetworkRealAccess],
            AppInstallApprovalOperation.Update,
            true,
            "admin",
            null,
            store);
    }
}
