using Kazo.Os.AppSdk.Web.Blazor.Sites;
using Kazo.Os.AppSdk.Web.Routing;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Web.Blazor.Tests;

/// <summary>The site router's route table (ADR 0084 §5): Blazor templates, precedence, parameter conversion.</summary>
public sealed class KazoSiteRouterTests
{
    private static KazoSiteRouter CreateRouter() => new() { AppAssembly = typeof(KazoSiteRouterTests).Assembly };

    [Theory]
    [InlineData("/", typeof(HomePage))]
    [InlineData("", typeof(HomePage))]
    [InlineData("/notes", typeof(NotesPage))]
    [InlineData("/notes/", typeof(NotesPage))]
    [InlineData("/notes/5", typeof(NotePage))]
    [InlineData("/notes/new", typeof(NewNotePage))]
    [InlineData("/files/a/b/c.txt", typeof(FilesPage))]
    [InlineData("/Notes/7?tab=all#top", typeof(NotePage))]
    public void Finds_the_page_of_a_path(string path, Type expected)
    {
        KazoSiteRouter router = CreateRouter();

        Assert.True(router.TryMatch(path, out Type? page, out _));
        Assert.Equal(expected, page);
    }

    [Theory]
    [InlineData("/missing")]
    [InlineData("/notes/5/extra")]
    [InlineData("/notes/abc")]
    public void Reports_no_page_for_an_unknown_path(string path)
    {
        KazoSiteRouter router = CreateRouter();

        Assert.False(router.TryMatch(path, out _, out _));
    }

    [Fact]
    public void Route_values_come_from_the_template()
    {
        KazoSiteRouter router = CreateRouter();

        Assert.True(router.TryMatch("/files/docs/readme.md", out _, out KazoRouteValueDictionary values));
        Assert.Equal("docs/readme.md", values["path"]);
        Assert.True(router.TryMatch("/notes/42", out _, out values));
        Assert.Equal("42", values["id"]);
    }

    [Fact]
    public void Lists_the_routes_of_the_assembly_most_specific_first()
    {
        KazoSiteRouter router = CreateRouter();
        router.TryMatch("/", out _, out _);

        IReadOnlyList<(string Template, Type Page)> routes = router.Routes;
        Assert.Contains(routes, route => route.Template == "/notes/new" && route.Page == typeof(NewNotePage));
        Assert.True(
            routes.ToList().FindIndex(route => route.Page == typeof(NewNotePage)) < routes.ToList().FindIndex(route => route.Page == typeof(NotePage)),
            "The literal segment must be tried before the parameter.");
    }

    [Theory]
    [InlineData("42", typeof(int), 42)]
    [InlineData("42", typeof(int?), 42)]
    [InlineData("true", typeof(bool), true)]
    [InlineData("3.5", typeof(double), 3.5)]
    [InlineData("x", typeof(string), "x")]
    public void Converts_route_values_to_the_parameter_type(string text, Type type, object expected) =>
        Assert.Equal(expected, KazoSiteRouter.ConvertRouteValue(text, type, "p"));

    [Fact]
    public void Converts_guids_and_dates_invariantly()
    {
        Guid guid = Guid.NewGuid();
        Assert.Equal(guid, KazoSiteRouter.ConvertRouteValue(guid.ToString(), typeof(Guid), "id"));
        Assert.Equal(new DateOnly(2026, 10, 16), KazoSiteRouter.ConvertRouteValue("2026-10-16", typeof(DateOnly), "day"));
    }

    [Fact]
    public void Rejects_a_value_the_parameter_cannot_take()
    {
        Assert.Throws<InvalidOperationException>(() => KazoSiteRouter.ConvertRouteValue("abc", typeof(int), "id"));
        Assert.Throws<InvalidOperationException>(() => KazoSiteRouter.ConvertRouteValue("abc", typeof(Uri), "id"));
    }

    [Route("/")]
    public sealed class HomePage : ComponentBase;

    [Route("/notes")]
    public sealed class NotesPage : ComponentBase;

    [Route("/notes/{id:int}")]
    public sealed class NotePage : ComponentBase
    {
        [Parameter]
        public int Id { get; set; }
    }

    [Route("/notes/new")]
    public sealed class NewNotePage : ComponentBase;

    [Route("/files/{*path}")]
    public sealed class FilesPage : ComponentBase
    {
        [Parameter]
        public string? Path { get; set; }
    }
}
