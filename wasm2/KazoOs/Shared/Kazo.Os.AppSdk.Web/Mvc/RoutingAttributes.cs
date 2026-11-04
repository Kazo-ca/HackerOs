namespace Kazo.Os.AppSdk.Web.Mvc;

// ADR 0076 §3: the attributes keep ASP.NET Core MVC's names; ASP.NET is never referenced, so they do
// not clash. The route generator (ADR 0082) reads them at build time; nothing reads them by reflection.

/// <summary>
/// An attribute route on a controller (prefix of its actions) or an action:
/// <c>[Route("api/[controller]")]</c>. <c>[controller]</c> and <c>[action]</c> are replaced at build time;
/// a template starting with <c>/</c> or <c>~/</c> on an action ignores the controller prefix.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RouteAttribute(string template) : Attribute
{
    /// <summary>Gets the template.</summary>
    public string Template { get; } = template;

    /// <summary>Gets or sets the route name, for links (<c>Url.RouteUrl</c>); unique in the site.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the order: lower is tried first (0 by default).</summary>
    public int Order { get; set; }
}

/// <summary>Base of <see cref="HttpGetAttribute"/> and the others: an action that answers some methods, with an optional template.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public abstract class HttpMethodAttribute : Attribute
{
    /// <summary>Initializes the attribute.</summary>
    /// <param name="httpMethods">Methods, upper case.</param>
    /// <param name="template">Template, or <see langword="null"/> to keep the action's other routes.</param>
    protected HttpMethodAttribute(IReadOnlyList<string> httpMethods, string? template)
    {
        HttpMethods = httpMethods;
        Template = template;
    }

    /// <summary>Gets the methods.</summary>
    public IReadOnlyList<string> HttpMethods { get; }

    /// <summary>Gets the template, or <see langword="null"/>.</summary>
    public string? Template { get; }

    /// <summary>Gets or sets the route name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the order (0 by default).</summary>
    public int Order { get; set; }
}

/// <summary>An action for <c>GET</c> (and <c>HEAD</c>).</summary>
public sealed class HttpGetAttribute : HttpMethodAttribute
{
    /// <summary>Restricts the action to <c>GET</c>.</summary>
    public HttpGetAttribute() : base(["GET"], null) { }

    /// <summary>Routes <c>GET</c> requests on <paramref name="template"/> to the action.</summary>
    public HttpGetAttribute(string template) : base(["GET"], template) { }
}

/// <summary>An action for <c>POST</c>.</summary>
public sealed class HttpPostAttribute : HttpMethodAttribute
{
    /// <summary>Restricts the action to <c>POST</c>.</summary>
    public HttpPostAttribute() : base(["POST"], null) { }

    /// <summary>Routes <c>POST</c> requests on <paramref name="template"/> to the action.</summary>
    public HttpPostAttribute(string template) : base(["POST"], template) { }
}

/// <summary>An action for <c>PUT</c>.</summary>
public sealed class HttpPutAttribute : HttpMethodAttribute
{
    /// <summary>Restricts the action to <c>PUT</c>.</summary>
    public HttpPutAttribute() : base(["PUT"], null) { }

    /// <summary>Routes <c>PUT</c> requests on <paramref name="template"/> to the action.</summary>
    public HttpPutAttribute(string template) : base(["PUT"], template) { }
}

/// <summary>An action for <c>PATCH</c>.</summary>
public sealed class HttpPatchAttribute : HttpMethodAttribute
{
    /// <summary>Restricts the action to <c>PATCH</c>.</summary>
    public HttpPatchAttribute() : base(["PATCH"], null) { }

    /// <summary>Routes <c>PATCH</c> requests on <paramref name="template"/> to the action.</summary>
    public HttpPatchAttribute(string template) : base(["PATCH"], template) { }
}

/// <summary>An action for <c>DELETE</c>.</summary>
public sealed class HttpDeleteAttribute : HttpMethodAttribute
{
    /// <summary>Restricts the action to <c>DELETE</c>.</summary>
    public HttpDeleteAttribute() : base(["DELETE"], null) { }

    /// <summary>Routes <c>DELETE</c> requests on <paramref name="template"/> to the action.</summary>
    public HttpDeleteAttribute(string template) : base(["DELETE"], template) { }
}

/// <summary>An action for <c>HEAD</c>.</summary>
public sealed class HttpHeadAttribute : HttpMethodAttribute
{
    /// <summary>Restricts the action to <c>HEAD</c>.</summary>
    public HttpHeadAttribute() : base(["HEAD"], null) { }

    /// <summary>Routes <c>HEAD</c> requests on <paramref name="template"/> to the action.</summary>
    public HttpHeadAttribute(string template) : base(["HEAD"], template) { }
}

/// <summary>An action for <c>OPTIONS</c>.</summary>
public sealed class HttpOptionsAttribute : HttpMethodAttribute
{
    /// <summary>Restricts the action to <c>OPTIONS</c>.</summary>
    public HttpOptionsAttribute() : base(["OPTIONS"], null) { }

    /// <summary>Routes <c>OPTIONS</c> requests on <paramref name="template"/> to the action.</summary>
    public HttpOptionsAttribute(string template) : base(["OPTIONS"], template) { }
}

/// <summary>A public method of a controller that is not an action.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class NonActionAttribute : Attribute;

/// <summary>A class deriving from a controller base that is not a controller.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class NonControllerAttribute : Attribute;

/// <summary>Gives the action another name than its method (<c>[ActionName("Delete")]</c> on <c>DeleteConfirmed</c>).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ActionNameAttribute(string name) : Attribute
{
    /// <summary>Gets the action name.</summary>
    public string Name { get; } = name;
}

/// <summary>Binds a parameter from the route values.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromRouteAttribute : Attribute
{
    /// <summary>Gets or sets the route value name; the parameter name by default.</summary>
    public string? Name { get; set; }
}

/// <summary>Binds a parameter from the query string.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromQueryAttribute : Attribute
{
    /// <summary>Gets or sets the field name; the parameter name by default.</summary>
    public string? Name { get; set; }
}

/// <summary>Binds a parameter from the form (<c>application/x-www-form-urlencoded</c>).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromFormAttribute : Attribute
{
    /// <summary>Gets or sets the field name; the parameter name by default.</summary>
    public string? Name { get; set; }
}

/// <summary>Binds a parameter from the JSON body (<c>System.Text.Json</c>); one per action.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromBodyAttribute : Attribute;

/// <summary>Binds a parameter from a request header.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromHeaderAttribute : Attribute
{
    /// <summary>Gets or sets the header name; the parameter name by default.</summary>
    public string? Name { get; set; }
}

/// <summary>Takes a parameter from the request's services (DI).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromServicesAttribute : Attribute;
