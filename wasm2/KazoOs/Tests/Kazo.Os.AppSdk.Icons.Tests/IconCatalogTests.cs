using Kazo.Os.AppSdk.Icons;

namespace Kazo.Os.AppSdk.Icons.Tests;

public sealed class IconCatalogTests
{
    private static IconCatalog CreateCatalog() => new();

    [Theory]
    [InlineData(IconLibrary.Bootstrap, 1500)]
    [InlineData(IconLibrary.FontAwesome, 2500)]
    [InlineData(IconLibrary.Lucide, 1500)]
    [InlineData(IconLibrary.SimpleIcons, 3000)]
    public void Count_ReturnsAtLeastExpectedIconsPerLibrary(IconLibrary library, int minimumCount)
    {
        IconCatalog catalog = CreateCatalog();

        Assert.True(catalog.Count(library) >= minimumCount,
            $"Expected at least {minimumCount} icons in {library}, found {catalog.Count(library)}.");
    }

    [Fact]
    public void Count_WithoutLibrary_SumsEveryLibrary()
    {
        IconCatalog catalog = CreateCatalog();

        int expected = catalog.Libraries.Sum(library => catalog.Count(library));

        Assert.Equal(expected, catalog.Count());
    }

    [Theory]
    [InlineData(IconLibrary.Bootstrap, "house")]
    [InlineData(IconLibrary.FontAwesome, "house")]
    [InlineData(IconLibrary.Lucide, "house")]
    [InlineData(IconLibrary.SimpleIcons, "github")]
    public void TryGet_ResolvesWellKnownIcon(IconLibrary library, string name)
    {
        IconCatalog catalog = CreateCatalog();

        bool found = catalog.TryGet(library, name, out IconDescriptor descriptor);

        Assert.True(found);
        Assert.Equal(library, descriptor.Library);
        Assert.Equal(name, descriptor.Name);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.ViewBox));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Markup));
        Assert.Contains('<', descriptor.Markup);
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsFalse()
    {
        IconCatalog catalog = CreateCatalog();

        bool found = catalog.TryGet(IconLibrary.Bootstrap, "definitely-not-a-real-icon-name", out _);

        Assert.False(found);
    }

    [Fact]
    public void Search_IsCaseInsensitiveAndScopesToLibrary()
    {
        IconCatalog catalog = CreateCatalog();

        IReadOnlyList<IconDescriptor> results = catalog.Search("HOUSE", IconLibrary.Bootstrap);

        Assert.NotEmpty(results);
        Assert.All(results, icon => Assert.Equal(IconLibrary.Bootstrap, icon.Library));
        Assert.Contains(results, icon => icon.Name == "house");
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsUpToMaxResults()
    {
        IconCatalog catalog = CreateCatalog();

        IReadOnlyList<IconDescriptor> results = catalog.Search(string.Empty, IconLibrary.Lucide, maxResults: 10);

        Assert.Equal(10, results.Count);
    }

    [Fact]
    public void GetAll_EveryIcon_HasUniqueNameWithinItsLibrary()
    {
        IconCatalog catalog = CreateCatalog();

        foreach (IconLibrary library in catalog.Libraries)
        {
            IReadOnlyList<IconDescriptor> icons = catalog.GetAll(library);
            int distinctNames = icons.Select(icon => icon.Name).Distinct(StringComparer.Ordinal).Count();
            Assert.Equal(icons.Count, distinctNames);
        }
    }

    [Fact]
    public void GetAll_EveryIcon_HasNonEmptyMarkupAndViewBox()
    {
        IconCatalog catalog = CreateCatalog();

        foreach (IconDescriptor icon in catalog.GetAll())
        {
            Assert.False(string.IsNullOrWhiteSpace(icon.ViewBox));
            Assert.False(string.IsNullOrWhiteSpace(icon.Markup));
            Assert.False(string.IsNullOrWhiteSpace(icon.DisplayName));
        }
    }

    [Fact]
    public void Lucide_Icons_AreStrokeBased()
    {
        IconCatalog catalog = CreateCatalog();

        Assert.True(catalog.TryGet(IconLibrary.Lucide, "house", out IconDescriptor icon));
        Assert.True(icon.StrokeBased);
    }

    [Fact]
    public void Bootstrap_Icons_AreFillBased()
    {
        IconCatalog catalog = CreateCatalog();

        Assert.True(catalog.TryGet(IconLibrary.Bootstrap, "house", out IconDescriptor icon));
        Assert.False(icon.StrokeBased);
    }
}
