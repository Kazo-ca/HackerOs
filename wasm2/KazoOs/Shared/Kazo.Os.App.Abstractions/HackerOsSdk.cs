using System.Reflection;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// The version of the Kazo OS App SDK this process runs (ADR 0098 §3), and the support policy
/// that decides whether an app compiled against another SDK version may load.
/// </summary>
/// <remarks>
/// <para>
/// The number is the one every published Kazo OS library carries (<c>build/Versions.props</c>,
/// ADR 0098 §1), read from this assembly's informational version, so it is the SDK the running
/// OS was built with. The manifest generator writes the SDK version an app was <i>compiled</i>
/// against as the app's <c>sdkCompatibility.minimumVersion</c>; <c>AppCatalog</c> then compares
/// the two at load time.
/// </para>
/// <para>
/// Support policy (ADR 0098 §3, deferred decision 11): the OS loads an app compiled against an SDK
/// of the <b>same major version</b>, lower than or equal to its own (SemVer precedence, prerelease
/// labels included). A public API is removed only at the next major version, after at least one
/// minor version marked it <see cref="ObsoleteAttribute"/> (ADR 0019).
/// </para>
/// </remarks>
public static class HackerOsSdk
{
    /// <summary>
    /// Gets the SemVer version of the App SDK of this process, e.g. <c>1.0.0</c>, without
    /// build metadata (the source revision SourceLink appends after <c>+</c>).
    /// </summary>
    public static string Version { get; } = ReadVersion(typeof(HackerOsSdk).Assembly);

    /// <summary>Gets <see cref="Version"/> parsed.</summary>
    public static SemanticVersion SemanticVersion { get; } = SemanticVersion.Parse(Version);

    /// <summary>Gets whether this process's SDK satisfies <paramref name="range"/>.</summary>
    /// <param name="range">The app's declared SDK range.</param>
    /// <returns><see langword="true"/> when the app may load.</returns>
    public static bool IsCompatible(AppSdkCompatibilityManifest range) => IsCompatible(range, SemanticVersion);

    /// <summary>
    /// Gets whether an SDK of version <paramref name="sdkVersion"/> satisfies <paramref name="range"/>:
    /// <c>minimum ≤ sdk</c>, same major version as the minimum, and <c>sdk ≤ maximum</c> when a
    /// maximum is declared. A range whose bounds are not valid SemVer never matches (the manifest
    /// validator already rejects it).
    /// </summary>
    /// <param name="range">The app's declared SDK range.</param>
    /// <param name="sdkVersion">The SDK version to test.</param>
    /// <returns><see langword="true"/> when an app with this range may load on that SDK.</returns>
    public static bool IsCompatible(AppSdkCompatibilityManifest range, SemanticVersion sdkVersion)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!SemanticVersion.TryParse(range.MinimumVersion, out SemanticVersion minimum))
        {
            return false;
        }

        if (minimum.Major != sdkVersion.Major || sdkVersion.CompareTo(minimum) < 0)
        {
            return false;
        }

        if (range.MaximumVersion is null)
        {
            return true;
        }

        return SemanticVersion.TryParse(range.MaximumVersion, out SemanticVersion maximum)
            && sdkVersion.CompareTo(maximum) <= 0;
    }

    /// <summary>
    /// Reads the SemVer version of an SDK assembly: its <see cref="AssemblyInformationalVersionAttribute"/>
    /// without build metadata, or <c>major.minor.patch</c> of its assembly version when it has none.
    /// The manifest generator uses it on the <c>Kazo.Os.App.Abstractions</c> an app was compiled against.
    /// </summary>
    /// <param name="assembly">A Kazo OS SDK assembly.</param>
    /// <returns>The SemVer version text.</returns>
    public static string ReadVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return NormalizeVersion(informational, assembly.GetName().Version);
    }

    /// <summary>
    /// Turns an informational version into SemVer text: build metadata (after <c>+</c>) is dropped;
    /// when the text is missing or not SemVer, <c>major.minor.patch</c> of <paramref name="fallback"/>
    /// (or <c>0.0.0</c>) is used.
    /// </summary>
    /// <param name="informationalVersion">The assembly's informational version, if any.</param>
    /// <param name="fallback">The assembly version.</param>
    /// <returns>The SemVer version text.</returns>
    public static string NormalizeVersion(string? informationalVersion, Version? fallback)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int metadata = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            string candidate = (metadata < 0 ? informationalVersion : informationalVersion[..metadata]).Trim();
            if (SemanticVersion.TryParse(candidate, out _))
            {
                return candidate;
            }
        }

        return fallback is null
            ? "0.0.0"
            : $"{fallback.Major}.{Math.Max(fallback.Minor, 0)}.{Math.Max(fallback.Build, 0)}";
    }
}
