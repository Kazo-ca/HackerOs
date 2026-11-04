using System.Net;
using System.Text.RegularExpressions;
using Kazo.Os.AppSdk.Web.Generated;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Tests.Site;
using Kazo.Os.AppSdk.Web.Views;

namespace Kazo.Os.AppSdk.Web.Tests.Views;

/// <summary>
/// .cshtml views of <see cref="TestSite"/> compiled by the SDK's Razor compiler against HackRazorPage
/// (web/8, ADR 0086): table, @model, @inject, sections, _Layout, _ViewStart, helpers, mixing with .razor.
/// </summary>
public sealed partial class CshtmlViewTests : IAsyncLifetime
{
    private const string Site = "http://intranet.hack";
    private HackWebApplication _app = null!;

    public Task InitializeAsync()
    {
        _app = HackWebHost.CreateApplication(new TestSite(), HackWebGeneratedRoutes.Table, views: HackWebGeneratedRoutes.Views);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<(int Status, string Html, HackWebResponseData Response)> Get(string path, params KeyValuePair<string, string>[] headers)
    {
        HackWebResponseData response = await _app.ProcessAsync(HackWebRequestData.Create("GET", Site + path, headers));
        return (response.StatusCode, response.ReadAsString(), response);
    }

    private static string Flat(string html) => Regex.Replace(html, @"\s+", " ");

    [GeneratedRegex("""name="__RequestVerificationToken" type="hidden" value="([^"]+)" """)]
    private static partial Regex TokenField();

    [Fact]
    public void Generator_lists_cshtml_views_with_the_compiler_class_names()
    {
        HackViewTable views = HackWebGeneratedRoutes.Views;

        HackViewDescriptor index = views.Find("Cshtml/Index")!;
        Assert.Equal(HackViewKind.RazorPage, index.Kind);
        Assert.Equal("AspNetCoreGeneratedDocument.Views_Cshtml_Index", index.ViewType.FullName);
        Assert.Equal("/Views/Cshtml/Index.cshtml", index.ToString());
        Assert.Equal(HackViewKind.RazorPage, views.Find("_ViewStart")!.Kind);
        Assert.Equal(HackViewKind.Component, views.Find("Cshtml/FromRazor")!.Kind);
        Assert.DoesNotContain(views.Views, view => view.Path.EndsWith("_ViewImports", StringComparison.OrdinalIgnoreCase));
        Assert.True(typeof(HackRazorPage<NoteStore>).IsAssignableFrom(index.ViewType));
    }

    [Fact]
    public async Task View_runs_view_starts_layout_model_inject_and_section()
    {
        (int status, string html, HackWebResponseData response) = await Get("/Cshtml");
        string flat = Flat(html);

        Assert.True(status == 200, html);
        Assert.StartsWith("text/html", response.ContentType);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<title>Cshtml notes</title>", html);
        Assert.Contains("""data-starts="root&gt;cshtml" """.TrimEnd(), html);
        Assert.Contains("<h1>2 notes &amp; &lt;tags&gt;</h1>", html);
        Assert.Contains("""<a href="/Cshtml" class="brand">Home</a>""", html);
        Assert.Contains("""<link rel="stylesheet" href="/css/site.css" />""", html);
        Assert.Contains("""<a href="/Cshtml/Details/1">First</a>""", html);
        Assert.Contains("""<p class="counter">ok</p>""", html);
        Assert.Contains("<footer>/Cshtml</footer>", html);
        Assert.Contains("<p>second is done</p>", html);

        // Attributes as in ASP.NET: a null part is dropped with its space, a lone false or null drops the attribute, a lone true writes its name.
        Assert.Contains("""<li class="note">""", html);
        Assert.Contains("""<li class="note done">""", html);
        Assert.Contains("""<input type="checkbox" />""", html);
        Assert.Contains("""<input type="checkbox" checked="checked" disabled="disabled" />""", html);

        // Partials of both kinds, a template, raw HTML, a component and JSON.
        Assert.Contains("""<li class="card" data-id="1">First</li>""", html);
        Assert.Contains("""<li class="card done" data-id="2">Second</li>""", html);
        Assert.Contains("""<ul class="razor-cards"><li class="note"><a href="/Cshtml/Details/1">First</a></li></ul>""", flat);
        Assert.Contains("""<b class="badge">new</b>""", html);
        Assert.Contains("""<hr class="raw" />""", html);
        Assert.Contains("""<p class="component"><a href="/Cshtml/Edit">Edit</a></p>""", html);
        Assert.Contains("""<p class="json">{"id":1,"title":"First","done":false}</p>""", html);

        // The section is written where the layout asks for it, after the footer.
        Assert.True(html.IndexOf("<footer>", StringComparison.Ordinal) < html.IndexOf("""<script src="/js/notes.js">""", StringComparison.Ordinal), html);
    }

    [Fact]
    public async Task Nested_layouts_and_a_redefined_section_render_from_the_inside_out()
    {
        (int status, string html, _) = await Get("/Cshtml/Details/2");
        string flat = Flat(html);

        Assert.True(status == 200, html);
        Assert.Contains("<title>Second</title>", html);
        Assert.Contains("""<section class="inner"><h1>Second</h1>""", flat);
        Assert.Contains("""<script src="/js/details.js"></script><script src="/js/inner.js"></script>""", flat);
    }

    [Fact]
    public async Task Razor_and_cshtml_mix_in_both_directions()
    {
        (int status, string mixed, _) = await Get("/Cshtml/WithRazorLayout");
        Assert.True(status == 200, mixed);
        Assert.Contains("<title>Mixed</title>", mixed);
        Assert.Contains("""<p class="mixed">cshtml in razor layout</p>""", mixed);
        Assert.Contains("""<script src="/js/mixed.js"></script>""", mixed);
        Assert.Contains("""<a href="/Pages" class="brand">Home</a>""", mixed);

        (status, string fromRazor, _) = await Get("/Cshtml/FromRazor");
        Assert.True(status == 200, fromRazor);
        Assert.Contains("<title>Cshtml site</title>", fromRazor);
        Assert.Contains("""<p class="from-razor">razor in cshtml layout</p>""", fromRazor);
        Assert.Contains("""<script src="/js/from-razor.js"></script>""", fromRazor);
        Assert.Contains("""<li class="card" data-id="9">Nine</li>""", fromRazor);
    }

    [Fact]
    public async Task Layout_null_partial_view_and_absolute_path_render_without_layout()
    {
        (_, string bare, _) = await Get("/Cshtml/NoLayout");
        Assert.Equal("<p>bare</p>", bare.Trim());

        (_, string card, _) = await Get("/Cshtml/Card/2");
        Assert.Equal("""<li class="card done" data-id="2">Second</li>""", card.Trim());

        (_, string absolute, _) = await Get("/Cshtml/Absolute");
        Assert.Equal("<p>bare</p>", absolute.Trim());
    }

    [Fact]
    public async Task Form_helpers_bind_the_model_and_show_errors_after_a_failed_post()
    {
        (int status, string form, HackWebResponseData response) = await Get("/Cshtml/Edit");
        Assert.True(status == 200, form);
        Assert.Contains("""<form action="/Cshtml/Edit" method="post" class="edit">""", form);
        Assert.Contains("""<label for="Name">Name</label>""", form);
        Assert.Contains("""<input type="text" name="Name" id="Name" value="Ada" />""", form);
        Assert.Contains("""<input type="number" name="Age" id="Age" value="36" />""", form);
        Assert.Contains("""<input type="checkbox" name="Subscribe" id="Subscribe" value="true" /><input name="Subscribe" type="hidden" value="false" />""", form);
        Assert.Contains("""<span class="field-validation-valid" data-valmsg-for="Name"></span>""", form);
        Assert.Contains("</button>", form);
        Assert.True(form.IndexOf("__RequestVerificationToken", StringComparison.Ordinal) < form.IndexOf("</form>", StringComparison.Ordinal));

        string token = TokenField().Match(form).Groups[1].Value;
        string cookie = response.Headers.First(header => header.Key == "Set-Cookie" && header.Value.StartsWith(".HackAntiforgery=", StringComparison.Ordinal)).Value.Split(';')[0];
        HackWebResponseData posted = await _app.ProcessAsync(HackWebRequestData.Create(
            "POST", Site + "/Cshtml/Edit", "application/x-www-form-urlencoded",
            $"Name=&Age=7&__RequestVerificationToken={WebUtility.UrlEncode(token)}",
            [new("Cookie", cookie)]));
        string errors = posted.ReadAsString();

        Assert.True(posted.StatusCode == 200, errors);
        Assert.Contains("""<div class="validation-summary-errors"><ul><li>Please fix the errors below.</li></ul></div>""", errors);
        Assert.Contains("""<span class="field-validation-error" data-valmsg-for="Name">""", errors);
        Assert.Contains("""name="Age" id="Age" value="7" class="input-validation-error" """.TrimEnd(), errors);

        HackWebResponseData valid = await _app.ProcessAsync(HackWebRequestData.Create(
            "POST", Site + "/Cshtml/Edit", "application/x-www-form-urlencoded",
            $"Name=Grace&Age=40&__RequestVerificationToken={WebUtility.UrlEncode(token)}",
            [new("Cookie", cookie)]));
        Assert.Equal(302, valid.StatusCode);
    }

    [Fact]
    public async Task Missing_body_missing_section_orphan_section_and_wrong_model_are_errors()
    {
        (int status, string noBody, _) = await Get("/Cshtml/NoBody");
        Assert.Equal(500, status);
        Assert.Contains("RenderBody has not been called for the page at '/Views/Shared/_CshtmlNoBody.cshtml'", WebUtility.HtmlDecode(noBody));

        string missing = WebUtility.HtmlDecode((await Get("/Cshtml/MissingSection")).Html);
        Assert.Contains("cannot find the section 'Sidebar'", missing);

        string orphan = WebUtility.HtmlDecode((await Get("/Cshtml/Orphan")).Html);
        Assert.Contains("'Orphan'", orphan);

        string wrongModel = WebUtility.HtmlDecode((await Get("/Cshtml/WrongModel")).Html);
        Assert.Contains("requires a model of type", wrongModel);
        Assert.Contains("Site.Note", wrongModel);
    }

    [Fact]
    public async Task Missing_view_lists_razor_and_cshtml_locations()
    {
        string html = WebUtility.HtmlDecode((await Get("/Pages/Missing")).Html);

        Assert.Contains("/Views/Pages/Nowhere.razor", html);
        Assert.Contains("/Views/Pages/Nowhere.cshtml", html);
        Assert.Contains("/Views/Shared/Nowhere.cshtml", html);
    }
}
