using Kazo.Os.AppSdk.Web.Routing;

namespace Kazo.Os.AppSdk.Web.Tests.Routing;

/// <summary>Endpoint selection: order, precedence, methods (ADR 0076 §3).</summary>
public sealed class EndpointRouterTests
{
    private static HackEndpoint Endpoint(string template, string name, int order = 0, params string[] methods) =>
        new(HackRoutePattern.Parse(template), methods, _ => Task.CompletedTask, name, order);

    [Fact]
    public void Literal_beats_constrained_beats_parameter_beats_catch_all()
    {
        HackEndpointRouter router = new([
            Endpoint("notes/{*rest}", "catchall"),
            Endpoint("notes/{id}", "param"),
            Endpoint("notes/{id:int}", "int"),
            Endpoint("notes/new", "literal")
        ]);

        Assert.Equal("literal", router.Match("GET", "/notes/new").Endpoint!.DisplayName);
        Assert.Equal("int", router.Match("GET", "/notes/3").Endpoint!.DisplayName);
        Assert.Equal("param", router.Match("GET", "/notes/abc").Endpoint!.DisplayName);
        Assert.Equal("catchall", router.Match("GET", "/notes/a/b").Endpoint!.DisplayName);
    }

    [Fact]
    public void Lower_order_wins_over_precedence()
    {
        HackEndpointRouter router = new([Endpoint("notes/new", "literal", order: 1), Endpoint("notes/{id}", "param")]);

        Assert.Equal("param", router.Match("GET", "/notes/new").Endpoint!.DisplayName);
    }

    [Fact]
    public void A_method_no_endpoint_accepts_gives_405_with_allow()
    {
        HackEndpointRouter router = new([Endpoint("notes", "list", 0, "GET"), Endpoint("notes", "create", 0, "POST")]);

        HackRouteMatch match = router.Match("DELETE", "/notes");

        Assert.Equal(HackRouteMatchKind.MethodNotAllowed, match.Kind);
        Assert.Equal(["GET", "HEAD", "OPTIONS", "POST"], match.AllowedMethods);
    }

    [Fact]
    public void Options_without_handler_and_head_fallback_to_get()
    {
        HackEndpointRouter router = new([Endpoint("notes", "list", 0, "GET")]);

        Assert.Equal(HackRouteMatchKind.Options, router.Match("OPTIONS", "/notes").Kind);
        Assert.Equal("list", router.Match("HEAD", "/notes").Endpoint!.DisplayName);
        Assert.Equal(HackRouteMatchKind.NotFound, router.Match("GET", "/other").Kind);
    }

    [Fact]
    public void Method_restricted_endpoint_beats_any_method_endpoint()
    {
        HackEndpointRouter router = new([Endpoint("notes", "any"), Endpoint("notes", "get", 0, "GET")]);

        Assert.Equal("get", router.Match("GET", "/notes").Endpoint!.DisplayName);
        Assert.Equal("any", router.Match("PUT", "/notes").Endpoint!.DisplayName);
    }

    [Fact]
    public void Two_equally_specific_endpoints_are_ambiguous()
    {
        HackEndpointRouter router = new([Endpoint("notes/{a}", "first", 0, "GET"), Endpoint("notes/{b}", "second", 0, "GET")]);

        Assert.Throws<HackAmbiguousRouteException>(() => router.Match("GET", "/notes/1"));
    }
}
