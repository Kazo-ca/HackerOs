namespace HackerOs.AppSdk.Apps;

/// <summary>For whom an app is installed or removed (ADR 0189 §3, §4).</summary>
public enum HackPackageScope
{
    /// <summary>
    /// For the session user when allowed (a trusted source, not blocked, not already installed for
    /// the system), otherwise for the system; a removal picks the copy the user has.
    /// </summary>
    Auto = 0,

    /// <summary>For the session user only (<c>~/.local/share/hackeros/apps</c>), without an administrator from a trusted source.</summary>
    User = 1,

    /// <summary>For every user (<c>/usr/lib/hackeros/apps</c>), after an administrator's two-step approval, every time.</summary>
    System = 2
}

/// <summary>Trust level of a source (ADR 0189 §2).</summary>
public enum HackPackageTrust
{
    /// <summary>An app of the official catalog, reviewed and compiled by the HackerOS team.</summary>
    Verified = 0,

    /// <summary>A source compiled into the OS, or marked trusted by an administrator.</summary>
    Trusted = 1,

    /// <summary>Any other source: every install asks an administrator, even for one user.</summary>
    Untrusted = 2
}

/// <summary>Kind of block-list rule of <c>/etc/hackeros/app-policy.config</c> (ADR 0189 §5).</summary>
public enum HackPackageBlockKind
{
    /// <summary>One app, by its ID (<c>[blocked-apps]</c>).</summary>
    App = 0,

    /// <summary>Every app of a category (<c>[blocked-categories]</c>).</summary>
    Category = 1,

    /// <summary>Every app that asks for a capability (<c>[blocked-capabilities]</c>).</summary>
    Capability = 2
}

/// <summary>One catalog of <c>[catalogs]</c> (ADR 0188, ADR 0189 §1).</summary>
/// <param name="Name">Catalog name, such as <c>official</c>.</param>
/// <param name="Address">Address of its <c>catalog.json</c>.</param>
/// <param name="Trust">Its trust level.</param>
/// <param name="BuiltIn">Whether the OS was built with it (it is then trusted without an administrator's change).</param>
public sealed record HackPackageCatalog(string Name, string Address, HackPackageTrust Trust, bool BuiltIn);

/// <summary>Where the OS downloads app packages from, and which sources it trusts (ADR 0182 §3, ADR 0189 §1).</summary>
/// <param name="GitHubHost">Host the OS reads GitHub repositories from (<c>raw.githubusercontent.com</c>).</param>
/// <param name="TrustedSources">
/// Trusted sources: <c>catalog:&lt;name&gt;</c>, <c>github.com/&lt;owner&gt;</c> or
/// <c>github.com/&lt;owner&gt;/&lt;repo&gt;</c>.
/// </param>
/// <param name="Warnings">Lines of <c>/etc/hackeros/app-sources.config</c> that were ignored.</param>
public sealed record HackPackageSources(string GitHubHost, IReadOnlyList<string> TrustedSources, IReadOnlyList<string> Warnings)
{
    /// <summary>Gets the catalogs, in name order.</summary>
    public IReadOnlyList<HackPackageCatalog> Catalogs { get; init; } = [];

    /// <summary>Gets the sources an administrator added without trusting them.</summary>
    public IReadOnlyList<string> UntrustedSources { get; init; } = [];

    /// <summary>Gets the trusted sources the OS was built with.</summary>
    public IReadOnlyList<string> BuiltInSources { get; init; } = [];

    /// <summary>Gets the hours between two update checks.</summary>
    public int UpdateIntervalHours { get; init; }
}

/// <summary>The administrator's block list, <c>/etc/hackeros/app-policy.config</c> (ADR 0189 §5).</summary>
/// <param name="BlockedApps">Blocked app IDs.</param>
/// <param name="BlockedCategories">Blocked categories.</param>
/// <param name="BlockedCapabilities">Blocked capabilities.</param>
/// <param name="Warnings">Lines of the file that were ignored.</param>
public sealed record HackPackagePolicy(
    IReadOnlyList<string> BlockedApps,
    IReadOnlyList<string> BlockedCategories,
    IReadOnlyList<string> BlockedCapabilities,
    IReadOnlyList<string> Warnings);

/// <summary>One app installed from a package, for the system or for the session user.</summary>
/// <param name="AppId">App identifier.</param>
/// <param name="Source">Its repository, such as <c>github:Kazo-ca/HackerOs-App-Webamp</c>.</param>
/// <param name="Version">Installed version.</param>
/// <param name="Loaded">
/// Whether the running OS has this version in its catalog: <see langword="false"/> when it was
/// refused at startup, or until the next restart when an older version was already loaded in the
/// page (ADR 0214 §1). After a removal, <see langword="true"/> means the removed app stays loaded
/// until the next restart.
/// </param>
public sealed record HackInstalledPackage(string AppId, string Source, string Version, bool Loaded)
{
    /// <summary>Gets whether it is installed for every user (otherwise for the session user only).</summary>
    public bool SystemWide { get; init; } = true;
}

/// <summary>What a repository publishes (its <c>index.json</c>, ADR 0182 §2).</summary>
/// <param name="Source">The repository.</param>
/// <param name="AppId">App identifier its packages carry.</param>
/// <param name="Versions">Published versions, newest first.</param>
/// <param name="Latest">Version installed by default (newest stable one), or <see langword="null"/>.</param>
/// <param name="Trusted">Whether its source is trusted.</param>
/// <param name="InstalledVersion">Version installed from it, for the system or for the session user, or <see langword="null"/>.</param>
public sealed record HackPackageInfo(
    string Source, string AppId, IReadOnlyList<string> Versions, string? Latest, bool Trusted, string? InstalledVersion)
{
    /// <summary>Gets the trust level of its source (ADR 0189 §2).</summary>
    public HackPackageTrust Trust { get; init; } = Trusted ? HackPackageTrust.Trusted : HackPackageTrust.Untrusted;

    /// <summary>Gets whether the installed copy is for every user; <see langword="null"/> when not installed.</summary>
    public bool? InstalledSystemWide { get; init; }

    /// <summary>
    /// Gets whether the OS holds a public key for this source (ADR 0214 §2): every package it
    /// installs from it must then carry a valid signature, otherwise it is refused.
    /// </summary>
    public bool SignatureRequired { get; init; }
}

/// <summary>What an install or an update did (ADR 0182 §5, ADR 0189).</summary>
/// <param name="AppId">App identifier.</param>
/// <param name="Name">App name.</param>
/// <param name="Version">Installed version.</param>
/// <param name="Source">Its repository, pinned to the version.</param>
/// <param name="PreviousVersion">The version it replaced, or <see langword="null"/>.</param>
/// <param name="Capabilities">Capabilities the app declares; dangerous ones are asked at its first launch (ADR 0049).</param>
/// <param name="Trusted">Whether its source is trusted.</param>
public sealed record HackPackageInstallReport(
    string AppId,
    string Name,
    string Version,
    string Source,
    string? PreviousVersion,
    IReadOnlyList<string> Capabilities,
    bool Trusted)
{
    /// <summary>
    /// Gets whether the OS must restart before this version can be launched (ADR 0214 §1): only when
    /// a previous version of the app is already loaded in the page, or the OS cannot add apps while
    /// it runs. Otherwise the app is in the launcher, the search and the terminal already.
    /// </summary>
    public bool RestartRequired { get; init; } = true;

    /// <summary>Gets the trust level of its source (ADR 0189 §2).</summary>
    public HackPackageTrust Trust { get; init; } = Trusted ? HackPackageTrust.Trusted : HackPackageTrust.Untrusted;

    /// <summary>Gets whether it was installed for every user (otherwise for the session user only).</summary>
    public bool SystemWide { get; init; } = true;

    /// <summary>Gets the block-list rule that sent it to the system install, such as <c>blocked-categories: social</c>.</summary>
    public string? BlockedBy { get; init; }

    /// <summary>Gets the personal copies a system install replaced (one copy at a time, ADR 0189 §3).</summary>
    public int ReplacedPersonalCopies { get; init; }

    /// <summary>
    /// Gets whether a key of the source verified the package's signature (ADR 0214 §2);
    /// <see langword="false"/> when the source has no key and the package was checked by its SHA-256 only.
    /// </summary>
    public bool Signed { get; init; }
}

/// <summary>A package refused or a source that could not be read (ADR 0182 §5).</summary>
public sealed class HackPackageException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Stable code, such as <c>apps.package.blocked</c> or <c>apps.package.missing-api</c>.</param>
    /// <param name="message">Explanation in English.</param>
    public HackPackageException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the stable code.</summary>
    public string Code { get; }
}
