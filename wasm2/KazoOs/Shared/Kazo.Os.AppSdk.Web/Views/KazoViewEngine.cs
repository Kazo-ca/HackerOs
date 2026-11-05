using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Kazo.Os.AppSdk.Web.Mvc;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.AppSdk.Web.Views;

/// <summary>
/// Finds and renders the views of a site (ASP.NET's <c>IViewEngine</c> and <c>IView</c>, ADR 0083 and 0086):
/// a name is looked up in the generated <see cref="KazoViewTable"/>, from <c>Views/&lt;Controller&gt;/</c>
/// then <c>Views/Shared/</c>; a <c>.razor</c> view is rendered to HTML by Blazor's <c>HtmlRenderer</c>
/// (static, no interactivity), a <c>.cshtml</c> page runs after its <c>_ViewStart.cshtml</c> files; then
/// each layout, of either kind, is rendered around the HTML of what it wraps.
/// </summary>
/// <remarks>
/// The view renders <em>before</em> its layout, in a pass of its own, as ASP.NET MVC executes a
/// <c>.cshtml</c> page before its <c>_Layout</c>: what the view sets (<c>ViewData["Title"]</c>,
/// <see cref="KazoSection"/>, <c>Layout</c>) is known when the layout renders. All the passes of a page
/// share one renderer, whose services are the request's scope (<c>@inject</c> works).
/// </remarks>
public sealed class KazoViewEngine
{
    /// <summary>Initializes the engine of a site.</summary>
    /// <param name="views">The site's views.</param>
    public KazoViewEngine(KazoViewTable views)
    {
        ArgumentNullException.ThrowIfNull(views);
        Views = views;
    }

    /// <summary>Gets the site's views.</summary>
    public KazoViewTable Views { get; }

    /// <summary>
    /// Finds a view by name: <c>Index</c> is searched as <c>Views/&lt;Controller&gt;/Index</c> (<c>.razor</c> or
    /// <c>.cshtml</c>), then <c>Views/Shared/Index</c>; a path (<c>~/Views/Notes/Edit.cshtml</c>, <c>/Views/Notes/Edit</c>) is taken as is.
    /// </summary>
    /// <param name="context">The action context (its controller names the folder).</param>
    /// <param name="name">The view name or path.</param>
    /// <param name="searchedLocations">The locations tried, for the error message.</param>
    /// <returns>The view, or <see langword="null"/>.</returns>
    public KazoViewDescriptor? FindView(KazoActionContext context, string name, out IReadOnlyList<string> searchedLocations)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        List<string> candidates = [];
        if (name.StartsWith("~/", StringComparison.Ordinal) || name.StartsWith('/'))
        {
            string path = name.TrimStart('~').TrimStart('/');
            if (path.StartsWith("Views/", StringComparison.OrdinalIgnoreCase))
            {
                path = path["Views/".Length..];
            }

            foreach (string extension in (string[])[".razor", ".cshtml"])
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    path = path[..^extension.Length];
                }
            }

            candidates.Add(path);
        }
        else
        {
            string? controller = context.ActionDescriptor?.ControllerName ?? context.RouteValues.GetString("controller");
            if (!string.IsNullOrEmpty(controller))
            {
                candidates.Add($"{controller}/{name}");
            }

            candidates.Add($"Shared/{name}");
        }

        searchedLocations = [.. candidates.SelectMany(candidate => (string[])[$"/Views/{candidate}.razor", $"/Views/{candidate}.cshtml"])];
        foreach (string candidate in candidates)
        {
            if (Views.Find(candidate) is KazoViewDescriptor view)
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>Finds a view by name, or throws with the searched locations (as ASP.NET MVC does).</summary>
    /// <param name="context">The action context.</param>
    /// <param name="name">The view name or path.</param>
    /// <returns>The view.</returns>
    /// <exception cref="InvalidOperationException">No view has this name.</exception>
    public KazoViewDescriptor GetView(KazoActionContext context, string name) =>
        FindView(context, name, out IReadOnlyList<string> searched) ?? throw new InvalidOperationException(
            $"The view '{name}' was not found. The following locations were searched:{Environment.NewLine}{string.Join(Environment.NewLine, searched)}");

    /// <summary>
    /// Renders a view to HTML: the view, then (unless <paramref name="isPartial"/>) its layouts, each
    /// around the HTML of the previous pass.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <param name="viewType">The view component.</param>
    /// <param name="model">The model (checked against <c>KazoView&lt;TModel&gt;</c>).</param>
    /// <param name="viewData">The view data from the controller.</param>
    /// <param name="isPartial">Whether to skip layouts and section checks (<c>PartialView()</c>).</param>
    /// <returns>The HTML.</returns>
    /// <exception cref="InvalidOperationException">Wrong model type, a layout without <c>@Body</c>, a section not rendered or missing.</exception>
    public async Task<string> RenderAsync(
        KazoActionContext context,
        [DynamicallyAccessedMembers(KazoViewTypes.Component)] Type viewType,
        object? model,
        KazoViewDataDictionary viewData,
        bool isPartial)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(viewType);
        ArgumentNullException.ThrowIfNull(viewData);
        EnsureModelType(viewType, model);

        IServiceProvider services = context.HttpContext.RequestServices;
        ILoggerFactory loggers = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        await using HtmlRenderer renderer = new(services, loggers);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            KazoViewContext viewContext = new(context, this, model, viewData, isPartial) { Renderer = renderer };
            string html;
            if (IsPage(viewType))
            {
                // A .cshtml view: its _ViewStart files run first and give Layout its initial value (ADR 0086 §5).
                string? layout = isPartial ? null : await RunViewStartsAsync(viewType, viewContext).ConfigureAwait(true);
                html = await RenderPageAsync(viewType, viewContext, model, body: null, layout).ConfigureAwait(true);
                if (isPartial)
                {
                    viewContext.Layout = null;
                }
            }
            else
            {
                viewContext.Layout = isPartial ? null : LayoutOf(viewType);
                html = await RenderPassAsync(renderer, viewType, viewContext, body: null).ConfigureAwait(true);
            }

            if (isPartial)
            {
                return html;
            }

            // Layouts from the inside out; a layout that names itself (an @layout in _Imports.razor
            // reaches the layout too) or an earlier one ends the chain instead of looping.
            HashSet<Type> applied = [];
            while (viewContext.Layout is Type layout && applied.Add(layout))
            {
                if (IsPage(layout))
                {
                    // A .cshtml layout writes sections as HTML: those a .razor view defined are rendered first.
                    viewContext.Layout = null;
                    await MaterializeSectionsAsync(renderer, viewContext).ConfigureAwait(true);
                    html = await RenderPageAsync(layout, viewContext, viewContext.Model, body: html, layout: null).ConfigureAwait(true);
                    continue;
                }

                viewContext.Layout = LayoutOf(layout);
                bool bodyRendered = false;
                string inner = html;
                RenderFragment body = builder =>
                {
                    bodyRendered = true;
                    builder.AddMarkupContent(0, inner);
                };

                html = await RenderPassAsync(renderer, layout, viewContext, body).ConfigureAwait(true);
                if (!bodyRendered)
                {
                    throw new InvalidOperationException($"The layout '{layout.Name}' did not render the body: write @Body where the page goes.");
                }
            }

            string[] unrendered = [.. viewContext.UnrenderedSections];
            if (unrendered.Length > 0)
            {
                throw new InvalidOperationException(
                    $"The following sections have been defined but have not been rendered by the layout: '{string.Join("', '", unrendered)}'. " +
                    "Add <KazoRenderSection Name=\"...\" Required=\"false\" /> (or @RenderSection(\"...\", required: false) in a .cshtml) to the layout.");
            }

            return html;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders a partial view of either kind inside a page (<c>Html.PartialAsync</c>): same request, view
    /// data and renderer, its own model, no layout.
    /// </summary>
    /// <param name="viewContext">The context of the page asking.</param>
    /// <param name="name">The partial's name or path.</param>
    /// <param name="model">The partial's model.</param>
    /// <returns>The HTML.</returns>
    internal static async Task<string> RenderPartialAsync(KazoViewContext viewContext, string name, object? model)
    {
        KazoViewDescriptor view = viewContext.FindPartial(name);
        EnsureModelType(view.ViewType, model);
        KazoViewContext partialContext = viewContext.ForPartial(model);
        return view.Kind == KazoViewKind.RazorPage
            ? await viewContext.Engine.RenderPageAsync(view.ViewType, partialContext, model, body: null, layout: null).ConfigureAwait(true)
            : await RenderPassAsync(viewContext.Renderer!, view.ViewType, partialContext, body: null).ConfigureAwait(true);
    }

    /// <summary>Renders a Razor component inside a <c>.cshtml</c> page (<c>Component.InvokeAsync&lt;T&gt;</c>), with the page's context cascaded.</summary>
    /// <param name="viewContext">The context of the page asking.</param>
    /// <param name="component">The component.</param>
    /// <param name="parameters">Its parameters.</param>
    /// <returns>The HTML.</returns>
    internal static Task<string> RenderComponentAsync(
        KazoViewContext viewContext,
        [DynamicallyAccessedMembers(KazoViewTypes.Component)] Type component,
        IReadOnlyDictionary<string, object?>? parameters) =>
        RenderPassAsync(viewContext.Renderer!, component, viewContext, body: null, parameters);

    /// <summary>
    /// Runs a <c>.cshtml</c> page and returns its HTML; its <c>Layout</c> (a view name) then becomes the
    /// context's layout, resolved like a view name.
    /// </summary>
    internal async Task<string> RenderPageAsync(
        [DynamicallyAccessedMembers(KazoViewTypes.Component)] Type pageType,
        KazoViewContext viewContext,
        object? model,
        string? body,
        string? layout)
    {
        EnsureModelType(pageType, model);
        KazoRazorPage page = (KazoRazorPage)Activator.CreateInstance(pageType)!;
        page.Path = Views.Find(pageType)?.ToString() ?? pageType.FullName ?? pageType.Name;
        string html = await page.RenderAsync(pageType, viewContext, model, body, layout).ConfigureAwait(true);
        viewContext.Layout = string.IsNullOrEmpty(page.Layout) ? null : FindLayout(viewContext, page.Layout, page.Path).ViewType;
        return html;
    }

    private KazoViewDescriptor FindLayout(KazoViewContext viewContext, string name, string pagePath) =>
        FindView(viewContext.ActionContext, name, out IReadOnlyList<string> searched) ?? throw new InvalidOperationException(
            $"The layout view '{name}' of '{pagePath}' could not be located. The following locations were searched:{Environment.NewLine}{string.Join(Environment.NewLine, searched)}");

    /// <summary>
    /// Runs the <c>_ViewStart.cshtml</c> files of <c>Views/</c> and of each folder down to the view's
    /// (ASP.NET's order: the outermost first); their output is discarded, their <c>Layout</c> is kept.
    /// </summary>
    /// <returns>The layout the view starts chose, or <see langword="null"/>.</returns>
    private async Task<string?> RunViewStartsAsync(Type viewType, KazoViewContext viewContext)
    {
        string? path = Views.Find(viewType)?.Path;
        if (path is null)
        {
            return null;
        }

        string? layout = null;
        string[] folders = path.Split('/')[..^1];
        for (int depth = 0; depth <= folders.Length; depth++)
        {
            string candidate = depth == 0 ? "_ViewStart" : $"{string.Join('/', folders[..depth])}/_ViewStart";
            if (Views.Find(candidate) is { Kind: KazoViewKind.RazorPage } viewStart)
            {
                KazoRazorPage page = (KazoRazorPage)Activator.CreateInstance(viewStart.ViewType)!;
                page.Path = viewStart.ToString();
                await page.RenderAsync(viewStart.ViewType, viewContext, viewContext.Model, body: null, layout).ConfigureAwait(true);
                layout = page.Layout;
            }
        }

        return layout;
    }

    /// <summary>Renders to HTML the sections a <c>.razor</c> view defined, for a <c>.cshtml</c> layout.</summary>
    private static async Task MaterializeSectionsAsync(HtmlRenderer renderer, KazoViewContext viewContext)
    {
        foreach (KeyValuePair<string, RenderFragment> section in viewContext.FragmentSections)
        {
            string html = await RenderPassAsync(renderer, typeof(KazoFragmentHost), viewContext, section.Value).ConfigureAwait(true);
            viewContext.SetSectionHtml(section.Key, html);
        }
    }

    private static bool IsPage(Type viewType) => typeof(KazoRazorPage).IsAssignableFrom(viewType);

    /// <summary>
    /// Checks that <paramref name="model"/> suits the view, like ASP.NET MVC's "The model item passed into
    /// the ViewDataDictionary is of type X, but this ViewDataDictionary instance requires a model item of type Y".
    /// </summary>
    /// <param name="viewType">The view component.</param>
    /// <param name="model">The model.</param>
    /// <exception cref="InvalidOperationException">The view derives from <c>KazoView&lt;TModel&gt;</c> or <c>KazoRazorPage&lt;TModel&gt;</c> with another model type.</exception>
    public static void EnsureModelType(Type viewType, object? model)
    {
        ArgumentNullException.ThrowIfNull(viewType);
        for (Type? type = viewType; type is not null; type = type.BaseType)
        {
            if (!type.IsGenericType || (type.GetGenericTypeDefinition() != typeof(KazoView<>) && type.GetGenericTypeDefinition() != typeof(KazoRazorPage<>)))
            {
                continue;
            }

            Type expected = type.GetGenericArguments()[0];
            bool suits = model is null
                ? !expected.IsValueType || Nullable.GetUnderlyingType(expected) is not null
                : expected.IsInstanceOfType(model);
            if (!suits)
            {
                throw new InvalidOperationException(
                    $"The model passed to the view '{viewType.Name}' is {(model is null ? "null" : $"of type '{model.GetType().FullName}'")}, " +
                    $"but this view requires a model of type '{expected.FullName}'.");
            }

            return;
        }
    }

    [return: DynamicallyAccessedMembers(KazoViewTypes.Component)]
    [UnconditionalSuppressMessage("Trimming", "IL2073", Justification = "A layout is a component the Razor compiler named in [Layout(typeof(...))] of the same assembly; Blazor's own LayoutView renders it the same way.")]
    private static Type? LayoutOf(Type component) => component.GetCustomAttribute<LayoutAttribute>()?.LayoutType;

    private static async Task<string> RenderPassAsync(
        HtmlRenderer renderer,
        [DynamicallyAccessedMembers(KazoViewTypes.Component)] Type component,
        KazoViewContext viewContext,
        RenderFragment? body,
        IReadOnlyDictionary<string, object?>? componentParameters = null)
    {
        ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(KazoViewHost.Pass)] = new KazoViewPass(component, viewContext, body, componentParameters)
        });
        HtmlRootComponent root = await renderer.RenderComponentAsync<KazoViewHost>(parameters).ConfigureAwait(false);
        return root.ToHtmlString();
    }
}

/// <summary>One rendering pass: the component to render, the page context and the layout's body.</summary>
/// <remarks>Carried as one object so the component type keeps its trimming annotation (a parameter set by reflection could not).</remarks>
internal sealed class KazoViewPass(
    [DynamicallyAccessedMembers(KazoViewTypes.Component)] Type component,
    KazoViewContext viewContext,
    RenderFragment? body,
    IReadOnlyDictionary<string, object?>? parameters = null)
{
    [DynamicallyAccessedMembers(KazoViewTypes.Component)]
    public Type Component { get; } = component;

    public KazoViewContext ViewContext { get; } = viewContext;

    public RenderFragment? Body { get; } = body;

    /// <summary>Gets the parameters of a component rendered by <c>Component.InvokeAsync</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; } = parameters;
}

/// <summary>
/// The root of one rendering pass: gives the page's <see cref="KazoViewContext"/> to every component
/// as a fixed cascading value, then renders the view (or a layout, with its <c>Body</c>).
/// </summary>
internal sealed class KazoViewHost : IComponent
{
    private RenderHandle _handle;

    [Parameter]
    public KazoViewPass Pass { get; set; } = default!;

    public void Attach(RenderHandle renderHandle) => _handle = renderHandle;

    public Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        _handle.Render(BuildRenderTree);
        return Task.CompletedTask;
    }

    private void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<KazoViewContext>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<KazoViewContext>.Value), Pass.ViewContext);
        builder.AddComponentParameter(2, nameof(CascadingValue<KazoViewContext>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<KazoViewContext>.ChildContent), (RenderFragment)RenderComponent);
        builder.CloseComponent();
    }

    private void RenderComponent(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, Pass.Component);
        if (Pass.Body is not null)
        {
            builder.AddComponentParameter(1, nameof(KazoLayout.Body), Pass.Body);
        }

        foreach (KeyValuePair<string, object?> parameter in Pass.Parameters ?? new Dictionary<string, object?>())
        {
            builder.AddComponentParameter(2, parameter.Key, parameter.Value);
        }

        builder.CloseComponent();
    }
}

/// <summary>Renders a section's fragment alone, to turn it into HTML for a <c>.cshtml</c> layout.</summary>
internal sealed class KazoFragmentHost : IComponent
{
    private RenderHandle _handle;

    [Parameter]
    public RenderFragment? Body { get; set; }

    public void Attach(RenderHandle renderHandle) => _handle = renderHandle;

    public Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        _handle.Render(builder => builder.AddContent(0, Body));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A <c>.cshtml</c> partial inside a <c>.razor</c> view (<c>&lt;KazoPartial Name="_Card" /&gt;</c>): the page
/// runs while the component initializes, then its HTML is written as markup.
/// </summary>
internal sealed class KazoRazorPagePartial : ComponentBase
{
    private string? _html;

    [Parameter]
    public KazoViewDescriptor View { get; set; } = default!;

    [Parameter]
    public KazoViewContext Context { get; set; } = default!;

    [Parameter]
    public object? Model { get; set; }

    protected override async Task OnInitializedAsync() =>
        _html = await Context.Engine.RenderPageAsync(View.ViewType, Context, Model, body: null, layout: null).ConfigureAwait(true);

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_html is not null)
        {
            builder.AddMarkupContent(0, _html);
        }
    }
}
