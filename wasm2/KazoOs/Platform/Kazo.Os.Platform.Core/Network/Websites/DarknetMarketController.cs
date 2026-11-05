using System.Collections.Immutable;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Platform.Core.Network.Websites;

/// <summary>
/// Simulated darknet marketplace at darknet.market.
/// Mirrors the legacy browser.ts <c>getDarknetMarketPage()</c> with a product
/// grid themed for the fictional underground economy. All products are fictional
/// and the disclaimer is preserved.
/// </summary>
public sealed class DarknetMarketController : SimulatedWebsiteControllerBase
{
    /// <inheritdoc/>
    public override string PrimaryHostname => "darknet.market";
    /// <inheritdoc/>
    public override string Theme           => "darknet";
    /// <inheritdoc/>
    public override string SiteName        => "DarkNet Market";

    /// <summary>Registers the routes: <c>/</c> and <c>/marketplace</c> (the product grid) and <c>/account</c>.</summary>
    public DarknetMarketController()
    {
        Get("/",            _ => SimulatedHttpResponse.Ok(BuildHomePage()));
        Get("/marketplace", _ => SimulatedHttpResponse.Ok(BuildHomePage()));
        Get("/account",     _ => SimulatedHttpResponse.Ok(BuildAccountPage()));
    }

    private static SimulatedPage BuildHomePage() => new(
        Title: "DarkNet Market",
        PageTheme: "darknet",
        Sections:
        [
            new HeroSection("DarkNet Market"),
            new NavigationSection(
            [
                new NavLink("Home",        "/",            IsActive: true),
                new NavLink("Marketplace", "/marketplace"),
                new NavLink("Account",     "/account"),
                new NavLink("Messages",    "#"),
                new NavLink("Cart",        "#"),
            ]),
            new AlertSection(
                "Warning: This marketplace is monitored. Use of this service implies acceptance of all risks.",
                AlertLevel.Warning),
            new NavigationSection(
            [
                new NavLink("Digital Goods",    "#"),
                new NavLink("Services",         "#"),
                new NavLink("Hardware",         "#"),
                new NavLink("Software",         "#"),
                new NavLink("Zero-day Exploits","#"),
            ]),
            new ProductGridSection(
            [
                new ProductCard(
                    "Premium VPN Service - 1 Year",
                    "Untraceable connection, no logs policy, 50+ servers globally.",
                    "0.012 BTC"),
                new ProductCard(
                    "USB Password Cracker",
                    "Hardware device to extract stored passwords from any system.",
                    "0.25 BTC"),
                new ProductCard(
                    "Custom Malware Development",
                    "Bespoke malware created for your specific needs. Undetectable by most AV.",
                    "Starting at 0.5 BTC",
                    Badge: "Custom"),
                new ProductCard(
                    "WiFi Pineapple Mark VII",
                    "The ultimate rogue access point for MITM attacks.",
                    "0.15 BTC"),
            ]),
            new AlertSection(
                "This is a simulated illegal marketplace for educational purposes only. All products are fictional.",
                AlertLevel.Info),
        ]);

    private static SimulatedPage BuildAccountPage() => new(
        Title: "DarkNet Market — Account",
        PageTheme: "darknet",
        Sections:
        [
            new HeroSection("DarkNet Market — Account"),
            new AlertSection(
                "You must be logged in to access your account.", AlertLevel.Warning),
            new LoginFormSection(
                Title: "Login",
                UsernameLabel: "Alias",
                PasswordLabel: "Passphrase",
                SubmitLabel: "Enter",
                PostPath: "/login"),
        ]);
}
