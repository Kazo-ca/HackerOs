using System.Reflection;
using System.Text.Json;

namespace Kazo.Os.AppSdk.Icons;

/// <summary>
/// Loads the four bundled icon libraries from embedded JSON resources on first use and serves
/// lookups/search from an in-memory index for the lifetime of the process.
/// </summary>
public sealed class IconCatalog : IIconCatalog
{
    private static readonly IReadOnlyDictionary<IconLibrary, string> ResourceNames = new Dictionary<IconLibrary, string>
    {
        [IconLibrary.Bootstrap] = "Kazo.Os.AppSdk.Icons.Data.bootstrap-icons.json",
        [IconLibrary.FontAwesome] = "Kazo.Os.AppSdk.Icons.Data.font-awesome.json",
        [IconLibrary.Lucide] = "Kazo.Os.AppSdk.Icons.Data.lucide.json",
        [IconLibrary.SimpleIcons] = "Kazo.Os.AppSdk.Icons.Data.simple-icons.json"
    };

    private readonly Lazy<IReadOnlyDictionary<IconLibrary, IReadOnlyDictionary<string, IconDescriptor>>> _byLibrary;

    /// <summary>Creates a lazily loaded catalog backed by the icon metadata embedded in this assembly.</summary>
    public IconCatalog()
    {
        _byLibrary = new Lazy<IReadOnlyDictionary<IconLibrary, IReadOnlyDictionary<string, IconDescriptor>>>(
            LoadAll, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public IReadOnlyList<IconLibrary> Libraries { get; } = Enum.GetValues<IconLibrary>();

    /// <inheritdoc />
    public int Count(IconLibrary? library = null) =>
        library is { } single
            ? _byLibrary.Value.TryGetValue(single, out var icons) ? icons.Count : 0
            : _byLibrary.Value.Values.Sum(icons => icons.Count);

    /// <inheritdoc />
    public bool TryGet(IconLibrary library, string name, out IconDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_byLibrary.Value.TryGetValue(library, out var icons) && icons.TryGetValue(name, out IconDescriptor? found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<IconDescriptor> GetAll(IconLibrary? library = null) =>
        library is { } single
            ? _byLibrary.Value.TryGetValue(single, out var icons) ? [.. icons.Values] : []
            : [.. _byLibrary.Value.Values.SelectMany(icons => icons.Values)];

    /// <inheritdoc />
    public IReadOnlyList<IconDescriptor> Search(string query, IconLibrary? library = null, int maxResults = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
        IEnumerable<IconDescriptor> candidates = GetAll(library);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [.. candidates.Take(maxResults)];
        }

        return [.. candidates
            .Where(icon =>
                icon.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                icon.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)];
    }

    private static IReadOnlyDictionary<IconLibrary, IReadOnlyDictionary<string, IconDescriptor>> LoadAll()
    {
        Dictionary<IconLibrary, IReadOnlyDictionary<string, IconDescriptor>> result = new();
        foreach ((IconLibrary library, string resourceName) in ResourceNames)
        {
            result[library] = LoadLibrary(library, resourceName);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, IconDescriptor> LoadLibrary(IconLibrary library, string resourceName)
    {
        Assembly assembly = typeof(IconCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded icon resource '{resourceName}'.");

        List<IconRecord>? records = JsonSerializer.Deserialize(stream, IconCatalogJsonSerializerContext.Default.ListIconRecord);
        Dictionary<string, IconDescriptor> byName = new(records?.Count ?? 0, StringComparer.Ordinal);
        if (records is null)
        {
            return byName;
        }

        foreach (IconRecord record in records)
        {
            byName[record.N] = new IconDescriptor(
                Library: library,
                Name: record.N,
                DisplayName: record.T ?? record.N,
                ViewBox: record.V,
                Markup: record.B,
                StrokeBased: record.S == 1,
                Variant: record.G);
        }

        return byName;
    }
}
