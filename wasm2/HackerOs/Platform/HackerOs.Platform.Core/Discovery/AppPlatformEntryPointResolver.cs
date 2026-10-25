using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Discovery;

/// <summary>Outcome of resolving a manifest's entry point for one platform, per plan §5 (<c>MOB-005</c>).</summary>
public enum AppPlatformEntryPointResolutionStatus
{
    /// <summary><see cref="AppPlatformEntryPointResolution.EntryPoint"/> is the entry point to activate.</summary>
    Resolved,

    /// <summary>The manifest does not declare support for the requested platform.</summary>
    PlatformUnsupported,

    /// <summary>
    /// The manifest's entry-point declaration is ambiguous or missing (neither or both of
    /// <see cref="AppManifest.EntryPoint"/>/<see cref="AppManifest.Platform"/> set). A validated
    /// catalog (<see cref="AppCatalog.Build(System.Collections.Generic.IEnumerable{AppManifest})"/>) never contains such a manifest — this exists so the
    /// resolver stays safe to call directly on an unvalidated manifest too.
    /// </summary>
    Invalid
}

/// <summary>The result of resolving one manifest's entry point for one platform.</summary>
/// <param name="Status">Outcome discriminator.</param>
/// <param name="EntryPoint">The resolved entry point, only set when <paramref name="Status"/> is <see cref="AppPlatformEntryPointResolutionStatus.Resolved"/>.</param>
public sealed record AppPlatformEntryPointResolution(
    AppPlatformEntryPointResolutionStatus Status,
    AppEntryPointManifest? EntryPoint)
{
    /// <summary>Gets whether resolution succeeded.</summary>
    public bool IsResolved => Status == AppPlatformEntryPointResolutionStatus.Resolved && EntryPoint is not null;
}

/// <summary>
/// Resolves which of a manifest's declared entry points activates an app on a given platform, per
/// doc/en/wasm/dev/old/mobile-interface-platform-plan.md §5 (<c>MOB-005</c>): "Manifest + plateforme active →
/// validation de compatibilité → point d'entrée unique → découverte du type → descripteur effectif →
/// lancement." This covers the first step; type discovery remains <see cref="AppEntryPointDiscovery"/>'s
/// job.
/// </summary>
public interface IAppPlatformEntryPointResolver
{
    /// <summary>Resolves <paramref name="manifest"/>'s entry point for <paramref name="platform"/>.</summary>
    AppPlatformEntryPointResolution Resolve(AppManifest manifest, AppPlatformId platform);
}

/// <summary>
/// Stateless <see cref="IAppPlatformEntryPointResolver"/> built on
/// <see cref="AppManifestPlatformSupport.Resolve"/>. Safe to share; exposes a static
/// <see cref="Instance"/> for callers (like the static <see cref="AppEntryPointDiscovery"/>) that
/// don't otherwise participate in dependency injection.
/// </summary>
public sealed class AppPlatformEntryPointResolver : IAppPlatformEntryPointResolver
{
    /// <summary>Gets the shared stateless instance.</summary>
    public static AppPlatformEntryPointResolver Instance { get; } = new();

    /// <inheritdoc />
    public AppPlatformEntryPointResolution Resolve(AppManifest manifest, AppPlatformId platform)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        AppManifestPlatformResolution? resolution = AppManifestPlatformSupport.Resolve(manifest);
        if (resolution is null)
        {
            return new AppPlatformEntryPointResolution(AppPlatformEntryPointResolutionStatus.Invalid, null);
        }

        return resolution.EntryPointsByPlatform.TryGetValue(platform, out AppEntryPointManifest? entryPoint)
            ? new AppPlatformEntryPointResolution(AppPlatformEntryPointResolutionStatus.Resolved, entryPoint)
            : new AppPlatformEntryPointResolution(AppPlatformEntryPointResolutionStatus.PlatformUnsupported, null);
    }
}
