namespace Kazo.Os.AppSdk.Icons;

/// <summary>How a <see cref="HackIcon"/> is drawn.</summary>
public enum HackIconKind
{
    /// <summary>A vector icon of a bundled library, resolved through <see cref="IIconCatalog"/>.</summary>
    Vector,

    /// <summary>An image: an asset path of the app, or a URI.</summary>
    Image
}

/// <summary>
/// An icon an app hands to an OS surface (a context menu entry, ADR 0150 §2), described rather than
/// rendered: the OS draws it with its own renderer and theme. Built with <see cref="HackIcons"/>.
/// </summary>
/// <param name="Kind">How the icon is drawn.</param>
/// <param name="Library">The library of a <see cref="HackIconKind.Vector"/> icon; ignored for an image.</param>
/// <param name="Source">The icon's lookup name in <paramref name="Library"/>, or the image's asset path or URI.</param>
public sealed record HackIcon(HackIconKind Kind, IconLibrary Library, string Source);

/// <summary>Creates the <see cref="HackIcon"/> descriptions apps hand to the OS.</summary>
public static class HackIcons
{
    /// <summary>A vector icon of a bundled library, e.g. <c>HackIcons.Vector(IconLibrary.Lucide, "archive")</c>.</summary>
    /// <param name="library">The icon library.</param>
    /// <param name="name">The icon's kebab-case lookup name in <paramref name="library"/>.</param>
    public static HackIcon Vector(IconLibrary library, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new HackIcon(HackIconKind.Vector, library, name);
    }

    /// <summary>An image, e.g. <c>HackIcons.FromImage("/assets/menu-icon.png")</c>.</summary>
    /// <param name="assetPathOrUri">The app's asset path, or an absolute or <c>data:</c> URI.</param>
    public static HackIcon FromImage(string assetPathOrUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPathOrUri);
        return new HackIcon(HackIconKind.Image, default, assetPathOrUri);
    }
}
