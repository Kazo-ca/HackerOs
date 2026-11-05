using System.Collections.Immutable;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Platform.Core.Network.Websites;

/// <summary>
/// Simulated email service at hackmail.com.
/// Mirrors the legacy browser.ts <c>getEmailPage()</c> content as structured sections.
/// Session-scoped login state is tracked via session cookies set at POST /login.
/// </summary>
public sealed class KazoMailController : SimulatedWebsiteControllerBase
{
    private const string SessionCookie = "hackmail_session";

    /// <inheritdoc/>
    public override string PrimaryHostname => "hackmail.com";
    /// <inheritdoc/>
    public override string Theme           => "mail";
    /// <inheritdoc/>
    public override string SiteName        => "KazoMail";

    /// <summary>Registers the routes: <c>/</c> and <c>/inbox</c> (the inbox when logged in, else the login form), GET/POST <c>/login</c> and <c>/logout</c>.</summary>
    public KazoMailController()
    {
        Get("/",       req => IsLoggedIn(req) ? SimulatedHttpResponse.Ok(BuildInboxPage())
                                              : SimulatedHttpResponse.Redirect("/login"));
        Get("/inbox",  req => IsLoggedIn(req) ? SimulatedHttpResponse.Ok(BuildInboxPage())
                                              : SimulatedHttpResponse.Redirect("/login"));
        Get("/login",  _   => SimulatedHttpResponse.Ok(BuildLoginPage(failed: false)));
        Post("/login", req =>
        {
            // Accept any non-empty username/password pair for the simulation.
            var username = req.FormBody?.GetValueOrDefault("username", "") ?? "";
            var password = req.FormBody?.GetValueOrDefault("password", "") ?? "";

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return SimulatedHttpResponse.Ok(BuildLoginPage(failed: true));

            return SimulatedHttpResponse.Redirect(
                "/inbox",
                setCookies: ImmutableDictionary<string, string>.Empty.Add(
                    SessionCookie, $"user={username}"));
        });
        Get("/logout",  _ => SimulatedHttpResponse.Redirect(
            "/login",
            setCookies: ImmutableDictionary<string, string>.Empty.Add(SessionCookie, "")));
    }

    private static bool IsLoggedIn(SimulatedHttpRequest req) =>
        req.Cookies.TryGetValue(SessionCookie, out var v) && !string.IsNullOrEmpty(v);

    private static SimulatedPage BuildLoginPage(bool failed)
    {
        var builder = ImmutableArray.CreateBuilder<SimulatedPageSection>();
        builder.Add(new HeroSection("KazoMail", "Secure Email for the Digital Underground"));
        if (failed)
            builder.Add(new AlertSection("Invalid username or password.", AlertLevel.Error));
        builder.Add(new LoginFormSection(
            Title: "Sign In",
            UsernameLabel: "Username",
            PasswordLabel: "Password",
            SubmitLabel: "Login",
            PostPath: "/login"));
        return new SimulatedPage("KazoMail — Login", "mail", builder.ToImmutable());
    }

    private static SimulatedPage BuildInboxPage() => new(
        Title: "KazoMail — Inbox",
        PageTheme: "mail",
        Sections:
        [
            new HeroSection("KazoMail"),
            new NavigationSection(
            [
                new NavLink("Inbox",  "/inbox",  IsActive: true),
                new NavLink("Sent",   "/sent"),
                new NavLink("Drafts", "/drafts"),
                new NavLink("Spam",   "/spam"),
                new NavLink("Trash",  "/trash"),
                new NavLink("Logout", "/logout"),
            ]),
            new EmailListSection(
            [
                new EmailRow(
                    Subject: "Welcome to KazoMail",
                    Sender: "KazoMail Team",
                    Snippet: "Welcome to your new secure email account. We're excited to have you join our...",
                    IsRead: true),
                new EmailRow(
                    Subject: "Your CryptoBank Statement",
                    Sender: "noreply@cryptobank.com",
                    Snippet: "Your monthly statement is now available. Log in to view the details...",
                    IsRead: true),
                new EmailRow(
                    Subject: "First Contract: Security Audit",
                    Sender: "anonymous@secure.net",
                    Snippet: "I need someone with your skills for a security audit. The target is a small...",
                    IsRead: false),
            ]),
        ]);
}
