namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>
/// One controller action as the build sees it (ADR 0082): names, routes, parameters, filters, and
/// the generated code that creates the controller, binds the arguments and calls the method. The
/// route generator writes one per action into the site's <see cref="HackControllerRouteTable"/>;
/// nothing is discovered by reflection at run time.
/// </summary>
/// <remarks>This type is the contract between the generated code and the framework: keep it stable.</remarks>
public sealed class HackControllerActionDescriptor
{
    /// <summary>Gets the controller class.</summary>
    public required Type ControllerType { get; init; }

    /// <summary>Gets the controller name: the class name without <c>Controller</c> (<c>Notes</c>).</summary>
    public required string ControllerName { get; init; }

    /// <summary>Gets the action name: the method name without <c>Async</c>, or <c>[ActionName]</c>.</summary>
    public required string ActionName { get; init; }

    /// <summary>Gets the C# method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets whether the controller derives from <see cref="HackApiController"/> (problem details, automatic 400).</summary>
    public bool IsApiController { get; init; }

    /// <summary>Gets the attribute routes, already combined with the controller's prefix; empty for a conventionally routed action.</summary>
    public IReadOnlyList<HackAttributeRouteInfo> AttributeRoutes { get; init; } = [];

    /// <summary>Gets the methods a conventionally routed action accepts (<c>[HttpPost]</c> without template); empty accepts any.</summary>
    public IReadOnlyList<string> HttpMethods { get; init; } = [];

    /// <summary>Gets the parameters, in order.</summary>
    public IReadOnlyList<HackActionParameterDescriptor> Parameters { get; init; } = [];

    /// <summary>Gets the filter attributes of the controller, then of the action.</summary>
    public IReadOnlyList<IHackFilterMetadata> Filters { get; init; } = [];

    /// <summary>Gets the generated factory: <c>new NotesController(services.GetRequiredService&lt;NotesStore&gt;())</c>.</summary>
    public required Func<IServiceProvider, HackControllerBase> CreateController { get; init; }

    /// <summary>Gets the generated binder: one value per parameter, errors in the model state.</summary>
    public required Func<HackModelBinder, object?[]> BindArguments { get; init; }

    /// <summary>
    /// Gets the generated call: invokes the method with the arguments and returns what it returned
    /// (awaited for a task), <see cref="HackEmptyResult.Instance"/> for <c>void</c> or <see cref="Task"/>.
    /// </summary>
    public required Func<HackControllerBase, object?[], ValueTask<object?>> Invoke { get; init; }

    /// <summary>
    /// Gets the type the method declares it returns, task unwrapped (<c>IReadOnlyList&lt;Note&gt;</c> for
    /// <c>Task&lt;IReadOnlyList&lt;Note&gt;&gt;</c>), or <see langword="null"/> for <c>void</c>, <c>object</c>
    /// and action results. JSON falls back to it when the runtime type has no metadata, like ASP.NET's
    /// <c>ObjectResult.DeclaredType</c>.
    /// </summary>
    public Type? DeclaredResultType { get; init; }

    /// <summary>Gets <c>Namespace.NotesController.Get</c>.</summary>
    public string DisplayName => $"{ControllerType.FullName}.{MethodName}";

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}

/// <summary>An attribute route of an action, after the build combined it with the controller's <c>[Route]</c>.</summary>
/// <param name="Template">The full template, without leading <c>/</c>.</param>
/// <param name="HttpMethods">Accepted methods; empty accepts any.</param>
/// <param name="Order">Order (lower first).</param>
/// <param name="Name">Route name, or <see langword="null"/>.</param>
public sealed record HackAttributeRouteInfo(string Template, IReadOnlyList<string> HttpMethods, int Order = 0, string? Name = null);

/// <summary>Where an action parameter's value comes from.</summary>
public enum HackBindingSource
{
    /// <summary>A simple value from the form, the route or the query (ASP.NET's default order).</summary>
    Value,

    /// <summary>Route values only (<c>[FromRoute]</c>).</summary>
    Route,

    /// <summary>The query string only (<c>[FromQuery]</c>).</summary>
    Query,

    /// <summary>The form only (<c>[FromForm]</c>).</summary>
    Form,

    /// <summary>A request header (<c>[FromHeader]</c>).</summary>
    Header,

    /// <summary>The JSON body (<c>[FromBody]</c>, or a complex type of an API controller).</summary>
    Body,

    /// <summary>A complex type built from form, route and query fields (a view controller's model).</summary>
    Model,

    /// <summary>A service of the request scope (<c>[FromServices]</c>).</summary>
    Services,

    /// <summary>Supplied by the framework: <see cref="CancellationToken"/>, <c>HackHttpContext</c>, request, response.</summary>
    Special
}

/// <summary>An action parameter, for diagnostics and documentation of the routes.</summary>
/// <param name="Name">C# name (also the field or route value name, unless renamed).</param>
/// <param name="ParameterType">Declared type.</param>
/// <param name="Source">Where its value comes from.</param>
public sealed record HackActionParameterDescriptor(string Name, Type ParameterType, HackBindingSource Source);

/// <summary>The controller actions of a site, written by the route generator (ADR 0082).</summary>
/// <param name="Actions">Every action of every controller of the assembly.</param>
public sealed record HackControllerRouteTable(IReadOnlyList<HackControllerActionDescriptor> Actions)
{
    /// <summary>A table with no action (a site without controllers).</summary>
    public static HackControllerRouteTable Empty { get; } = new([]);
}
