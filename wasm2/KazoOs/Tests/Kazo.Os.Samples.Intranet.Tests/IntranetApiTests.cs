using System.Text.Json;
using Kazo.Os.AppSdk.Web.Http;

namespace Kazo.Os.Samples.Intranet.Tests;

/// <summary>The REST API of the Intranet sample: every verb of <c>/api/notes</c>, the directory and the board's tasks.</summary>
public sealed class IntranetApiTests
{
    [Fact]
    public async Task Health_reports_the_counters_of_the_site_process()
    {
        HackWebResponseData health = await new IntranetClient().GetAsync("/health");

        Assert.Equal(200, health.StatusCode);
        Assert.StartsWith("application/json", health.ContentType);
        using JsonDocument json = JsonDocument.Parse(health.ReadAsString());
        Assert.Equal("up", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("notes").GetInt32());
        Assert.Equal(4, json.RootElement.GetProperty("tasks").GetInt32());
    }

    [Fact]
    public async Task Notes_of_one_author_are_read_through_the_database_index()
    {
        IntranetClient client = new();
        await client.SendJsonAsync("POST", "/api/notes", """{"title":"Mine","body":"x","author":"neo"}""");

        using JsonDocument itOps = JsonDocument.Parse((await client.GetAsync("/api/notes?author=it-ops")).ReadAsString());
        using JsonDocument neo = JsonDocument.Parse((await client.GetAsync("/api/notes?author=neo&q=min")).ReadAsString());

        Assert.Equal(["Welcome to the intranet", "VPN maintenance"], itOps.RootElement.EnumerateArray().Select(note => note.GetProperty("title").GetString()));
        Assert.Equal("Mine", Assert.Single(neo.RootElement.EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Deleting_every_note_does_not_bring_the_welcome_notes_back()
    {
        IntranetClient client = new();
        for (int id = 1; id <= 3; id++)
        {
            Assert.Equal(204, (await client.DeleteAsync($"/api/notes/{id}")).StatusCode);
        }

        using JsonDocument health = JsonDocument.Parse((await client.GetAsync("/health")).ReadAsString());
        Assert.Equal(0, health.RootElement.GetProperty("notes").GetInt32());
    }

    [Fact]
    public async Task Notes_answer_every_verb_with_json_and_problems()
    {
        IntranetClient client = new();

        using (JsonDocument list = JsonDocument.Parse((await client.GetAsync("/api/notes")).ReadAsString()))
        {
            Assert.Equal(3, list.RootElement.GetArrayLength());
            Assert.True(list.RootElement[0].GetProperty("pinned").GetBoolean());
        }

        using (JsonDocument search = JsonDocument.Parse((await client.GetAsync("/api/notes?q=vpn")).ReadAsString()))
        {
            Assert.Equal("VPN maintenance", Assert.Single(search.RootElement.EnumerateArray()).GetProperty("title").GetString());
        }

        HackWebResponseData missing = await client.GetAsync("/api/notes/42");
        Assert.Equal(404, missing.StatusCode);
        Assert.StartsWith("application/problem+json", missing.ContentType);

        HackWebResponseData invalid = await client.SendJsonAsync("POST", "/api/notes", """{"title":"x"}""");
        Assert.Equal(400, invalid.StatusCode);
        Assert.Contains("\"Title\"", invalid.ReadAsString());

        HackWebResponseData created = await client.SendJsonAsync("POST", "/api/notes", """{"title":"From the API","body":"curl -d"}""");
        Assert.Equal(201, created.StatusCode);
        Assert.Equal("http://intranet.hack/api/notes/4", IntranetClient.Location(created));
        Assert.Contains("\"author\":\"anonymous\"", created.ReadAsString());

        HackWebResponseData replaced = await client.SendJsonAsync("PUT", "/api/notes/4", """{"title":"Replaced","body":"","pinned":false,"author":"api"}""");
        Assert.Equal(200, replaced.StatusCode);
        Assert.Contains("\"title\":\"Replaced\"", replaced.ReadAsString());

        HackWebResponseData pinned = await client.SendJsonAsync("PATCH", "/api/notes/4", """{"pinned":true}""");
        Assert.Equal(200, pinned.StatusCode);
        Assert.Contains("\"pinned\":true", pinned.ReadAsString());
        Assert.Contains("\"title\":\"Replaced\"", pinned.ReadAsString());

        HackWebResponseData badPatch = await client.SendJsonAsync("PATCH", "/api/notes/4", """{"title":"no"}""");
        Assert.Equal(400, badPatch.StatusCode);
        Assert.Contains("\"Title\"", badPatch.ReadAsString());

        Assert.Equal(204, (await client.DeleteAsync("/api/notes/4")).StatusCode);
        Assert.Equal(404, (await client.DeleteAsync("/api/notes/4")).StatusCode);
        Assert.Equal(405, (await client.DeleteAsync("/api/notes")).StatusCode);
    }

    [Fact]
    public async Task People_are_searched_as_json()
    {
        IntranetClient client = new();

        using JsonDocument web = JsonDocument.Parse((await client.GetAsync("/api/people?q=web")).ReadAsString());
        Assert.Equal(["Morpheus", "Thomas Anderson"], web.RootElement.EnumerateArray().Select(person => person.GetProperty("name").GetString()));
        Assert.Contains("\"handle\":\"grace\"", (await client.GetAsync("/api/people/3")).ReadAsString());
        Assert.Equal(404, (await client.GetAsync("/api/people/99")).StatusCode);
    }

    [Fact]
    public async Task Tasks_are_created_moved_and_deleted_for_the_board()
    {
        IntranetClient client = new();

        HackWebResponseData wrongColumn = await client.SendJsonAsync("POST", "/api/tasks", """{"title":"Audit","column":"later"}""");
        Assert.Equal(400, wrongColumn.StatusCode);
        Assert.Contains("\"Column\"", wrongColumn.ReadAsString());

        HackWebResponseData created = await client.SendJsonAsync("POST", "/api/tasks", """{"title":"Audit the firewall","assignee":"trinity"}""");
        Assert.Equal(201, created.StatusCode);
        Assert.Equal("http://intranet.hack/api/tasks/5", IntranetClient.Location(created));
        Assert.Contains("\"column\":\"todo\"", created.ReadAsString());

        HackWebResponseData moved = await client.SendJsonAsync("PUT", "/api/tasks/5", """{"title":"Audit the firewall","assignee":"trinity","column":"doing"}""");
        Assert.Contains("\"column\":\"doing\"", moved.ReadAsString());

        using (JsonDocument all = JsonDocument.Parse((await client.GetAsync("/api/tasks")).ReadAsString()))
        {
            Assert.Equal(5, all.RootElement.GetArrayLength());
        }

        Assert.Equal(204, (await client.DeleteAsync("/api/tasks/5")).StatusCode);
        Assert.Equal(404, (await client.GetAsync("/api/tasks/5")).StatusCode);
    }
}
