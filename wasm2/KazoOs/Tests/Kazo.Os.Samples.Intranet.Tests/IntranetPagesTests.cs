using Kazo.Os.AppSdk.Web.Http;

namespace Kazo.Os.Samples.Intranet.Tests;

/// <summary>
/// The MVC side of the Intranet sample: <c>.cshtml</c> and <c>.razor</c> views in one <c>.razor</c>
/// layout, the embedded <c>wwwroot</c>, the cookie session, anti-forgery and validation of the forms.
/// </summary>
public sealed class IntranetPagesTests
{
    [Fact]
    public async Task Home_is_a_cshtml_view_in_the_razor_layout_with_its_assets()
    {
        IntranetClient client = new();

        HackWebResponseData home = await client.GetAsync("/");

        Assert.Equal(200, home.StatusCode);
        Assert.StartsWith("text/html", home.ContentType);
        Assert.Equal("ASP.Hack", IntranetClient.Header(home, "X-Powered-By"));
        string html = home.ReadAsString();
        Assert.Contains("<title>Home - Intranet</title>", html);
        Assert.Contains("Welcome, stranger", html);
        Assert.Contains("href=\"/css/site.css\"", html);
        Assert.Contains("<script src=\"/js/health.js\"></script>", html);
        Assert.Contains("class=\"active\" id=\"nav-home\"", html);
        Assert.Contains("intranet.hack is live", html);
        Assert.Contains("id=\"note-count\">3<", html);
        Assert.Contains("id=\"task-count\">4<", html);
    }

    [Theory]
    [InlineData("/css/site.css", "text/css")]
    [InlineData("/js/health.js", "javascript")]
    [InlineData("/js/people.js", "javascript")]
    [InlineData("/js/notes.js", "javascript")]
    public async Task The_embedded_wwwroot_is_served(string path, string type)
    {
        HackWebResponseData file = await new IntranetClient().GetAsync(path);

        Assert.Equal(200, file.StatusCode);
        Assert.Contains(type, file.ContentType);
        Assert.NotNull(IntranetClient.Header(file, "ETag"));
    }

    [Fact]
    public async Task The_profile_form_needs_its_token_validates_and_keeps_the_name_in_the_session()
    {
        IntranetClient client = new();
        string form = (await client.GetAsync("/Home/Profile")).ReadAsString();
        Assert.Contains("<label for=\"Name\">Display name</label>", form);

        HackWebResponseData forged = await client.PostFormAsync("/Home/Profile", formPage: null, ("Name", "Mallory"));
        Assert.Equal(400, forged.StatusCode);

        HackWebResponseData invalid = await client.PostFormAsync("/Home/Profile", form, ("Name", "x"));
        Assert.Equal(200, invalid.StatusCode);
        string redisplayed = invalid.ReadAsString();
        Assert.Contains("field-validation-error", redisplayed);
        Assert.Contains("input-validation-error", redisplayed);

        HackWebResponseData saved = await client.PostFormAsync("/Home/Profile", form, ("Name", "Neo"));
        Assert.Equal(302, saved.StatusCode);
        Assert.Equal("/", IntranetClient.Location(saved));
        Assert.Contains(".HackSession", client.Cookies.Keys);

        string home = (await client.GetAsync("/")).ReadAsString();
        Assert.Contains("Welcome back, Neo", home);
        Assert.Contains("id=\"whoami\">Neo<", home);
        Assert.Contains("\"displayName\":\"Neo\"", (await client.GetAsync("/api/me")).ReadAsString());

        string profile = (await client.GetAsync("/Home/Profile")).ReadAsString();
        HackWebResponseData forgotten = await client.PostFormAsync("/Home/SignOut", profile);
        Assert.Equal(302, forgotten.StatusCode);
        Assert.Contains("\"displayName\":null", (await client.GetAsync("/api/me")).ReadAsString());
    }

    [Fact]
    public async Task Notes_are_created_edited_and_deleted_through_razor_views_and_forms()
    {
        IntranetClient client = new();
        string list = (await client.GetAsync("/Notes")).ReadAsString();
        Assert.Contains("<title>Notes - Intranet</title>", list);
        Assert.Contains("class=\"active\" id=\"nav-notes\"", list);
        Assert.Contains("Welcome to the intranet", list);
        Assert.Contains("<script src=\"/js/notes.js\"></script>", list);
        // Pinned notes first.
        Assert.True(list.IndexOf("Welcome to the intranet", StringComparison.Ordinal) < list.IndexOf("VPN maintenance", StringComparison.Ordinal));

        string create = (await client.GetAsync("/Notes/Create")).ReadAsString();
        Assert.Contains("action=\"/Notes/Create\"", create);
        HackWebResponseData refused = await client.PostFormAsync("/Notes/Create", create, ("Title", "ab"), ("Body", "too short a title"));
        Assert.Equal(200, refused.StatusCode);
        Assert.Contains("field-validation-error", refused.ReadAsString());
        Assert.Contains("too short a title", refused.ReadAsString());

        HackWebResponseData created = await client.PostFormAsync("/Notes/Create", create, ("Title", "Server room"), ("Body", "Badge required."), ("Pinned", "on"));
        Assert.Equal(302, created.StatusCode);
        Assert.Equal("/Notes/Details/4", IntranetClient.Location(created));

        string details = (await client.GetAsync("/Notes/Details/4")).ReadAsString();
        Assert.Contains("<title>Server room - Intranet</title>", details);
        Assert.Contains("by anonymous", details);
        Assert.Contains(">pinned<", details);

        string edit = (await client.GetAsync("/Notes/Edit/4")).ReadAsString();
        Assert.Contains("action=\"/Notes/Edit/4\"", edit);
        Assert.Contains("checked", edit);
        HackWebResponseData edited = await client.PostFormAsync("/Notes/Edit/4", edit, ("Title", "Server room B"), ("Body", "Badge and PIN required."));
        Assert.Equal("/Notes/Details/4", IntranetClient.Location(edited));
        string after = (await client.GetAsync("/Notes/Details/4")).ReadAsString();
        Assert.Contains("Badge and PIN required.", after);
        Assert.DoesNotContain(">pinned<", after);

        HackWebResponseData deleted = await client.PostFormAsync("/Notes/Delete/4", after);
        Assert.Equal("/Notes", IntranetClient.Location(deleted));
        Assert.Equal(404, (await client.GetAsync("/Notes/Details/4")).StatusCode);
    }

    [Fact]
    public async Task People_are_searched_by_the_server_without_script()
    {
        string html = (await new IntranetClient().GetAsync("/People?q=security")).ReadAsString();

        Assert.Contains("value=\"security\"", html);
        Assert.Contains("Alan Turing", html);
        Assert.Contains("Trinity", html);
        Assert.DoesNotContain("Grace Hopper", html);
        Assert.Contains("2 result(s)", html);
        Assert.Contains("@aturing", html);
        Assert.Contains("data-api=\"/api/people\"", html);
    }

    [Fact]
    public async Task An_unknown_note_is_a_404_page()
    {
        HackWebResponseData missing = await new IntranetClient().GetAsync("/Notes/Edit/99");

        Assert.Equal(404, missing.StatusCode);
    }
}
