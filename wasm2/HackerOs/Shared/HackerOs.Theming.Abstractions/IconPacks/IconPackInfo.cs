namespace HackerOs.Theming.Abstractions.IconPacks;

/// <summary>Where an icon pack was found (ADR 0203 §2): the machine's folders come before the user's.</summary>
public enum IconPackOrigin
{
    /// <summary><c>/usr/share/icons/</c> or <c>/usr/share/hackeros/theme-packs/*/icons/</c>, for every user.</summary>
    System,

    /// <summary><c>~/.local/share/icons/</c> or <c>~/.local/share/hackeros/theme-packs/*/icons/</c>, for one user.</summary>
    User
}

/// <summary>The identity of one installed icon pack, read from its <c>icon-pack.json</c> (ADR 0203 §1).</summary>
/// <param name="Id">Unique id (lower case letters, digits, <c>.</c>, <c>-</c>, <c>_</c>).</param>
/// <param name="Name">Name shown in Settings ▸ Appearance.</param>
/// <param name="Author">Author, when given.</param>
/// <param name="Version">Version, when given.</param>
/// <param name="Description">Short description, when given.</param>
/// <param name="RootPath">Absolute virtual path of the pack's folder (the one holding <c>icon-pack.json</c>).</param>
/// <param name="Origin">Which folder it came from.</param>
public sealed record IconPackInfo(
    string Id,
    string Name,
    string? Author,
    string? Version,
    string? Description,
    string RootPath,
    IconPackOrigin Origin);

/// <summary>The icon packs found by <see cref="IconPackLibrary.ListAsync"/> and the warnings of the folders read.</summary>
/// <param name="Packs">Accepted packs: system folders first, then the user's, each by folder name.</param>
/// <param name="Diagnostics">One line per refused pack or unreadable manifest.</param>
public sealed record IconPackListResult(IReadOnlyList<IconPackInfo> Packs, IReadOnlyList<string> Diagnostics)
{
    /// <summary>Gets an empty result.</summary>
    public static IconPackListResult Empty { get; } = new([], []);

    /// <summary>Finds an accepted pack by id.</summary>
    /// <param name="id">Pack id.</param>
    public IconPackInfo? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : Packs.FirstOrDefault(pack => string.Equals(pack.Id, id, StringComparison.Ordinal));
}
