using System.Diagnostics.CodeAnalysis;
using Kazo.Os.AppSdk.Web.Antiforgery;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.AppSdk.Web.Routing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Views;

/// <summary>
/// Values a controller hands to its view and that the view hands to its layout (ASP.NET's
/// <c>ViewDataDictionary</c>): <c>ViewData["Title"] = "Notes"</c>. Keys compare without case.
/// </summary>
public sealed class KazoViewDataDictionary : Dictionary<string, object?>
{
    /// <summary>Initializes an empty dictionary.</summary>
    public KazoViewDataDictionary()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    /// <summary>
    /// Gets or sets a value; a missing key reads as <see langword="null"/>, as ASP.NET's <c>ViewData["x"]</c>
    /// (a layout can test a value only some views set).
    /// </summary>
    /// <param name="key">The key (case-insensitive).</param>
    public new object? this[string key]
    {
        get => TryGetValue(key, out object? value) ? value : null;
        set => base[key] = value;
    }

    /// <summary>Gets or sets the page title (<c>ViewData["Title"]</c>), which layouts usually put in <c>&lt;title&gt;</c>.</summary>
    public string? Title
    {
        get => TryGetValue("Title", out object? value) ? value?.ToString() : null;
        set => this["Title"] = value;
    }
}

/// <summary>
/// Everything a view, its layout and its helpers see of the request (ASP.NET's <c>ViewContext</c>,
/// ADR 0083 §3): the request, the action, the model, the model state, the view data, the URL helper
/// and the layout to apply. The view engine gives it to every component of the page as a cascading value.
/// </summary>
public sealed class KazoViewContext
{
    private readonly KazoViewEngine _engine;
    private readonly Dictionary<string, KazoViewSection> _sections;
    private readonly HashSet<string> _renderedSections;
    private IKazoUrlHelper? _url;

    internal KazoViewContext(
        KazoActionContext actionContext,
        KazoViewEngine engine,
        object? model,
        KazoViewDataDictionary viewData,
        bool isPartial)
    {
        ActionContext = actionContext;
        _engine = engine;
        Model = model;
        ViewData = viewData;
        IsPartial = isPartial;
        _sections = new Dictionary<string, KazoViewSection>(StringComparer.OrdinalIgnoreCase);
        _renderedSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private KazoViewContext(KazoViewContext parent, object? model)
    {
        ActionContext = parent.ActionContext;
        _engine = parent._engine;
        _url = parent._url;
        Model = model;
        ViewData = parent.ViewData;
        IsPartial = true;
        _sections = parent._sections;
        _renderedSections = parent._renderedSections;
        Renderer = parent.Renderer;
    }

    /// <summary>Gets the engine that renders the page.</summary>
    internal KazoViewEngine Engine => _engine;

    /// <summary>Gets the action context (request, route values, action, model state).</summary>
    public KazoActionContext ActionContext { get; }

    /// <summary>Gets the request.</summary>
    public KazoHttpContext HttpContext => ActionContext.HttpContext;

    /// <summary>Gets the route values of the request.</summary>
    public KazoRouteValueDictionary RouteValues => ActionContext.RouteValues;

    /// <summary>Gets the controller action, or <see langword="null"/> for a minimal route.</summary>
    public KazoControllerActionDescriptor? ActionDescriptor => ActionContext.ActionDescriptor;

    /// <summary>Gets the binding and validation state (errors shown by <see cref="KazoValidationMessage"/>).</summary>
    public KazoModelStateDictionary ModelState => ActionContext.ModelState;

    /// <summary>Gets the model the action passed (<c>View(model)</c>), or that a partial received.</summary>
    public object? Model { get; }

    /// <summary>Gets the view data, shared by the view, its partials and its layouts.</summary>
    public KazoViewDataDictionary ViewData { get; }

    /// <summary>Gets whether this is a partial view (no layout).</summary>
    public bool IsPartial { get; }

    /// <summary>Gets the URL helper.</summary>
    public IKazoUrlHelper Url => _url ??= new KazoUrlHelper(ActionContext);

    /// <summary>
    /// Gets or sets the layout applied once the view (or the current layout) rendered: the
    /// component's <c>@layout</c> by default; a view may change it or set <see langword="null"/>,
    /// like <c>Layout = null</c> in a <c>.cshtml</c>.
    /// </summary>
    [DynamicallyAccessedMembers(KazoViewTypes.Component)]
    public Type? Layout { get; set; }

    /// <summary>Gets the anti-forgery tokens of the request (the cookie is set when first asked).</summary>
    public KazoAntiforgeryTokenSet GetAntiforgeryTokens() =>
        HttpContext.RequestServices.GetRequiredService<KazoAntiforgery>().GetAndStoreTokens(HttpContext);

    /// <summary>Finds a partial view by name, from the current controller's folder then <c>Shared/</c>.</summary>
    /// <param name="name">A name (<c>_NoteCard</c>) or a path (<c>~/Views/Notes/_NoteCard.razor</c>).</param>
    /// <returns>The view.</returns>
    /// <exception cref="InvalidOperationException">No such view; the message lists the searched locations.</exception>
    public KazoViewDescriptor FindPartial(string name) => _engine.GetView(ActionContext, name);

    /// <summary>Creates the context of a partial view: same request and view data, another model.</summary>
    /// <param name="model">The partial's model.</param>
    public KazoViewContext ForPartial(object? model) => new(this, model);

    /// <summary>
    /// Gets or sets the renderer of the page's passes (the engine sets it): a <c>.cshtml</c> page renders
    /// its partials and components with it, inside the same request scope.
    /// </summary>
    internal Microsoft.AspNetCore.Components.Web.HtmlRenderer? Renderer { get; set; }

    internal void DefineSection(string name, RenderFragment content) => DefineSection(name, new KazoViewSection(content, null), replaceRendered: false);

    /// <summary>Defines a section a <c>.cshtml</c> page rendered to HTML (ADR 0086 §5).</summary>
    /// <param name="name">The section name.</param>
    /// <param name="html">Its HTML.</param>
    /// <param name="replaceRendered">
    /// Whether a section already rendered may be defined again: a nested layout re-defines the section it
    /// rendered (<c>@section Scripts { @RenderSection("Scripts", false) }</c>) for the layout around it.
    /// </param>
    internal void DefineSection(string name, string html, bool replaceRendered) =>
        DefineSection(name, new KazoViewSection(null, html), replaceRendered);

    private void DefineSection(string name, KazoViewSection section, bool replaceRendered)
    {
        if (_sections.ContainsKey(name))
        {
            if (!replaceRendered || !_renderedSections.Remove(name))
            {
                throw new InvalidOperationException($"The section '{name}' is already defined.");
            }

            _sections[name] = section;
            return;
        }

        _sections.Add(name, section);
    }

    internal bool TryRenderSection(string name, [NotNullWhen(true)] out RenderFragment? content)
    {
        if (_sections.TryGetValue(name, out KazoViewSection? section))
        {
            _renderedSections.Add(name);
            content = section.Fragment ?? (builder => builder.AddMarkupContent(0, section.Html));
            return true;
        }

        content = null;
        return false;
    }

    /// <summary>Gets the HTML of a section for a <c>.cshtml</c> layout; the engine materialized it before the pass.</summary>
    internal bool TryRenderSectionHtml(string name, [NotNullWhen(true)] out string? html)
    {
        if (_sections.TryGetValue(name, out KazoViewSection? section))
        {
            html = section.Html ?? throw new InvalidOperationException($"The section '{name}' was not rendered to HTML before the layout.");
            _renderedSections.Add(name);
            return true;
        }

        html = null;
        return false;
    }

    /// <summary>Marks a section as rendered without writing it (<c>IgnoreSection</c>).</summary>
    internal void IgnoreSection(string name)
    {
        if (!_sections.ContainsKey(name))
        {
            throw new InvalidOperationException($"The section '{name}' is not defined.");
        }

        _renderedSections.Add(name);
    }

    internal bool IsSectionDefined(string name) => _sections.ContainsKey(name);

    /// <summary>The sections still held as Razor fragments, which a <c>.cshtml</c> layout cannot write as such.</summary>
    internal IEnumerable<KeyValuePair<string, RenderFragment>> FragmentSections =>
        _sections.Where(pair => pair.Value.Html is null).Select(pair => KeyValuePair.Create(pair.Key, pair.Value.Fragment!)).ToArray();

    /// <summary>Replaces a fragment section by its HTML (rendered by the engine).</summary>
    internal void SetSectionHtml(string name, string html) => _sections[name] = new KazoViewSection(_sections[name].Fragment, html);

    internal IEnumerable<string> UnrenderedSections => _sections.Keys.Where(name => !_renderedSections.Contains(name));
}

/// <summary>A section of a page: a Razor fragment (a <c>.razor</c> view), its HTML (a <c>.cshtml</c> page), or both.</summary>
internal sealed record KazoViewSection(RenderFragment? Fragment, string? Html);
