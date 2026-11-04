using System.Collections.Immutable;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Platform.Core.Network.Websites;

/// <summary>
/// Simulated banking website at cryptobank.com.
/// Mirrors the legacy browser.ts <c>getBankPage()</c> with a login form
/// and account dashboard secured by a session cookie.
/// </summary>
public sealed class CryptoBankController : SimulatedWebsiteControllerBase
{
    private const string SessionCookie = "cryptobank_session";

    /// <inheritdoc/>
    public override string PrimaryHostname => "cryptobank.com";
    /// <inheritdoc/>
    public override string Theme           => "bank";
    /// <inheritdoc/>
    public override string SiteName        => "CryptoBank";

    /// <summary>Registers the routes: <c>/</c> and <c>/account</c> (dashboard when logged in, else the login form), GET/POST <c>/login</c> and <c>/logout</c>.</summary>
    public CryptoBankController()
    {
        Get("/",        req => IsLoggedIn(req) ? SimulatedHttpResponse.Ok(BuildDashboardPage(GetUsername(req)))
                                              : SimulatedHttpResponse.Ok(BuildLoginPage(false)));
        Get("/login",   _   => SimulatedHttpResponse.Ok(BuildLoginPage(false)));
        Post("/login",  req =>
        {
            var username = req.FormBody?.GetValueOrDefault("username", "") ?? "";
            var password = req.FormBody?.GetValueOrDefault("password", "") ?? "";

            return string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)
                ? SimulatedHttpResponse.Ok(BuildLoginPage(true))
                : SimulatedHttpResponse.Redirect(
                    "/account",
                    setCookies: ImmutableDictionary<string, string>.Empty
                        .Add(SessionCookie, $"user={username}"));
        });
        Get("/account", req => IsLoggedIn(req) ? SimulatedHttpResponse.Ok(BuildDashboardPage(GetUsername(req)))
                                              : SimulatedHttpResponse.Redirect("/login"));
        Get("/logout",  _ => SimulatedHttpResponse.Redirect(
            "/login",
            setCookies: ImmutableDictionary<string, string>.Empty.Add(SessionCookie, "")));
    }

    private static bool IsLoggedIn(SimulatedHttpRequest req) =>
        req.Cookies.TryGetValue(SessionCookie, out var v) && !string.IsNullOrEmpty(v);

    private static string GetUsername(SimulatedHttpRequest req) =>
        req.Cookies.TryGetValue(SessionCookie, out var v) && v.StartsWith("user=", StringComparison.Ordinal)
            ? v[5..] : "User";

    private static SimulatedPage BuildLoginPage(bool failed)
    {
        var builder = ImmutableArray.CreateBuilder<SimulatedPageSection>();
        builder.Add(new HeroSection("CryptoBank", "Next-Generation Digital Banking"));
        builder.Add(new NavigationSection(
        [
            new NavLink("Personal",  "#"),
            new NavLink("Business",  "#"),
            new NavLink("About",     "#"),
            new NavLink("Contact",   "#"),
        ]));
        if (failed)
            builder.Add(new AlertSection("Invalid credentials. Please try again.", AlertLevel.Error));
        builder.Add(new LoginFormSection(
            Title: "Secure Login",
            UsernameLabel: "Username",
            PasswordLabel: "Password",
            SubmitLabel: "Login",
            PostPath: "/login"));
        return new SimulatedPage("CryptoBank — Secure Login", "bank", builder.ToImmutable());
    }

    private static SimulatedPage BuildDashboardPage(string username) => new(
        Title: "CryptoBank — Account Dashboard",
        PageTheme: "bank",
        Sections:
        [
            new HeroSection("CryptoBank", $"Welcome, {username}"),
            new NavigationSection(
            [
                new NavLink("Dashboard",  "/account", IsActive: true),
                new NavLink("Transfer",   "#"),
                new NavLink("History",    "#"),
                new NavLink("Settings",   "#"),
                new NavLink("Logout",     "/logout"),
            ]),
            new TableSection(
                Title: "Account Summary",
                Headers: ["Account", "Balance", "Currency"],
                Rows:
                [
                    ["Checking",   "4,215.50",  "BTC-USD"],
                    ["Savings",    "12,880.00", "BTC-USD"],
                    ["Crypto Wallet", "0.42175", "BTC"],
                ]),
            new AlertSection(
                "This is a simulated banking interface for educational purposes. All data is fictional.",
                AlertLevel.Info),
        ]);
}
