using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;

namespace Kazo.Os.Ecosystem;

/// <summary>The apps this host was built with (its <see cref="HostAppCatalog"/>).</summary>
/// <remarks>
/// The catalog comes from the <c>Kazo.Os.BuiltInApplications.json</c> resource the build
/// aggregates into this assembly from every referenced app's generated <c>manifest.json</c>
/// (ADR 0047 §3, §5). The server host and test/test reference this project and read the same one.
/// System apps (ADR 0048 §1) are exposed by <see cref="AppCatalog.SystemAppIds"/> and
/// <see cref="AppCatalog.IsSystemApp"/> on <see cref="Catalog"/>.
/// </remarks>
public static class BuildKnownLazyApps
{
    /// <summary>Gets this host's apps, read once from its own assembly.</summary>
    public static HostAppCatalog Host { get; } = HostAppCatalog.Load(typeof(BuildKnownLazyApps).Assembly);

    /// <summary>Gets the validated immutable catalog used for lazy descriptor discovery.</summary>
    public static AppCatalog Catalog => Host.Catalog;

    /// <summary>
    /// Gets the executable image of each built-in app, by app id: the assembly sizes the host build
    /// measured (ADR 0068, image layer). An app missing here has no image layer.
    /// </summary>
    public static IReadOnlyDictionary<string, AppImage> Images => Host.Images;
}

/// <summary>Single source of truth for optional assemblies emitted by this host build.</summary>
public static class BuildKnownLazyAssemblies
{
    /// <summary>Gets the exact app assemblies declared by the immutable host catalog (<see cref="HostAppCatalog.LazyAssemblyNames"/>).</summary>
    public static IReadOnlyList<string> Names => BuildKnownLazyApps.Host.LazyAssemblyNames;
}
