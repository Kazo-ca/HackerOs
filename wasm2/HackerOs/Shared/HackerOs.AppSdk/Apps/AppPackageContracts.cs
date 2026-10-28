namespace HackerOs.AppSdk.Apps;

/// <summary>Where the OS downloads app packages from, and whose packages it trusts (ADR 0182 §3).</summary>
/// <param name="GitHubHost">Host the OS reads GitHub repositories from (<c>raw.githubusercontent.com</c>).</param>
/// <param name="TrustedOwners">Trusted owners, such as <c>github.com/Kazo-ca</c>.</param>
/// <param name="Warnings">Lines of <c>/etc/hackeros/app-sources.config</c> that were ignored.</param>
public sealed record HackPackageSources(string GitHubHost, IReadOnlyList<string> TrustedOwners, IReadOnlyList<string> Warnings);

/// <summary>One app installed from a package (<c>[installed]</c> of <c>/etc/hackeros/apps.config</c>).</summary>
/// <param name="AppId">App identifier.</param>
/// <param name="Source">Its repository, such as <c>github:Kazo-ca/HackerOs-App-Webamp</c>.</param>
/// <param name="Version">Installed version.</param>
/// <param name="Loaded">
/// Whether the running OS has this version in its catalog; <see langword="false"/> until the next
/// restart after an install or an update, or when it was refused at startup.
/// </param>
public sealed record HackInstalledPackage(string AppId, string Source, string Version, bool Loaded);

/// <summary>What a repository publishes (its <c>index.json</c>, ADR 0182 §2).</summary>
/// <param name="Source">The repository.</param>
/// <param name="AppId">App identifier its packages carry.</param>
/// <param name="Versions">Published versions, newest first.</param>
/// <param name="Latest">Version installed by default (newest stable one), or <see langword="null"/>.</param>
/// <param name="Trusted">Whether its owner is trusted.</param>
/// <param name="InstalledVersion">Version installed from it on this machine, or <see langword="null"/>.</param>
public sealed record HackPackageInfo(
    string Source, string AppId, IReadOnlyList<string> Versions, string? Latest, bool Trusted, string? InstalledVersion);

/// <summary>What an install or an update did (ADR 0182 §5).</summary>
/// <param name="AppId">App identifier.</param>
/// <param name="Name">App name.</param>
/// <param name="Version">Installed version.</param>
/// <param name="Source">Its repository, pinned to the version.</param>
/// <param name="PreviousVersion">The version it replaced, or <see langword="null"/>.</param>
/// <param name="Capabilities">Capabilities the app declares; dangerous ones are asked at its first launch (ADR 0049).</param>
/// <param name="Trusted">Whether its owner is trusted.</param>
public sealed record HackPackageInstallReport(
    string AppId,
    string Name,
    string Version,
    string Source,
    string? PreviousVersion,
    IReadOnlyList<string> Capabilities,
    bool Trusted)
{
    /// <summary>Gets whether the OS must restart before the app can be launched: always, for now (ADR 0182, Q5).</summary>
    public bool RestartRequired => true;
}

/// <summary>A package refused or a source that could not be read (ADR 0182 §5).</summary>
public sealed class HackPackageException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Stable code, such as <c>apps.package.untrusted</c> or <c>apps.package.missing-api</c>.</param>
    /// <param name="message">Explanation in English.</param>
    public HackPackageException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the stable code.</summary>
    public string Code { get; }
}
