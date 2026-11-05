using System.Diagnostics.CodeAnalysis;
using Kazo.Os.AppSdk.Web.Antiforgery;
using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.AppSdk.Web.Routing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.AppSdk.Web.Views;

/// <summary>Base of the helpers of a view: components that need the page's <see cref="KazoViewContext"/>.</summary>
public abstract class KazoViewHelper : ComponentBase
{
    private KazoViewContext? _viewContext;

    /// <summary>Gets or sets the context of the page (cascaded by the view engine).</summary>
    [CascadingParameter]
    public KazoViewContext ViewContext
    {
        get => _viewContext ?? throw new InvalidOperationException(
            $"<{GetType().Name}> works only inside an ASP.Kazo view rendered by View() or PartialView().");
        set => _viewContext = value;
    }

    /// <summary>Gets the URL helper.</summary>
    protected IKazoUrlHelper Url => ViewContext.Url;

    /// <summary>Gets the HTML attributes written as they were given (<c>class</c>, <c>id</c>, <c>data-*</c>...).</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Resolves the URL of an action, a named route or a literal path, as the link and form helpers do.</summary>
    /// <param name="href">A literal URL (<c>~/</c> is resolved); wins when set.</param>
    /// <param name="route">A route name.</param>
    /// <param name="action">An action name (the current one when <see langword="null"/>).</param>
    /// <param name="controller">A controller name (the current one when <see langword="null"/>).</param>
    /// <param name="routeValues">Route values.</param>
    /// <returns>The URL.</returns>
    /// <exception cref="InvalidOperationException">No route produces the URL.</exception>
    protected string ResolveUrl(string? href, string? route, string? action, string? controller, IReadOnlyDictionary<string, object?>? routeValues)
    {
        if (href is not null)
        {
            return Url.Content(href);
        }

        KazoRouteValueDictionary values = routeValues is null ? [] : new KazoRouteValueDictionary(routeValues);
        string? url = route is not null ? Url.RouteUrl(route, values) : Url.Action(action, controller, values);
        return url ?? throw new InvalidOperationException(
            route is not null
                ? $"No route named '{route}' produces a URL with the given values."
                : $"No route produces a URL for the action '{action ?? ViewContext.ActionDescriptor?.ActionName}' of controller '{controller ?? ViewContext.ActionDescriptor?.ControllerName}'.");
    }
}

/// <summary>
/// A link to an action (<c>&lt;KazoLink Action="Details" Controller="Notes" RouteValues="@(new { id = note.Id })"&gt;...&lt;/KazoLink&gt;</c>),
/// a named route (<c>Route="GetNote"</c>) or a path (<c>Href="~/about"</c>): an <c>&lt;a&gt;</c> whose
/// <c>href</c> the route table produces (ASP.NET's <c>&lt;a asp-action&gt;</c>).
/// </summary>
public sealed class KazoLink : KazoViewHelper
{
    /// <summary>Gets or sets the action name.</summary>
    [Parameter]
    public string? Action { get; set; }

    /// <summary>Gets or sets the controller name.</summary>
    [Parameter]
    public string? Controller { get; set; }

    /// <summary>Gets or sets the route name (instead of an action).</summary>
    [Parameter]
    public string? Route { get; set; }

    /// <summary>Gets or sets a literal URL (instead of an action); <c>~/</c> is resolved.</summary>
    [Parameter]
    public string? Href { get; set; }

    /// <summary>Gets or sets the route values (<c>new { id = 3 }</c> through <see cref="KazoRoute.Values{TValues}"/>, or a dictionary).</summary>
    [Parameter]
    public IReadOnlyDictionary<string, object?>? RouteValues { get; set; }

    /// <summary>Gets or sets the link's content.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "a");
        builder.AddAttribute(1, "href", ResolveUrl(Href, Route, Action, Controller, RouteValues));
        builder.AddMultipleAttributes(2, AdditionalAttributes);
        builder.AddContent(3, ChildContent);
        builder.CloseElement();
    }
}

/// <summary>
/// A form posted to an action (ASP.NET's <c>&lt;form asp-action&gt;</c>): <c>method="post"</c> by default,
/// <c>action</c> from the route table, and the anti-forgery hidden field unless <see cref="Antiforgery"/>
/// is <see langword="false"/> or the method is <c>GET</c>.
/// </summary>
public sealed class KazoForm : KazoViewHelper
{
    /// <summary>Gets or sets the action name.</summary>
    [Parameter]
    public string? Action { get; set; }

    /// <summary>Gets or sets the controller name.</summary>
    [Parameter]
    public string? Controller { get; set; }

    /// <summary>Gets or sets the route name (instead of an action).</summary>
    [Parameter]
    public string? Route { get; set; }

    /// <summary>Gets or sets a literal URL (instead of an action).</summary>
    [Parameter]
    public string? Href { get; set; }

    /// <summary>Gets or sets the route values.</summary>
    [Parameter]
    public IReadOnlyDictionary<string, object?>? RouteValues { get; set; }

    /// <summary>Gets or sets the HTTP method (<c>post</c> by default).</summary>
    [Parameter]
    public string Method { get; set; } = "post";

    /// <summary>Gets or sets whether the anti-forgery field is written (yes by default, for a non-<c>GET</c> form).</summary>
    [Parameter]
    public bool Antiforgery { get; set; } = true;

    /// <summary>Gets or sets the form's content.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "form");
        builder.AddAttribute(1, "method", Method);
        builder.AddAttribute(2, "action", ResolveUrl(Href, Route, Action, Controller, RouteValues));
        builder.AddMultipleAttributes(3, AdditionalAttributes);
        builder.AddContent(4, ChildContent);
        if (Antiforgery && !string.Equals(Method, "get", StringComparison.OrdinalIgnoreCase))
        {
            builder.OpenComponent<KazoAntiforgeryToken>(5);
            builder.CloseComponent();
        }

        builder.CloseElement();
    }
}

/// <summary>The hidden anti-forgery field of a form (<see cref="KazoForm"/> writes it itself; use it in a hand-written <c>&lt;form&gt;</c>).</summary>
public sealed class KazoAntiforgeryToken : KazoViewHelper
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        KazoAntiforgeryTokenSet tokens = ViewContext.GetAntiforgeryTokens();
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "name", tokens.FormFieldName);
        builder.AddAttribute(2, "type", "hidden");
        builder.AddAttribute(3, "value", tokens.RequestToken);
        builder.CloseElement();
    }
}

/// <summary>
/// The first error of a field of <c>ModelState</c>, in a <c>&lt;span&gt;</c> (ASP.NET's
/// <c>&lt;span asp-validation-for&gt;</c>): class <c>field-validation-error</c> when there is one,
/// <c>field-validation-valid</c> otherwise, so a stylesheet can style both.
/// </summary>
public sealed class KazoValidationMessage : KazoViewHelper
{
    /// <summary>Gets or sets the field key (<c>Title</c>, <c>note.Title</c>).</summary>
    [Parameter]
    [EditorRequired]
    public string For { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        string? error = ViewContext.ModelState[For]?.Errors.FirstOrDefault();
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", error is null ? "field-validation-valid" : "field-validation-error");
        builder.AddAttribute(2, "data-valmsg-for", For);
        builder.AddMultipleAttributes(3, AdditionalAttributes);
        builder.AddContent(4, error);
        builder.CloseElement();
    }
}

/// <summary>
/// Every error of <c>ModelState</c> as a list (ASP.NET's <c>&lt;div asp-validation-summary&gt;</c>):
/// nothing is written when the model is valid.
/// </summary>
public sealed class KazoValidationSummary : KazoViewHelper
{
    /// <summary>Gets or sets whether only the model-level errors (empty key) are listed.</summary>
    [Parameter]
    public bool ModelOnly { get; set; }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        KazoModelStateDictionary modelState = ViewContext.ModelState;
        string[] errors = [.. modelState.Keys
            .Where(key => !ModelOnly || key.Length == 0)
            .SelectMany(key => modelState[key]!.Errors)];
        if (errors.Length == 0)
        {
            return;
        }

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "validation-summary-errors");
        builder.AddMultipleAttributes(2, AdditionalAttributes);
        builder.OpenElement(3, "ul");
        foreach (string error in errors)
        {
            builder.OpenElement(4, "li");
            builder.AddContent(5, error);
            builder.CloseElement();
        }

        builder.CloseElement();
        builder.CloseElement();
    }
}

/// <summary>
/// Defines a section the layout renders elsewhere (<c>@section Scripts { ... }</c> of a <c>.cshtml</c>):
/// <c>&lt;KazoSection Name="Scripts"&gt;&lt;script src="~/js/notes.js"&gt;&lt;/script&gt;&lt;/KazoSection&gt;</c>
/// writes nothing where it stands; the layout writes it with <see cref="KazoRenderSection"/>.
/// </summary>
public sealed class KazoSection : KazoViewHelper
{
    /// <summary>Gets or sets the section name.</summary>
    [Parameter]
    [EditorRequired]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the content.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        ArgumentException.ThrowIfNullOrEmpty(Name);
        ViewContext.DefineSection(Name, ChildContent ?? (_ => { }));
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
    }
}

/// <summary>Writes a section the view defined (<c>@RenderSection("Scripts", required: false)</c>), in a layout.</summary>
public sealed class KazoRenderSection : KazoViewHelper
{
    /// <summary>Gets or sets the section name.</summary>
    [Parameter]
    [EditorRequired]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets whether a view that does not define the section is an error (yes by default, as in ASP.NET).</summary>
    [Parameter]
    public bool Required { get; set; } = true;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        ArgumentException.ThrowIfNullOrEmpty(Name);
        if (ViewContext.TryRenderSection(Name, out RenderFragment? content))
        {
            builder.AddContent(0, content);
        }
        else if (Required)
        {
            throw new InvalidOperationException($"The layout page cannot find the section '{Name}' in the content page: define it with <KazoSection Name=\"{Name}\">, or mark it Required=\"false\".");
        }
    }
}

/// <summary>
/// Renders a partial view by name (<c>&lt;KazoPartial Name="_NoteCard" Model="note" /&gt;</c>, ASP.NET's
/// <c>&lt;partial&gt;</c>): looked up in the controller's folder then <c>Shared/</c>, rendered with the
/// same request and view data and its own model (the page's model when none is given).
/// </summary>
public sealed class KazoPartial : KazoViewHelper
{
    /// <summary>Gets or sets the view name or path.</summary>
    [Parameter]
    [EditorRequired]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the partial's model; the page's model when not set.</summary>
    [Parameter]
    public object? Model { get; set; }

    private bool _modelSet;

    /// <inheritdoc />
    public override Task SetParametersAsync(ParameterView parameters)
    {
        _modelSet = parameters.TryGetValue(nameof(Model), out object? _);
        return base.SetParametersAsync(parameters);
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        ArgumentException.ThrowIfNullOrEmpty(Name);
        KazoViewDescriptor view = ViewContext.FindPartial(Name);
        object? model = _modelSet ? Model : ViewContext.Model;
        KazoViewEngine.EnsureModelType(view.ViewType, model);
        KazoViewContext partialContext = ViewContext.ForPartial(model);
        builder.OpenComponent<CascadingValue<KazoViewContext>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<KazoViewContext>.Value), partialContext);
        builder.AddComponentParameter(2, nameof(CascadingValue<KazoViewContext>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<KazoViewContext>.ChildContent), (RenderFragment)(inner =>
        {
            if (view.Kind == KazoViewKind.RazorPage)
            {
                // A .cshtml partial (ADR 0086 §5) runs as a page, then is written as markup.
                inner.OpenComponent<KazoRazorPagePartial>(0);
                inner.AddComponentParameter(1, nameof(KazoRazorPagePartial.View), view);
                inner.AddComponentParameter(2, nameof(KazoRazorPagePartial.Context), partialContext);
                inner.AddComponentParameter(3, nameof(KazoRazorPagePartial.Model), model);
            }
            else
            {
                inner.OpenComponent(0, view.ViewType);
            }

            inner.CloseComponent();
        }));
        builder.CloseComponent();
    }
}

/// <summary>Builds route values for the helpers from an anonymous object: <c>RouteValues="@KazoRoute.Values(new { id = note.Id })"</c>.</summary>
public static class KazoRoute
{
    /// <summary>Reads the public properties of <paramref name="values"/> (or copies a dictionary) into route values.</summary>
    /// <typeparam name="TValues">The anonymous type (inferred; its properties survive trimming).</typeparam>
    /// <param name="values">The values.</param>
    public static IReadOnlyDictionary<string, object?> Values<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(TValues values) =>
        KazoRouteValueDictionary.FromObject(values);
}
