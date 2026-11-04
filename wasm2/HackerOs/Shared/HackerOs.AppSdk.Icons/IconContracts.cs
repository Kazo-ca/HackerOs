namespace HackerOs.AppSdk.Icons;

/// <summary>
/// Describes a single icon resolved from an <see cref="IIconCatalog"/>: its source library,
/// stable lookup name, and the inline SVG fragment used to render it.
/// </summary>
/// <param name="Library">The icon library the icon belongs to.</param>
/// <param name="Name">The stable, kebab-case lookup name within <paramref name="Library"/>.</param>
/// <param name="DisplayName">A human-readable label suitable for search results and tooltips.</param>
/// <param name="ViewBox">The SVG <c>viewBox</c> the <paramref name="Markup"/> was authored against.</param>
/// <param name="Markup">
/// The icon's inner SVG markup (one or more <c>&lt;path&gt;</c>/shape elements, no wrapping
/// <c>&lt;svg&gt;</c> tag). Colored via inherited <c>fill</c>/<c>stroke</c> from the host element.
/// </param>
/// <param name="StrokeBased">
/// <see langword="true"/> when the icon is drawn with an outline stroke (e.g. Lucide) rather than a
/// filled shape; renderers should set <c>stroke="currentColor" fill="none"</c> accordingly.
/// </param>
/// <param name="Variant">
/// An optional sub-style within <paramref name="Library"/> (e.g. Font Awesome's
/// <c>solid</c>/<c>regular</c>/<c>brands</c>), or <see langword="null"/> when the library has none.
/// </param>
public sealed record IconDescriptor(
    IconLibrary Library,
    string Name,
    string DisplayName,
    string ViewBox,
    string Markup,
    bool StrokeBased,
    string? Variant);

/// <summary>
/// Resolves and searches the icons bundled with KazoOS. Registered as a process-wide singleton
/// (see <c>EcosystemServiceCollectionExtensions</c>) so it is available to every window, terminal,
/// and OS shell surface without each consumer re-parsing icon data.
/// </summary>
public interface IIconCatalog
{
    /// <summary>Gets every library this catalog has icons for.</summary>
    IReadOnlyList<IconLibrary> Libraries { get; }

    /// <summary>Gets the total number of icons, optionally scoped to one library.</summary>
    int Count(IconLibrary? library = null);

    /// <summary>Attempts to resolve one icon by its exact library and name.</summary>
    bool TryGet(IconLibrary library, string name, out IconDescriptor descriptor);

    /// <summary>Gets every icon, optionally scoped to one library. Order is not guaranteed.</summary>
    IReadOnlyList<IconDescriptor> GetAll(IconLibrary? library = null);

    /// <summary>
    /// Finds icons whose name or display name contains <paramref name="query"/> (ordinal,
    /// case-insensitive), optionally scoped to one library, capped at <paramref name="maxResults"/>.
    /// An empty or whitespace <paramref name="query"/> matches every icon.
    /// </summary>
    IReadOnlyList<IconDescriptor> Search(string query, IconLibrary? library = null, int maxResults = 200);
}
