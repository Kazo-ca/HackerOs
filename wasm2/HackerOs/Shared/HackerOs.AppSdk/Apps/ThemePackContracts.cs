namespace HackerOs.AppSdk.Apps;

/// <summary>What an entry of a catalog installs (<c>kind</c> of <c>catalog.json</c>, ADR 0213 §2).</summary>
public enum HackCatalogItemKind
{
    /// <summary>An app (<c>.hackapp</c>), installed with <see cref="HackAppPackages.InstallAsync"/>.</summary>
    App = 0,

    /// <summary>A theme pack (<c>.hacktheme</c>), installed with <see cref="HackAppPackages.InstallThemePackAsync"/>.</summary>
    ThemePack = 1
}

/// <summary>The parts a theme pack holds (ADR 0190 §1).</summary>
[Flags]
public enum HackThemePackParts
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Themes (<c>.theme</c>), offered in Settings ▸ Appearance.</summary>
    Themes = 1,

    /// <summary>Wallpapers, offered in Settings ▸ Appearance ▸ Wallpaper.</summary>
    Wallpapers = 2,

    /// <summary>An icon pack, offered in Settings ▸ Appearance ▸ Icon pack.</summary>
    Icons = 4,

    /// <summary>A sound theme named after the pack, offered in Settings ▸ Sounds.</summary>
    Sounds = 8,

    /// <summary>A cursor theme, offered in Settings ▸ Appearance ▸ Cursors.</summary>
    Cursors = 16
}

/// <summary>An installed theme pack (ADR 0213 §3).</summary>
/// <param name="PackId">Pack id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">Installed version.</param>
/// <param name="Source">Where it came from, such as <c>catalog:official/org.example.neon@1.0.0</c>.</param>
/// <param name="SystemWide">Whether it is installed for every user (otherwise for the session user only).</param>
/// <param name="Parts">What it holds.</param>
public sealed record HackInstalledThemePack(string PackId, string Name, string Version, string Source, bool SystemWide, HackThemePackParts Parts);

/// <summary>What the install or the update of a theme pack did (ADR 0213 §4).</summary>
/// <param name="PackId">Pack id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">Installed version.</param>
/// <param name="Source">Its source, pinned to the version.</param>
/// <param name="PreviousVersion">The version it replaced, or <see langword="null"/>.</param>
/// <param name="SystemWide">Whether it was installed for every user.</param>
/// <param name="Parts">What it holds.</param>
/// <param name="Trust">Trust level of its catalog.</param>
public sealed record HackThemePackInstallReport(
    string PackId,
    string Name,
    string Version,
    string Source,
    string? PreviousVersion,
    bool SystemWide,
    HackThemePackParts Parts,
    HackPackageTrust Trust)
{
    /// <summary>Gets whether the OS must restart: never, a pack is data the catalogs read at once.</summary>
    public bool RestartRequired => false;
}
