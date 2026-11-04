using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Web.Views;

/// <summary>The two kinds of views (ADR 0086 §5).</summary>
public enum KazoViewKind
{
    /// <summary>A Razor component (<c>.razor</c>, ADR 0083), rendered by Blazor's <c>HtmlRenderer</c>.</summary>
    Component,

    /// <summary>A Razor page (<c>.cshtml</c>, ADR 0086), a <see cref="KazoRazorPage"/> executed by the engine.</summary>
    RazorPage,
}

/// <summary>
/// One view of a site: a Razor component (<c>.razor</c>) or page (<c>.cshtml</c>) under <c>Views/</c>, by
/// its path without folder nor extension (<c>Home/Index</c> for <c>Views/Home/Index.razor</c> or
/// <c>Views/Home/Index.cshtml</c>, ADR 0083 §2, ADR 0086 §4).
/// </summary>
public sealed class KazoViewDescriptor
{
    /// <summary>Initializes a view.</summary>
    /// <param name="path">Path under <c>Views/</c>, without extension (<c>Shared/_Layout</c>).</param>
    /// <param name="viewType">The component or page class the Razor compiler generated for the file.</param>
    public KazoViewDescriptor(
        string path,
        [DynamicallyAccessedMembers(KazoViewTypes.Component)] Type viewType)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(viewType);
        Kind = typeof(IComponent).IsAssignableFrom(viewType) ? KazoViewKind.Component
            : typeof(KazoRazorPage).IsAssignableFrom(viewType) ? KazoViewKind.RazorPage
            : throw new ArgumentException($"The view '{path}' ({viewType.FullName}) is neither a Razor component nor a KazoRazorPage.", nameof(viewType));
        Path = path;
        ViewType = viewType;
    }

    /// <summary>Gets whether the view is a <c>.razor</c> component or a <c>.cshtml</c> page.</summary>
    public KazoViewKind Kind { get; }

    /// <summary>Gets the path under <c>Views/</c>, without extension.</summary>
    public string Path { get; }

    /// <summary>Gets the component or page type.</summary>
    [DynamicallyAccessedMembers(KazoViewTypes.Component)]
    public Type ViewType { get; }

    /// <inheritdoc />
    public override string ToString() => $"/Views/{Path}{(Kind == KazoViewKind.RazorPage ? ".cshtml" : ".razor")}";
}

/// <summary>
/// The views of a site, written at build by the route generator from the <c>.razor</c> and <c>.cshtml</c>
/// files under <c>Views/</c> (ADR 0083 §2, ADR 0086 §4): the view engine looks a name up here, no type is searched by
/// reflection at run time.
/// </summary>
public sealed class KazoViewTable
{
    private readonly Dictionary<string, KazoViewDescriptor> _views;
    private readonly Dictionary<Type, KazoViewDescriptor> _byType;

    /// <summary>Initializes a table.</summary>
    /// <param name="views">The views; paths compare without case.</param>
    public KazoViewTable(IEnumerable<KazoViewDescriptor> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        _views = new Dictionary<string, KazoViewDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (KazoViewDescriptor view in views)
        {
            _views[view.Path] = view;
        }

        Views = [.. _views.Values];
        _byType = [];
        foreach (KazoViewDescriptor view in Views)
        {
            _byType.TryAdd(view.ViewType, view);
        }
    }

    /// <summary>A table with no view (a site without <c>Views/</c>).</summary>
    public static KazoViewTable Empty { get; } = new([]);

    /// <summary>Gets every view.</summary>
    public IReadOnlyList<KazoViewDescriptor> Views { get; }

    /// <summary>Gets the view at <paramref name="path"/> (<c>Home/Index</c>), or <see langword="null"/>.</summary>
    /// <param name="path">Path under <c>Views/</c>, without extension.</param>
    public KazoViewDescriptor? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _views.GetValueOrDefault(path);
    }

    /// <summary>Gets the view whose class is <paramref name="viewType"/> (a <c>.cshtml</c> page needs its path), or <see langword="null"/>.</summary>
    /// <param name="viewType">The component or page class.</param>
    public KazoViewDescriptor? Find(Type viewType)
    {
        ArgumentNullException.ThrowIfNull(viewType);
        return _byType.GetValueOrDefault(viewType);
    }
}

/// <summary>Trimming annotations shared by the view types.</summary>
internal static class KazoViewTypes
{
    /// <summary>
    /// What <c>RenderTreeBuilder.OpenComponent(int, Type)</c> requires of a component type; it also keeps a
    /// page's constructor and its <c>@inject</c> properties, which the engine creates and sets.
    /// </summary>
    public const DynamicallyAccessedMemberTypes Component = DynamicallyAccessedMemberTypes.All;
}
