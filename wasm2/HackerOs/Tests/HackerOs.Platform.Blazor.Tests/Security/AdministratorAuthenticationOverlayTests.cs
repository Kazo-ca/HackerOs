using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Icons;
using HackerOs.Platform.Blazor.Security;
using HackerOs.Platform.Blazor.Tests.Localization;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Platform.Core.Elevation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Blazor.Tests.Security;

/// <summary>Render contract of the OS authentication agent's overlay (ADR 0055 section 5).</summary>
public sealed class AdministratorAuthenticationOverlayTests
{
    [Fact]
    public async Task Overlay_shows_the_operation_and_the_app_and_disables_authenticate_without_password()
    {
        AdministratorAuthenticationBroker broker = new();
        using IDisposable host = broker.Attach();
        Task<AdministratorAuthenticationOutcome> outcome = broker.RequestAsync(CreateRequest("admin"));

        string html = await RenderAsync(broker);
        string text = System.Net.WebUtility.HtmlDecode(html);

        Assert.Contains("Administrator authentication", text, StringComparison.Ordinal);
        Assert.Contains("Administrator name", text, StringComparison.Ordinal);
        Assert.Contains("Uninstall Calculator for all users", text, StringComparison.Ordinal);
        Assert.Contains("org.hackeros.settings", html, StringComparison.Ordinal);
        Assert.Contains("value=\"admin\"", html, StringComparison.Ordinal);
        Assert.Matches("<button type=\"submit\" class=\"aaa-authenticate\" disabled", html);
        Assert.Contains("/var/log/auth.log", html, StringComparison.Ordinal);
        Assert.False(outcome.IsCompleted);
    }

    [Fact]
    public async Task Overlay_speaks_the_session_language_French_after_sign_in()
    {
        AdministratorAuthenticationBroker broker = new();
        using IDisposable host = broker.Attach();
        _ = broker.RequestAsync(CreateRequest("admin"));

        string text = System.Net.WebUtility.HtmlDecode(
            await RenderAsync(broker, TestLocalization.CreateSessionLocale("fr_CA.UTF-8")));

        Assert.Contains("Authentification administrateur", text, StringComparison.Ordinal);
        Assert.Contains("Nom d'administrateur", text, StringComparison.Ordinal);
        Assert.Contains(">Annuler</button>", text, StringComparison.Ordinal);
        Assert.Contains("Authentifier", text, StringComparison.Ordinal);
        Assert.Contains("Cette demande sera inscrite dans /var/log/auth.log", text, StringComparison.Ordinal);
        // The operation comes from the requester; it is not the overlay's to translate.
        Assert.Contains("Uninstall Calculator for all users", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_lone_guest_is_asked_for_a_plain_confirmation()
    {
        HackerOs.Platform.Core.Sessions.InMemoryLocalUserRepository users = new();
        HackerOs.Platform.Core.Diagnostics.BoundedAuditLog audit = new(maxEntries: 10);
        await new HackerOs.Platform.Core.Sessions.GuestAccountService(
            users, new HackerOs.Platform.Core.Sessions.InMemoryLocalGroupRepository(), audit).CreateAsync("Guest");
        AdministratorAuthenticationBroker broker = new();
        using IDisposable host = broker.Attach();
        _ = broker.RequestAsync(CreateRequest("guest"));

        string html = await RenderAsync(broker, verifier: new AdminElevationVerifier(users, audit));
        string text = System.Net.WebUtility.HtmlDecode(html);

        Assert.Contains("no password is asked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"password\"", html, StringComparison.Ordinal);
        // Enabled without a password (scoped-CSS attributes follow the class).
        Assert.Matches("class=\"aaa-authenticate\"(?![^>]*disabled)", html);
        Assert.Contains("Confirm", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_who_is_not_the_lone_guest_still_types_a_password()
    {
        HackerOs.Platform.Core.Sessions.InMemoryLocalUserRepository users = new();
        HackerOs.Platform.Core.Diagnostics.BoundedAuditLog audit = new(maxEntries: 10);
        AdministratorAuthenticationBroker broker = new();
        using IDisposable host = broker.Attach();
        _ = broker.RequestAsync(CreateRequest("admin"));

        string html = await RenderAsync(broker, verifier: new AdminElevationVerifier(users, audit));

        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlay_renders_nothing_without_a_pending_request()
    {
        string html = await RenderAsync(new AdministratorAuthenticationBroker());

        Assert.DoesNotContain("aaa-veil", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Broker_without_an_overlay_reports_the_prompt_unavailable()
    {
        AdministratorAuthenticationOutcome outcome = await new AdministratorAuthenticationBroker().RequestAsync(CreateRequest(null));

        Assert.Equal(AdministratorAuthenticationOutcome.Unavailable, outcome);
    }

    [Fact]
    public async Task Removing_the_overlay_cancels_the_pending_request()
    {
        AdministratorAuthenticationBroker broker = new();
        IDisposable host = broker.Attach();
        Task<AdministratorAuthenticationOutcome> outcome = broker.RequestAsync(CreateRequest(null));

        host.Dispose();

        Assert.Equal(AdministratorAuthenticationOutcome.Cancelled, await outcome);
        Assert.Null(broker.Current);
    }

    private static async Task<string> RenderAsync(
        AdministratorAuthenticationBroker broker, SessionLocaleService? locale = null, AdminElevationVerifier? verifier = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(broker);
        if (verifier is not null)
        {
            services.AddSingleton<HackerOs.App.Abstractions.Policy.IAdminElevationVerifier>(verifier);
        }

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
            var root = await renderer.RenderComponentAsync<AdministratorAuthenticationOverlay>(ParameterView.Empty);
            return root.ToHtmlString();
        });
    }

    private static AdministratorAuthenticationPromptRequest CreateRequest(string? suggestedLogin) =>
        new(
            new AppManifest
            {
                Id = "org.hackeros.settings",
                Name = "Settings",
                Version = "1.0.0",
                PublisherId = "pub.hackeros",
                Description = "Overlay fixture.",
                Kind = AppKind.Window,
                SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
                Presentation = new PresentationManifest("system", AppLaunchVisibility.Visible, []),
                Resources = AppResourceProfileManifest.None
            },
            "Uninstall Calculator for all users",
            null,
            suggestedLogin,
            (_, _, _) => Task.FromResult(new AdministratorAuthenticationAttempt(false, 2, "invalid-credentials")));
}
