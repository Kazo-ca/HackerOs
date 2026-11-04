using Kazo.Os.AppSdk.Web.Routing;

namespace Kazo.Os.AppSdk.Web.Tests.Routing;

/// <summary>Route templates: parsing, matching, constraints and link generation (ADR 0076 §3).</summary>
public sealed class RouteTemplateTests
{
    [Fact]
    public void Parses_literals_parameters_defaults_optionals_and_constraints()
    {
        KazoRouteTemplate template = KazoRouteTemplate.Parse("/api/{controller=Home}/{id:int:min(1)?}");

        Assert.Equal(3, template.Segments.Count);
        Assert.Equal("api", template.Segments[0].Literal);
        Assert.Equal("Home", template.Segments[1].Parameter!.DefaultValue);
        KazoRouteParameter id = template.Segments[2].Parameter!;
        Assert.True(id.IsOptional);
        Assert.Equal(["int", "min"], id.Constraints.Select(constraint => constraint.Name));
        Assert.Equal("1", id.Constraints[1].Argument);
    }

    [Theory]
    [InlineData("a/{id")]
    [InlineData("a/{}")]
    [InlineData("a/{id}{name}")]
    [InlineData("a/{id:unknown}")]
    [InlineData("{id?}/b")]
    [InlineData("{*rest}/b")]
    [InlineData("{id}/{id}")]
    [InlineData("a//b")]
    public void Rejects_invalid_templates(string text)
    {
        Assert.Throws<FormatException>(() => KazoRouteTemplate.Parse(text));
    }

    [Fact]
    public void Conflict_key_ignores_parameter_names_but_not_constraints()
    {
        Assert.Equal(
            KazoRouteTemplate.Parse("notes/{id:int}").GetConflictKey(),
            KazoRouteTemplate.Parse("NOTES/{key:int}").GetConflictKey());
        Assert.NotEqual(
            KazoRouteTemplate.Parse("notes/{id:int}").GetConflictKey(),
            KazoRouteTemplate.Parse("notes/{id}").GetConflictKey());
    }

    [Theory]
    [InlineData("/notes/42", true, "42")]
    [InlineData("/notes/abc", false, null)]
    [InlineData("/NOTES/7/", true, "7")]
    [InlineData("/notes", false, null)]
    [InlineData("/notes/1/extra", false, null)]
    public void Matches_a_constrained_parameter(string path, bool matches, string? id)
    {
        KazoRoutePattern pattern = KazoRoutePattern.Parse("notes/{id:int}");

        Assert.Equal(matches, pattern.TryMatch(KazoRoutePattern.SplitPath(path), out KazoRouteValueDictionary values));
        if (matches)
        {
            Assert.Equal(id, values.GetString("id"));
        }
    }

    [Fact]
    public void Fills_defaults_and_skips_absent_optionals()
    {
        KazoRoutePattern pattern = KazoRoutePattern.Parse("{controller=Home}/{action=Index}/{id?}");

        Assert.True(pattern.TryMatch(KazoRoutePattern.SplitPath("/"), out KazoRouteValueDictionary values));
        Assert.Equal("Home", values.GetString("controller"));
        Assert.Equal("Index", values.GetString("action"));
        Assert.False(values.ContainsKey("id"));
    }

    [Fact]
    public void Catch_all_takes_the_rest_of_the_path()
    {
        KazoRoutePattern pattern = KazoRoutePattern.Parse("files/{*path}");

        Assert.True(pattern.TryMatch(KazoRoutePattern.SplitPath("/files/a/b/c.txt"), out KazoRouteValueDictionary values));
        Assert.Equal("a/b/c.txt", values.GetString("path"));
        Assert.True(pattern.TryMatch(KazoRoutePattern.SplitPath("/files"), out _));
    }

    [Theory]
    [InlineData("{v:bool}", "true", true)]
    [InlineData("{v:bool}", "yes", false)]
    [InlineData("{v:guid}", "0f8fad5b-d9cb-469f-a165-70867728950e", true)]
    [InlineData("{v:alpha}", "abc", true)]
    [InlineData("{v:alpha}", "ab1", false)]
    [InlineData("{v:range(1,5)}", "5", true)]
    [InlineData("{v:range(1,5)}", "6", false)]
    [InlineData("{v:length(3)}", "abc", true)]
    [InlineData("{v:maxlength(2)}", "abc", false)]
    [InlineData("{v:regex(^[a-z]+-\\d+$)}", "ab-12", true)]
    [InlineData("{v:regex(^[a-z]+-\\d+$)}", "ab12", false)]
    public void Applies_constraints(string template, string value, bool matches)
    {
        Assert.Equal(matches, KazoRoutePattern.Parse(template).TryMatch([value], out _));
    }

    [Fact]
    public void Required_values_restrict_a_conventional_route_to_one_action()
    {
        KazoRoutePattern pattern = KazoRoutePattern.Parse(
            "{controller=Home}/{action=Index}/{id?}",
            new Dictionary<string, string> { ["controller"] = "Home", ["action"] = "Details" });

        Assert.True(pattern.TryMatch(KazoRoutePattern.SplitPath("/home/details/3"), out _));
        Assert.False(pattern.TryMatch(KazoRoutePattern.SplitPath("/home/index"), out _));
    }

    [Fact]
    public void Generates_paths_dropping_trailing_defaults_and_adding_extra_values_to_the_query()
    {
        KazoRoutePattern details = KazoRoutePattern.Parse(
            "{controller=Home}/{action=Index}/{id?}",
            new Dictionary<string, string> { ["controller"] = "Home", ["action"] = "Details" });
        KazoRoutePattern index = KazoRoutePattern.Parse(
            "{controller=Home}/{action=Index}/{id?}",
            new Dictionary<string, string> { ["controller"] = "Home", ["action"] = "Index" });

        Assert.True(details.TryGeneratePath(new Dictionary<string, object?> { ["id"] = 7, ["sort"] = "a b" }, out string path));
        Assert.Equal("/Home/Details/7?sort=a%20b", path);
        Assert.True(index.TryGeneratePath(new Dictionary<string, object?>(), out string root));
        Assert.Equal("/", root);
    }

    [Fact]
    public void Generation_fails_when_a_required_parameter_is_missing_or_violates_a_constraint()
    {
        KazoRoutePattern pattern = KazoRoutePattern.Parse("notes/{id:int}");

        Assert.False(pattern.TryGeneratePath(new Dictionary<string, object?>(), out _));
        Assert.False(pattern.TryGeneratePath(new Dictionary<string, object?> { ["id"] = "abc" }, out _));
        Assert.True(pattern.TryGeneratePath(new Dictionary<string, object?> { ["id"] = 5 }, out string path));
        Assert.Equal("/notes/5", path);
    }
}
