using System.Diagnostics.CodeAnalysis;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Mvc;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Web.Views;

/// <summary>
/// Base of the views and layouts of an ASP.Hack site (ADR 0083 §3): a Razor component rendered once
/// to HTML, which sees the request through the cascading <see cref="HackViewContext"/>. Any component
/// can be a view; deriving from this class adds the MVC shortcuts (<see cref="ViewData"/>,
/// <see cref="Url"/>, <see cref="ModelState"/>, <see cref="Layout"/>).
/// </summary>
public abstract class HackViewBase : ComponentBase
{
    private HackViewContext? _viewContext;

    /// <summary>Gets or sets the context of the page (set by the view engine).</summary>
    [CascadingParameter]
    public HackViewContext ViewContext
    {
        get => _viewContext ?? throw new InvalidOperationException(
            $"{GetType().Name} is an ASP.Hack view: it is rendered by View() or PartialView(), which give it its HackViewContext.");
        set => _viewContext = value;
    }

    /// <summary>Gets whether the component is rendered by the view engine (it has a context).</summary>
    protected bool HasViewContext => _viewContext is not null;

    /// <summary>Gets the request.</summary>
    protected HackHttpContext HttpContext => ViewContext.HttpContext;

    /// <summary>Gets the HTTP request.</summary>
    protected HackHttpRequest Request => ViewContext.HttpContext.Request;

    /// <summary>Gets the view data shared with the controller, the partials and the layouts.</summary>
    protected HackViewDataDictionary ViewData => ViewContext.ViewData;

    /// <summary>Gets the binding and validation state of the action.</summary>
    protected HackModelStateDictionary ModelState => ViewContext.ModelState;

    /// <summary>Gets the URL helper (<c>Url.Action("Details", "Notes", new { id = 3 })</c>, <c>Url.Content("~/css/site.css")</c>).</summary>
    protected IHackUrlHelper Url => ViewContext.Url;

    /// <summary>
    /// Gets or sets the layout applied after this component rendered (its <c>@layout</c> by default);
    /// <see langword="null"/> renders the page without layout.
    /// </summary>
    /// <remarks>
    /// Not annotated for the trimmer on purpose: every view class is kept with all its members (the
    /// generated view table and Razor name it with <see cref="HackViewTypes.Component"/>), and an annotated
    /// member of a view base would then be reported as reached by reflection in every site (IL2111).
    /// </remarks>
    protected Type? Layout
    {
        get => ViewContext.Layout;
        [UnconditionalSuppressMessage("Trimming", "IL2067",
            Justification = "A view sets its layout from typeof() of a component of its own assembly, as Razor's [Layout(typeof(...))] does; Blazor's LayoutView renders it the same way.")]
        set => ViewContext.Layout = value;
    }
}

/// <summary>A view without a typed model (<c>@inherits HackView</c>).</summary>
public abstract class HackView : HackViewBase
{
    /// <summary>Gets the model the action passed, untyped.</summary>
    protected object? Model => ViewContext.Model;
}

/// <summary>
/// A view with a typed model (<c>@inherits HackView&lt;NoteListModel&gt;</c>, ADR 0076 §7): <see cref="Model"/>
/// is the value the action passed to <c>View(model)</c>, or the <c>Model</c> parameter when the view
/// is used as a component (<c>&lt;NoteCard Model="note" /&gt;</c>).
/// </summary>
/// <typeparam name="TModel">The model type; the view engine refuses another type with a clear error.</typeparam>
public abstract class HackView<TModel> : HackViewBase
{
    private bool _modelSet;
    private TModel _model = default!;

    /// <summary>Gets or sets the model.</summary>
    /// <remarks>Not an auto-property (BL0007): a view rendered by the engine reads the model from its context, a view used as a component from its parameter.</remarks>
    [Parameter]
#pragma warning disable BL0007
    public TModel Model
    {
        get => _modelSet || !HasViewContext ? _model : ViewContext.Model is TModel model ? model : default!;
        set
        {
            _model = value;
            _modelSet = true;
        }
    }
#pragma warning restore BL0007
}

/// <summary>
/// A layout (<c>@inherits HackLayout</c>, then <c>@layout</c> in the views or their <c>_Imports.razor</c>):
/// the page around the view's HTML, which it writes with <c>@Body</c>. It renders after the view, as in
/// ASP.NET MVC, so it sees what the view set (<c>ViewData["Title"]</c>, sections).
/// </summary>
public abstract class HackLayout : HackViewBase
{
    /// <summary>Gets or sets the HTML of the view (or of the inner layout).</summary>
    [Parameter]
    public RenderFragment? Body { get; set; }

    /// <summary>Gets the model of the view.</summary>
    protected object? Model => ViewContext.Model;
}
