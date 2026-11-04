using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Web.Routing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// The <c>Router</c> of a Blazor site (ADR 0084, section 5): finds the page whose <c>@page</c>
/// template matches the tab's address and renders it in its layout. Templates are Blazor's
/// (<c>/notes/{id:int}</c>, <c>/{name?}</c>, <c>/files/{*path}</c>) and route values are assigned to
/// the page's <c>[Parameter]</c> properties by name. The site's root component is usually this
/// router alone, as a Blazor WebAssembly <c>App.razor</c> is.
/// </summary>
/// <example>
/// <code>
/// &lt;HackSiteRouter AppAssembly="typeof(App).Assembly" DefaultLayout="typeof(MainLayout)"&gt;
///     &lt;NotFound&gt;&lt;p&gt;Nothing here.&lt;/p&gt;&lt;/NotFound&gt;
/// &lt;/HackSiteRouter&gt;
/// </code>
/// </example>
public sealed class HackSiteRouter : HackComponentBase, IDisposable
{
    private static readonly Type[] s_supportedParameterTypes =
    [
        typeof(string), typeof(int), typeof(long), typeof(short), typeof(bool), typeof(Guid),
        typeof(decimal), typeof(double), typeof(float), typeof(DateTime), typeof(DateOnly), typeof(TimeOnly)
    ];

    private readonly List<RouteEntry> _routes = [];
    private Assembly? _indexedAssembly;
    private HackWebSite? _site;

    /// <summary>Gets or sets the assembly whose components carry <c>@page</c> directives.</summary>
    [Parameter, EditorRequired]
    public Assembly AppAssembly { get; set; } = default!;

    /// <summary>Gets or sets the layout of pages that declare none (<c>@layout</c> wins).</summary>
    [Parameter]
    public Type? DefaultLayout { get; set; }

    /// <summary>Gets or sets what to render when no page matches the address.</summary>
    [Parameter]
    public RenderFragment? NotFound { get; set; }

    /// <summary>Gets the tab's site services, cascaded by <see cref="HackWebSiteView"/>.</summary>
    [CascadingParameter]
    public HackWebSite? Site { get; set; }

    /// <summary>Gets the routes found in <see cref="AppAssembly"/>, most specific first.</summary>
    public IReadOnlyList<(string Template, Type Page)> Routes =>
        [.. _routes.Select(route => (route.Pattern.RawText, route.Page))];

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Pages come from an installed app's assembly, which a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    protected override void OnParametersSet()
    {
        ArgumentNullException.ThrowIfNull(AppAssembly);
        if (!ReferenceEquals(_indexedAssembly, AppAssembly))
        {
            _indexedAssembly = AppAssembly;
            IndexRoutes(AppAssembly);
        }

        if (!ReferenceEquals(_site, Site))
        {
            if (_site is not null)
            {
                _site.Navigation.LocationChanged -= OnLocationChanged;
            }

            _site = Site;
            if (_site is not null)
            {
                _site.Navigation.LocationChanged += OnLocationChanged;
            }
        }
    }

    /// <summary>Finds the page for <paramref name="path"/> (<c>/notes/5</c>) and its route values.</summary>
    /// <param name="path">A site-relative path, with or without the leading slash; the query string is ignored.</param>
    /// <param name="page">The matching page type.</param>
    /// <param name="values">The route values.</param>
    /// <returns><see langword="true"/> when a page matches.</returns>
    public bool TryMatch(
        string path,
        [NotNullWhen(true)] out Type? page,
        out HackRouteValueDictionary values)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (AppAssembly is not null && !ReferenceEquals(_indexedAssembly, AppAssembly))
        {
            _indexedAssembly = AppAssembly;
            IndexRoutes(AppAssembly);
        }

        int query = path.IndexOfAny(['?', '#']);
        string[] segments = HackRoutePattern.SplitPath(Uri.UnescapeDataString(query < 0 ? path : path[..query]));
        foreach (RouteEntry route in _routes)
        {
            if (route.Pattern.TryMatch(segments, out values))
            {
                page = route.Page;
                return true;
            }
        }

        page = null;
        values = [];
        return false;
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "Layouts come from an installed app's assembly, which a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        string path = _site is null ? "/" : "/" + _site.Navigation.ToBaseRelativePath(_site.Navigation.Uri);
        if (!TryMatch(path, out Type? page, out HackRouteValueDictionary values))
        {
            if (NotFound is not null)
            {
                NotFound(builder);
            }
            else
            {
                builder.OpenElement(0, "p");
                builder.AddAttribute(1, "role", "alert");
                // The default "not found" text speaks the site process's language (ADR 0104).
                builder.AddContent(2, Kazo.Os.AppSdk.Globalization.HackLocalization.GetLocalizer<Strings>(AppContext)["Router.NotFound"].Value);
                builder.CloseElement();
            }

            return;
        }

        RenderFragment body = pageBuilder => RenderPage(pageBuilder, page, values);
        Type? layout = page.GetCustomAttribute<LayoutAttribute>()?.LayoutType ?? DefaultLayout;
        RenderInLayouts(builder, layout, body);
    }

    /// <summary>Wraps <paramref name="body"/> in <paramref name="layout"/> and in the layouts that layout declares.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2067",
        Justification = "Layouts come from an installed app's assembly, which a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    private static void RenderInLayouts(RenderTreeBuilder builder, Type? layout, RenderFragment body)
    {
        if (layout is null)
        {
            body(builder);
            return;
        }

        RenderFragment inLayout = layoutBuilder =>
        {
            layoutBuilder.OpenComponent(0, layout);
            layoutBuilder.AddComponentParameter(1, nameof(HackSiteLayoutBase.Body), body);
            layoutBuilder.CloseComponent();
        };
        RenderInLayouts(builder, layout.GetCustomAttribute<LayoutAttribute>()?.LayoutType, inLayout);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "Pages come from an installed app's assembly, which a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    private static void RenderPage(
        RenderTreeBuilder builder, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type page, HackRouteValueDictionary values)
    {
        builder.OpenComponent(0, page);
        int sequence = 1;
        foreach (PropertyInfo property in page.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<ParameterAttribute>() is null
                || !values.TryGetValue(property.Name, out object? raw)
                || raw is not string text)
            {
                continue;
            }

            builder.AddComponentParameter(sequence++, property.Name, ConvertRouteValue(text, property.PropertyType, property.Name));
        }

        builder.CloseComponent();
    }

    /// <summary>Converts a route value to the page parameter's type, as Blazor's router does.</summary>
    internal static object? ConvertRouteValue(string text, Type type, string name)
    {
        Type target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(string))
        {
            return text;
        }

        if (!s_supportedParameterTypes.Contains(target))
        {
            throw new InvalidOperationException(
                $"The route parameter '{name}' has type '{type}', which a page parameter cannot receive from the address.");
        }

        try
        {
            return target == typeof(Guid) ? Guid.Parse(text)
                : target == typeof(DateOnly) ? DateOnly.Parse(text, CultureInfo.InvariantCulture)
                : target == typeof(TimeOnly) ? TimeOnly.Parse(text, CultureInfo.InvariantCulture)
                : Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw new InvalidOperationException($"The route value '{text}' is not a valid '{type}' for the parameter '{name}'.", exception);
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Pages come from an installed app's assembly, which a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    private void IndexRoutes(Assembly assembly)
    {
        _routes.Clear();
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(IComponent).IsAssignableFrom(type))
            {
                continue;
            }

            foreach (RouteAttribute route in type.GetCustomAttributes<RouteAttribute>())
            {
                _routes.Add(new RouteEntry(HackRoutePattern.Parse(route.Template), type));
            }
        }

        // The most specific template first: literals before parameters, shorter before longer.
        _routes.Sort((x, y) => ComparePrecedence(x.Pattern.Precedence, y.Pattern.Precedence));
    }

    private static int ComparePrecedence(IReadOnlyList<int> x, IReadOnlyList<int> y)
    {
        for (int index = 0; index < Math.Min(x.Count, y.Count); index++)
        {
            int compared = x[index].CompareTo(y[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return x.Count.CompareTo(y.Count);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_site is not null)
        {
            _site.Navigation.LocationChanged -= OnLocationChanged;
        }
    }

    private sealed record RouteEntry(HackRoutePattern Pattern, Type Page);
}
