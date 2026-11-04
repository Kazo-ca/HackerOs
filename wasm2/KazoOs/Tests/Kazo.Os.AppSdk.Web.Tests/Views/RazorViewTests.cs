using System.Text.RegularExpressions;
using Kazo.Os.AppSdk.Web.Generated;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Tests.Site;
using Kazo.Os.AppSdk.Web.Views;

namespace Kazo.Os.AppSdk.Web.Tests.Views;

/// <summary>
/// Razor views of <see cref="TestSite"/> rendered through the pipeline (web/7, ADR 0083): lookup,
/// layouts, sections, partials, helpers, anti-forgery, static files, errors.
/// </summary>
public sealed partial class RazorViewTests : IAsyncLifetime
{
    private const string Site = "http://intranet.hack";
    private HackWebApplication _app = null!;

    public Task InitializeAsync()
    {
        _app = HackWebHost.CreateApplication(new TestSite(), HackWebGeneratedRoutes.Table, views: HackWebGeneratedRoutes.Views);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<HackWebResponseData> Get(string path, params KeyValuePair<string, string>[] headers) =>
        _app.ProcessAsync(HackWebRequestData.Create("GET", Site + path, headers));

    private Task<HackWebResponseData> PostForm(string path, string body, params KeyValuePair<string, string>[] headers) =>
        _app.ProcessAsync(HackWebRequestData.Create("POST", Site + path, "application/x-www-form-urlencoded", body, headers));

    private static KeyValuePair<string, string> Header(string name, string value) => new(name, value);

    [GeneratedRegex("""name="__RequestVerificationToken" type="hidden" value="([^"]+)" """)]
    private static partial Regex TokenField();

    [Fact]
    public void Generator_lists_the_views_under_Views()
    {
        string[] paths = [.. HackWebGeneratedRoutes.Views.Views.Select(view => view.Path).Order()];

        Assert.Contains("Pages/Index", paths);
        Assert.Contains("Pages/_NoteCard", paths);
        Assert.Contains("Shared/_Layout", paths);
        Assert.DoesNotContain("_Imports", paths);
        Assert.Equal(typeof(Tests.Views.Pages.Index), HackWebGeneratedRoutes.Views.Find("pages/index")!.ViewType);
    }

    [Fact]
    public async Task View_renders_model_layout_title_partials_and_section()
    {
        HackWebResponseData response = await Get("/Pages");
        string html = response.ReadAsString();

        Assert.True(response.StatusCode == 200, html);
        Assert.StartsWith("text/html", response.ContentType);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<title>Notes</title>", html);
        Assert.Contains("<h1>2 notes</h1>", html);
        Assert.Contains("""<li class="note"><a href="/Pages/Details/1">First</a></li>""", html.Replace("\n", string.Empty).Replace("\r", string.Empty).Replace("    ", string.Empty));
        Assert.Contains("""class="note done""", html);
        Assert.Contains("""<a href="/Pages" class="brand">Home</a>""", html);
        Assert.Contains("""<link rel="stylesheet" href="/css/site.css" />""", html);
        Assert.Contains("<footer>/Pages</footer>", html);
        // The section defined by the view is written where the layout asks for it, after the footer.
        Assert.True(html.IndexOf("<footer>", StringComparison.Ordinal) < html.IndexOf("""<script src="/js/notes.js">""", StringComparison.Ordinal), html);
    }

    [Fact]
    public async Task Nested_layouts_render_from_the_inside_out()
    {
        string html = (await Get("/Pages/Details/2")).ReadAsString();

        Assert.Contains("<title>Second</title>", html);
        Assert.Contains("""<section class="inner"><h1>Second</h1>""", html.Replace("\n", string.Empty).Replace("\r", string.Empty).Replace("    ", string.Empty));
        Assert.Contains("<p>Done: True</p>", html);
    }

    [Fact]
    public async Task Form_helper_writes_action_antiforgery_and_validation_markup()
    {
        HackWebResponseData response = await Get("/Pages/Edit");
        string html = response.ReadAsString();

        Assert.Contains("""<form method="post" action="/Pages/Edit" class="edit">""", html);
        Assert.Matches(TokenField(), html);
        Assert.Contains("""<span class="field-validation-valid" data-valmsg-for="Name"></span>""", html);
        Assert.Contains("""value="Ada" """, html);
        Assert.DoesNotContain("validation-summary-errors", html);
        Assert.Contains("""<form method="get" action="/search">""", html);
        Assert.Single(TokenField().Matches(html));
        string? cookie = response.Headers.FirstOrDefault(header => header.Key == "Set-Cookie" && header.Value.StartsWith(".HackAntiforgery=", StringComparison.Ordinal)).Value;
        Assert.NotNull(cookie);
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);
    }

    [Fact]
    public async Task Post_with_valid_token_is_accepted_and_invalid_model_shows_errors()
    {
        (string cookie, string token) = await GetTokens();

        HackWebResponseData ok = await PostForm("/Pages/Edit", $"Name=Ada&Age=40&__RequestVerificationToken={Uri.EscapeDataString(token)}", Header("Cookie", cookie));
        Assert.Equal(302, ok.StatusCode);
        Assert.Equal("/Pages", ok.GetHeader("Location"));

        HackWebResponseData invalid = await PostForm("/Pages/Edit", $"Name=&Age=7&__RequestVerificationToken={Uri.EscapeDataString(token)}", Header("Cookie", cookie));
        string html = invalid.ReadAsString();
        Assert.Equal(200, invalid.StatusCode);
        Assert.Contains("""<div class="validation-summary-errors"><ul><li>Please fix the errors below.</li></ul></div>""", html);
        Assert.Contains("""<span class="field-validation-error" data-valmsg-for="Age">""", html);
        Assert.Contains("""value="7" """, html);
    }

    [Fact]
    public async Task Token_is_also_accepted_in_the_header()
    {
        (string cookie, string token) = await GetTokens();

        HackWebResponseData response = await PostForm("/Pages/Edit", "Name=Ada&Age=40", Header("Cookie", cookie), Header("RequestVerificationToken", token));

        Assert.Equal(302, response.StatusCode);
    }

    [Fact]
    public async Task Post_without_or_with_a_foreign_token_is_refused()
    {
        HackWebResponseData none = await PostForm("/Pages/Edit", "Name=Ada&Age=40");
        Assert.Equal(400, none.StatusCode);
        Assert.Contains(".HackAntiforgery", none.ReadAsString());

        (string cookie, _) = await GetTokens();
        HackWebResponseData noField = await PostForm("/Pages/Edit", "Name=Ada&Age=40", Header("Cookie", cookie));
        Assert.Equal(400, noField.StatusCode);
        Assert.Contains("__RequestVerificationToken", noField.ReadAsString());

        (_, string otherToken) = await GetTokens();
        HackWebResponseData foreign = await PostForm("/Pages/Edit", $"Name=Ada&Age=40&__RequestVerificationToken={Uri.EscapeDataString(otherToken)}", Header("Cookie", cookie));
        Assert.Equal(400, foreign.StatusCode);
        Assert.Contains("could not be validated", foreign.ReadAsString());
    }

    [Fact]
    public async Task Ignore_attribute_skips_the_check()
    {
        HackWebResponseData response = await PostForm("/Pages/Open", string.Empty);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("open", response.ReadAsString());
    }

    [Fact]
    public async Task Untyped_view_named_view_and_status_code()
    {
        Assert.Equal("<p>plain null</p>", (await Get("/Pages/Plain")).ReadAsString().Trim());
        Assert.Equal("<p>plain model</p>", (await Get("/Pages/PlainModel")).ReadAsString().Trim());

        HackWebResponseData teapot = await Get("/Pages/Status");
        Assert.Equal(418, teapot.StatusCode);
        Assert.Contains("plain", teapot.ReadAsString());
    }

    [Fact]
    public async Task Partial_view_has_no_layout_and_typed_view_needs_no_lookup()
    {
        string partial = (await Get("/Pages/Partial/1")).ReadAsString();
        Assert.StartsWith("<li", partial.Trim());
        Assert.DoesNotContain("<html", partial);

        Assert.Equal("<p>loose component</p>", (await Get("/Pages/Typed")).ReadAsString().Trim());
    }

    [Fact]
    public async Task Shared_folder_and_absolute_paths_are_searched()
    {
        Assert.Contains("Something went wrong", (await Get("/Pages/Shared")).ReadAsString());
        Assert.Contains("Something went wrong", (await Get("/Pages/Absolute")).ReadAsString());
    }

    [Fact]
    public async Task Services_are_injected_in_the_view()
    {
        string html = (await Get("/Pages/Injected")).ReadAsString();

        Assert.Contains("counter ok 0HN", html);
    }

    [Fact]
    public async Task Missing_view_lists_the_searched_locations()
    {
        HackWebResponseData response = await Get("/Pages/Missing");
        string html = response.ReadAsString();

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("The view 'Nowhere' was not found", html);
        Assert.Contains("/Views/Pages/Nowhere.razor", html);
        Assert.Contains("/Views/Shared/Nowhere.razor", html);
    }

    [Fact]
    public async Task Wrong_model_orphan_section_and_layout_without_body_are_errors()
    {
        string wrongModel = (await Get("/Pages/WrongModel")).ReadAsString();
        Assert.Contains("requires a model of type", wrongModel);
        Assert.Contains("Site.Note", wrongModel);

        string orphan = (await Get("/Pages/Broken")).ReadAsString();
        Assert.Contains("'Orphan'", orphan);

        string noBody = (await Get("/Pages/NoBody")).ReadAsString();
        Assert.Contains("did not render the body", noBody);
    }

    [Fact]
    public async Task Static_files_are_served_from_the_embedded_wwwroot()
    {
        HackWebResponseData css = await Get("/css/site.css");
        Assert.Equal(200, css.StatusCode);
        Assert.Equal("text/css; charset=utf-8", css.ContentType);
        Assert.Contains("color: #0f0", css.ReadAsString());
        Assert.Equal("public, max-age=3600", css.GetHeader("Cache-Control"));
        string etag = css.GetHeader("ETag")!;

        HackWebResponseData cached = await Get("/css/site.css", Header("If-None-Match", etag));
        Assert.Equal(304, cached.StatusCode);
        Assert.Empty(cached.Body);

        HackWebResponseData png = await Get("/logo.png");
        Assert.Equal("image/png", png.ContentType);
        Assert.Equal(8, png.Body.Length);

        HackWebResponseData index = await Get("/docs/");
        Assert.Equal(200, index.StatusCode);
        Assert.Contains("<h1>docs</h1>", index.ReadAsString());

        HackWebResponseData head = await _app.ProcessAsync(HackWebRequestData.Create("HEAD", Site + "/css/site.css"));
        Assert.Equal(200, head.StatusCode);
        Assert.Empty(head.Body);

        HackWebResponseData post = await PostForm("/css/site.css", string.Empty);
        Assert.Equal(405, post.StatusCode);
    }

    [Fact]
    public async Task Paths_outside_wwwroot_fall_through_to_the_controllers()
    {
        Assert.Equal(404, (await Get("/css/missing.css")).StatusCode);
        Assert.Equal(404, (await Get("/../TestSite.cs")).StatusCode);
        Assert.Equal("home", (await Get("/")).ReadAsString());
    }

    [Fact]
    public void Model_type_check_accepts_null_for_reference_types_only()
    {
        HackViewEngine.EnsureModelType(typeof(Tests.Views.Pages.Details), new Note());
        HackViewEngine.EnsureModelType(typeof(Tests.Views.Pages.Details), null);
        HackViewEngine.EnsureModelType(typeof(Tests.Views.Pages.Loose), "anything");

        Assert.Throws<InvalidOperationException>(() => HackViewEngine.EnsureModelType(typeof(Tests.Views.Pages.Details), 42));
    }

    private async Task<(string Cookie, string Token)> GetTokens()
    {
        HackWebResponseData page = await Get("/Pages/Edit");
        string setCookie = page.Headers.First(header => header.Key == "Set-Cookie" && header.Value.StartsWith(".HackAntiforgery=", StringComparison.Ordinal)).Value;
        string cookie = setCookie[..setCookie.IndexOf(';')];
        string token = TokenField().Match(page.ReadAsString()).Groups[1].Value;
        return (cookie, token);
    }
}
