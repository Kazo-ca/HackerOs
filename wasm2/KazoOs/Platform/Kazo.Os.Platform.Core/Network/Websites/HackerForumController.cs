using System.Collections.Immutable;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Platform.Core.Network.Websites;

/// <summary>
/// Simulated hacker community forum at hackerz.forum.
/// Mirrors the legacy browser.ts <c>getHackerForumPage()</c> as typed forum sections.
/// </summary>
public sealed class HackerForumController : SimulatedWebsiteControllerBase
{
    /// <inheritdoc/>
    public override string PrimaryHostname => "hackerz.forum";
    /// <inheritdoc/>
    public override string Theme           => "forum";
    /// <inheritdoc/>
    public override string SiteName        => "Hackerz Forum";

    /// <summary>Registers the routes: <c>/</c> and <c>/forums</c>, both serving the forum index.</summary>
    public HackerForumController()
    {
        Get("/",       _ => SimulatedHttpResponse.Ok(BuildMainPage()));
        Get("/forums", _ => SimulatedHttpResponse.Ok(BuildMainPage()));
    }

    private static SimulatedPage BuildMainPage() => new(
        Title: "Hackerz Forum",
        PageTheme: "forum",
        Sections:
        [
            new HeroSection("Hackerz Forum"),
            new NavigationSection(
            [
                new NavLink("Home",        "/",       IsActive: true),
                new NavLink("Forums",      "/forums"),
                new NavLink("Members",     "#"),
                new NavLink("Profile",     "#"),
                new NavLink("Messages (3)","#"),
            ]),
            new ForumSection(
                SectionTitle: "Announcements",
                Threads:
                [
                    new ForumThread("Forum Rules - READ BEFORE POSTING", "Moderator",       "2 days ago",   1200),
                    new ForumThread("Welcome to Hackerz Forum",           "Admin",           "1 week ago",    880),
                ]),
            new ForumSection(
                SectionTitle: "General Hacking",
                Threads:
                [
                    new ForumThread("CVE-2024-8742 — Critical RCE in Apache",    "d3xt3r",    "3 hours ago",  4521, IsHot: true),
                    new ForumThread("Best wordlist for WPA2 cracking in 2025?",  "n00b_hax0r", "Yesterday",   1033),
                    new ForumThread("SQL injection bypass on modern WAFs",        "inj3ctor",  "2 days ago",    765),
                ]),
            new ForumSection(
                SectionTitle: "Tools & Resources",
                Threads:
                [
                    new ForumThread("Metasploit module for targetbank.com",       "p3n3trat0r", "5 hours ago", 2104, IsHot: true),
                    new ForumThread("Custom Python reverse shell - FUD",          "bl4ckh4t",  "1 day ago",    893),
                    new ForumThread("Burp Suite custom extension — share yours",  "webh4ck3r", "3 days ago",   541),
                ]),
        ]);
}
