namespace HackerOs.App.Abstractions;

/// <summary>
/// Declares which platforms an app supports and how each maps to an entry point, per
/// doc/wasm/dev/old/mobile-interface-platform-plan.md §4.1 (<c>MOB-003</c>). One entry point may cover several
/// platforms (a shared component); each supported platform must be covered by exactly one entry
/// point — enforced by <see cref="AppManifestValidator"/>, not by this record itself.
/// </summary>
/// <param name="Supported">Every platform identifier this app declares support for.</param>
/// <param name="EntryPoints">Entry points, each covering one or more of <paramref name="Supported"/>.</param>
public sealed record AppManifestPlatform(
    IReadOnlyList<string> Supported,
    IReadOnlyList<AppPlatformEntryPointManifest> EntryPoints);

/// <summary>
/// One managed entry point, valid for one or more platforms declared in the owning
/// <see cref="AppManifestPlatform.Supported"/> list.
/// </summary>
/// <param name="Platforms">Platform identifiers this entry point activates the app for.</param>
/// <param name="Assembly">Assembly name without a path.</param>
/// <param name="Type">Assembly-qualified or full .NET type name.</param>
public sealed record AppPlatformEntryPointManifest(
    IReadOnlyList<string> Platforms,
    string Assembly,
    string Type);

/// <summary>
/// Normalizes a manifest's platform/entry-point declaration — whichever of the mutually exclusive
/// <see cref="AppManifest.EntryPoint"/> (legacy) or <see cref="AppManifest.Platform"/> (<c>MOB-003</c>)
/// forms it uses — into one canonical shape that <c>IAppPlatformEntryPointResolver</c> (<c>MOB-005</c>)
/// and <see cref="AppManifestValidator"/> both consume. Per plan §4.3, a manifest is never treated as
/// having two independent sources of truth for its entry points.
/// </summary>
public static class AppManifestPlatformSupport
{
    /// <summary>
    /// Resolves <paramref name="manifest"/>'s effective platform support. A manifest using the legacy
    /// <see cref="AppManifest.EntryPoint"/> field is treated as supporting only
    /// <see cref="WellKnownAppPlatforms.Desktop"/>, per plan §4.3 ("migrer l'ancien champ entryPoint
    /// vers une entrée couvrant desktop par défaut").
    /// </summary>
    /// <param name="manifest">Manifest to resolve. Not validated — callers should validate first.</param>
    /// <returns>
    /// The resolved platform support, or <see langword="null"/> when neither or both of
    /// <see cref="AppManifest.EntryPoint"/>/<see cref="AppManifest.Platform"/> are set (an invalid
    /// manifest state <see cref="AppManifestValidator"/> reports separately).
    /// </returns>
    public static AppManifestPlatformResolution? Resolve(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        bool hasLegacyEntryPoint = manifest.EntryPoint is not null;
        bool hasPlatform = manifest.Platform is not null;

        if (hasLegacyEntryPoint == hasPlatform)
        {
            // Neither set, or both set — ambiguous/invalid; let the validator report it.
            return null;
        }

        if (hasLegacyEntryPoint)
        {
            AppEntryPointManifest entryPoint = manifest.EntryPoint!;
            return new AppManifestPlatformResolution(
                SupportedPlatforms: [WellKnownAppPlatforms.Desktop],
                EntryPointsByPlatform: new Dictionary<AppPlatformId, AppEntryPointManifest>
                {
                    [WellKnownAppPlatforms.Desktop] = entryPoint
                });
        }

        AppManifestPlatform platform = manifest.Platform!;
        List<AppPlatformId> supported = [];
        Dictionary<AppPlatformId, AppEntryPointManifest> byPlatform = [];

        foreach (string rawPlatformId in platform.Supported ?? [])
        {
            if (!AppPlatformId.TryParse(rawPlatformId, out AppPlatformId platformId))
            {
                // Malformed identifier — validator reports this; skip it here rather than throwing.
                continue;
            }

            supported.Add(platformId);
        }

        foreach (AppPlatformEntryPointManifest entryPoint in platform.EntryPoints ?? [])
        {
            AppEntryPointManifest resolvedEntryPoint = new(entryPoint.Assembly, entryPoint.Type);
            foreach (string rawPlatformId in entryPoint.Platforms ?? [])
            {
                if (!AppPlatformId.TryParse(rawPlatformId, out AppPlatformId platformId))
                {
                    continue;
                }

                // Duplicate coverage of the same platform by two entry points is a validation error;
                // keep the first one seen here so resolution stays deterministic either way.
                byPlatform.TryAdd(platformId, resolvedEntryPoint);
            }
        }

        return new AppManifestPlatformResolution(supported, byPlatform);
    }
}

/// <summary>
/// The normalized result of <see cref="AppManifestPlatformSupport.Resolve"/>: every platform an app
/// supports and the concrete entry point to activate it on each.
/// </summary>
/// <param name="SupportedPlatforms">Every platform this manifest declares support for.</param>
/// <param name="EntryPointsByPlatform">The entry point to use for each supported platform.</param>
public sealed record AppManifestPlatformResolution(
    IReadOnlyList<AppPlatformId> SupportedPlatforms,
    IReadOnlyDictionary<AppPlatformId, AppEntryPointManifest> EntryPointsByPlatform);
