using System.Text.Json;
using Kazo.Os.AppSdk.Web.Generated;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Tests.Site;

namespace Kazo.Os.AppSdk.Web.Tests.Mvc;

/// <summary>
/// The whole pipeline of <see cref="TestSite"/>, with the route table the generator built for it:
/// middlewares, routing, controllers, binding, filters, results, session (ADR 0076).
/// </summary>
public sealed class MvcPipelineTests : IAsyncLifetime
{
    private const string Site = "http://intranet.hack";
    private KazoWebApplication _app = null!;

    public Task InitializeAsync()
    {
        _app = KazoWebHost.CreateApplication(new TestSite(), KazoWebGeneratedRoutes.Table);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<KazoWebResponseData> Send(string method, string path, params KeyValuePair<string, string>[] headers) =>
        _app.ProcessAsync(KazoWebRequestData.Create(method, Site + path, headers));

    private Task<KazoWebResponseData> Send(string method, string path, string contentType, string body, params KeyValuePair<string, string>[] headers) =>
        _app.ProcessAsync(KazoWebRequestData.Create(method, Site + path, contentType, body, headers));

    private static KeyValuePair<string, string> Header(string name, string value) => new(name, value);

    private static JsonElement Json(KazoWebResponseData response) => JsonDocument.Parse(response.Body).RootElement;

    [Fact]
    public async Task Minimal_route_and_middleware_run()
    {
        KazoWebResponseData response = await Send("GET", "/health");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("up", response.ReadAsString());
        Assert.Equal("ASP.Hack", response.GetHeader("X-Powered-By"));
    }

    [Fact]
    public async Task Api_lists_notes_as_camel_case_json()
    {
        KazoWebResponseData response = await Send("GET", "/api/notes");

        Assert.True(response.StatusCode == 200, response.ReadAsString());
        Assert.StartsWith("application/json", response.ContentType);
        JsonElement notes = Json(response);
        Assert.Equal(2, notes.GetArrayLength());
        Assert.Equal("First", notes[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Query_binding_filters_the_list()
    {
        JsonElement notes = Json(await Send("GET", "/api/notes?done=true"));

        Assert.Equal(1, notes.GetArrayLength());
        Assert.Equal(2, notes[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Get_by_id_and_not_found_as_problem_details()
    {
        KazoWebResponseData found = await Send("GET", "/api/notes/2");
        KazoWebResponseData missing = await Send("GET", "/api/notes/99");

        Assert.Equal("Second", Json(found).GetProperty("title").GetString());
        Assert.Equal(404, missing.StatusCode);
        Assert.StartsWith("application/problem+json", missing.ContentType);
        JsonElement problem = Json(missing);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.5", problem.GetProperty("type").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Route_constraint_rejects_a_non_integer_id()
    {
        Assert.Equal(404, (await Send("GET", "/api/notes/abc")).StatusCode);
    }

    [Fact]
    public async Task Post_creates_a_note_with_location_of_get()
    {
        KazoWebResponseData response = await Send("POST", "/api/notes", "application/json", """{"title":"Third"}""");

        Assert.Equal(201, response.StatusCode);
        Assert.Equal($"{Site}/api/Notes/3", response.GetHeader("Location"));
        Assert.Equal(3, Json(response).GetProperty("id").GetInt32());
        Assert.Equal("Third", Json(await Send("GET", "/api/notes/3")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Invalid_body_gets_an_automatic_validation_problem()
    {
        KazoWebResponseData response = await Send("POST", "/api/notes", "application/json", """{"title":"x"}""");

        Assert.Equal(400, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.ContentType);
        JsonElement errors = Json(response).GetProperty("errors");
        Assert.True(errors.TryGetProperty("Title", out JsonElement title));
        Assert.Contains("minimum length", title[0].GetString());
    }

    [Fact]
    public async Task Malformed_json_is_a_validation_problem()
    {
        KazoWebResponseData response = await Send("POST", "/api/notes", "application/json", "{not json");

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task Non_json_body_is_415()
    {
        KazoWebResponseData response = await Send("POST", "/api/notes", "text/plain", "title=Third");

        Assert.Equal(415, response.StatusCode);
    }

    [Fact]
    public async Task Patch_runs_an_async_action()
    {
        KazoWebResponseData response = await Send("PATCH", "/api/notes/1", "application/json", """{"done":true}""");

        Assert.Equal(200, response.StatusCode);
        Assert.True(Json(response).GetProperty("done").GetBoolean());
        Assert.Equal("First", Json(response).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Delete_answers_204_then_404()
    {
        Assert.Equal(204, (await Send("DELETE", "/api/notes/1")).StatusCode);
        Assert.Equal(404, (await Send("DELETE", "/api/notes/1")).StatusCode);
    }

    [Fact]
    public async Task Action_filter_short_circuits_the_action()
    {
        KazoWebResponseData response = await Send("DELETE", "/api/notes/1?deny=1");

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("denied", response.ReadAsString());
        Assert.Equal(200, (await Send("GET", "/api/notes/1")).StatusCode);
    }

    [Fact]
    public async Task Filters_run_global_then_controller_then_action()
    {
        KazoWebResponseData response = await Send("GET", "/api/notes/1");

        Assert.Equal("global>controller>action><action<controller<global", response.GetHeader("X-Trace"));
    }

    [Fact]
    public async Task Method_not_allowed_lists_allowed_methods()
    {
        KazoWebResponseData response = await Send("PUT", "/api/notes/1");

        Assert.Equal(405, response.StatusCode);
        Assert.Equal("DELETE, GET, HEAD, OPTIONS, PATCH", response.GetHeader("Allow"));
    }

    [Fact]
    public async Task Options_answers_204_with_allow()
    {
        KazoWebResponseData response = await Send("OPTIONS", "/api/notes");

        Assert.Equal(204, response.StatusCode);
        Assert.Contains("POST", response.GetHeader("Allow"));
    }

    [Fact]
    public async Task Head_runs_get_without_body()
    {
        KazoWebResponseData response = await Send("HEAD", "/api/notes");

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task Binds_route_query_header_enum_default_and_arrays()
    {
        KazoWebResponseData response = await Send("GET", "/api/notes/search?term=hack&page=2&priority=high&ids=1&ids=3", Header("X-Client", "curl"));
        KazoWebResponseData defaults = await Send("GET", "/api/notes/search?term=hack&page=1");

        Assert.Equal("term=hack page=2 client=curl priority=High ids=1,3", response.ReadAsString());
        Assert.Equal("term=hack page=1 client=- priority=Normal ids=", defaults.ReadAsString());
    }

    [Fact]
    public async Task Invalid_simple_values_give_validation_problems()
    {
        KazoWebResponseData outOfRange = await Send("GET", "/api/notes/search?term=hack&page=11");
        KazoWebResponseData notANumber = await Send("GET", "/api/notes/search?term=hack&page=two");
        KazoWebResponseData missingRequired = await Send("GET", "/api/notes/search?page=1");

        Assert.Equal(400, outOfRange.StatusCode);
        Assert.True(Json(outOfRange).GetProperty("errors").TryGetProperty("page", out _));
        Assert.Equal(400, notANumber.StatusCode);
        Assert.Equal(400, missingRequired.StatusCode);
        Assert.True(Json(missingRequired).GetProperty("errors").TryGetProperty("term", out _));
    }

    [Fact]
    public async Task Scoped_services_are_shared_within_a_request_only()
    {
        Assert.Equal("same", (await Send("GET", "/api/notes/scope")).ReadAsString());
    }

    [Fact]
    public async Task Absolute_attribute_route_ignores_the_controller_prefix()
    {
        Assert.Equal("rest=a/b/c", (await Send("GET", "/absolute/a/b/c")).ReadAsString());
        Assert.Equal(404, (await Send("GET", "/api/notes/absolute/a")).StatusCode);
    }

    [Fact]
    public async Task Non_action_methods_are_not_routed()
    {
        Assert.Equal(404, (await Send("GET", "/api/notes/helper")).StatusCode);
        Assert.Equal(404, (await Send("GET", "/notes/helper")).StatusCode);
    }

    [Fact]
    public async Task Exception_page_shows_the_error()
    {
        KazoWebResponseData html = await Send("GET", "/api/notes/throw", Header("Accept", "text/html"));
        KazoWebResponseData text = await Send("GET", "/boom");

        Assert.Equal(500, html.StatusCode);
        Assert.StartsWith("text/html", html.ContentType);
        Assert.Contains("bad format", html.ReadAsString());
        Assert.Equal(500, text.StatusCode);
        Assert.Contains("kaboom", text.ReadAsString());
    }

    [Fact]
    public async Task Without_exception_page_a_failure_is_a_bare_500()
    {
        await using KazoWebApplication app = KazoWebHost.CreateApplication(new BareSite(), KazoWebGeneratedRoutes.Table);
        List<Exception> logged = [];
        app.UnhandledException += (_, exception) => logged.Add(exception);

        KazoWebResponseData response = await app.ProcessAsync(KazoWebRequestData.Create("GET", "http://bare.hack/fail"));

        Assert.Equal(500, response.StatusCode);
        Assert.Equal("500 Internal Server Error", response.ReadAsString());
        Assert.IsType<InvalidOperationException>(Assert.Single(logged));
    }

    [Theory]
    [InlineData("/", "home")]
    [InlineData("/Home", "home")]
    [InlineData("/home/index", "home")]
    [InlineData("/home/contact", "contact form")]
    public async Task Conventional_route_reaches_page_actions(string path, string expected)
    {
        KazoWebResponseData response = await Send("GET", path);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expected, response.ReadAsString());
        Assert.StartsWith("text/plain", response.ContentType);
    }

    [Fact]
    public async Task Conventional_route_binds_the_id_segment()
    {
        KazoWebResponseData response = await Send("GET", "/home/details/12");

        Assert.Equal("<h1>Note 12</h1>", response.ReadAsString());
        Assert.StartsWith("text/html", response.ContentType);
    }

    [Fact]
    public async Task Form_post_binds_the_model_and_redirects()
    {
        KazoWebResponseData response = await Send(
            "POST", "/home/contact", "application/x-www-form-urlencoded", "Name=Neo&Age=30&Subscribe=on&Tags=a&Tags=b");

        Assert.Equal(302, response.StatusCode);
        string location = response.GetHeader("Location")!;
        Assert.Equal("/Home/Thanks?name=Neo&tags=2&subscribe=true", location);
        KazoWebResponseData thanks = await Send("GET", location);
        Assert.Equal("thanks Neo (2 tags, subscribe=True)", thanks.ReadAsString());
    }

    [Fact]
    public async Task Invalid_form_fills_model_state()
    {
        KazoWebResponseData response = await Send("POST", "/home/contact", "application/x-www-form-urlencoded", "Age=12");

        Assert.Equal(400, response.StatusCode);
        JsonElement errors = Json(response);
        Assert.True(errors.TryGetProperty("Name", out _));
        Assert.True(errors.TryGetProperty("Age", out _));
    }

    [Fact]
    public async Task Controller_on_action_executing_can_short_circuit()
    {
        Assert.Equal("stopped by controller", (await Send("GET", "/home/index?stop=1")).ReadAsString());
    }

    [Fact]
    public async Task Url_helper_generates_action_and_named_route_links()
    {
        Assert.Equal("/Home/Details/7?sort=asc", (await Send("GET", "/home/link")).ReadAsString());
        Assert.Equal("/api/Notes/3", (await Send("GET", "/home/notelink")).ReadAsString());
    }

    [Fact]
    public async Task File_result_sets_content_disposition()
    {
        KazoWebResponseData response = await Send("GET", "/home/download");
        Assert.True(response.StatusCode == 200, response.ReadAsString());

        Assert.Equal("hello", response.ReadAsString());
        Assert.Equal("text/plain", response.ContentType);
        Assert.Contains("filename=\"hello.txt\"", response.GetHeader("Content-Disposition"));
    }

    [Fact]
    public async Task Local_redirect_refuses_an_external_url()
    {
        KazoWebResponseData response = await Send("GET", "/home/away");

        Assert.Equal(500, response.StatusCode);
        Assert.Null(response.GetHeader("Location"));
    }

    [Fact]
    public async Task Session_survives_across_requests_with_its_cookie()
    {
        KazoWebResponseData first = await Send("GET", "/home/visits");
        string setCookie = first.GetHeader("Set-Cookie")!;
        string cookie = setCookie.Split(';')[0];
        KazoWebResponseData second = await Send("GET", "/home/visits", Header("Cookie", cookie));
        KazoWebResponseData stranger = await Send("GET", "/home/visits");

        Assert.Equal("visits=1", first.ReadAsString());
        Assert.StartsWith(".KazoSession=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("visits=2", second.ReadAsString());
        Assert.Equal("visits=1", stranger.ReadAsString());
    }

    [Fact]
    public async Task A_page_that_does_not_touch_the_session_sets_no_cookie()
    {
        Assert.Null((await Send("GET", "/")).GetHeader("Set-Cookie"));
    }

    [Fact]
    public async Task Raw_handler_reads_the_body()
    {
        KazoWebResponseData response = await Send("POST", "/echo", "text/plain", "ping");

        Assert.Equal("text/plain|ping", response.ReadAsString());
    }

    [Fact]
    public async Task Unknown_path_is_404()
    {
        Assert.Equal(404, (await Send("GET", "/nowhere/at/all/here")).StatusCode);
    }
}
