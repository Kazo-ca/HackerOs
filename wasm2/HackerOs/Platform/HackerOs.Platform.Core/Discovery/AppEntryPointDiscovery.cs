using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Discovery;

/// <summary>
/// Resolves each catalog manifest's declared entry point to a concrete, correctly based type
/// using only an explicit host-provided assembly list, per `P1-APP-002` and `P1-APP-003`.
/// </summary>
/// <remarks>
/// Platform Core intentionally has no project reference to the Blazor App SDK (window rendering
/// is out of scope for Phase 1), so a <see cref="AppKind.Window"/> entry point cannot be verified
/// with a direct <c>typeof(WindowAppBase).IsAssignableFrom(...)</c> check. Every app kind is
/// therefore verified the same way: by walking the candidate type's base-type chain and comparing
/// each ancestor's full name against the expected SDK base type name for that <see cref="AppKind"/>.
/// This never instantiates app code and never loads an assembly beyond what the host supplied.
/// </remarks>
public static class AppEntryPointDiscovery
{
    private static readonly IReadOnlyDictionary<AppKind, string> ExpectedBaseTypeFullNames = new Dictionary<AppKind, string>
    {
        [AppKind.Window] = "HackerOs.AppSdk.Blazor.WindowAppBase",
        [AppKind.Terminal] = "HackerOs.AppSdk.TerminalAppBase",
        [AppKind.Service] = "HackerOs.AppSdk.ServiceAppBase",
        // ADR 0073: a web site is a service-like process (WebServerAppBase derives from ServiceAppBase).
        [AppKind.Web] = "HackerOs.AppSdk.ServiceAppBase"
    };

    private const string AppBackHandlerInterfaceFullName = "HackerOs.AppSdk.Blazor.IAppBackHandler";

    /// <summary>
    /// Resolves every manifest in <paramref name="catalog"/> to a validated <see cref="AppDescriptor"/>.
    /// </summary>
    /// <param name="catalog">Catalog whose manifests already passed dependency/version validation.</param>
    /// <param name="hostAssemblies">
    /// The explicit, host-provided set of assemblies apps may be loaded from, keyed by simple
    /// assembly name. Discovery never scans <see cref="AppDomain"/> or loads assemblies itself.
    /// </param>
    /// <param name="activePlatform">
    /// The platform to resolve each manifest's entry point for, per plan §5 (<c>MOB-005</c>).
    /// Defaults to <see cref="WellKnownAppPlatforms.Desktop"/> so every existing caller keeps its
    /// current behavior unchanged — no shell today ever resolves discovery for another platform
    /// (that is Phase 2/<c>MOB-008</c>). A manifest that does not support <paramref name="activePlatform"/>
    /// is silently excluded from the result, not reported as an error, per plan §5 ("une application
    /// incompatible n'apparaît pas dans le launcher actif").
    /// </param>
    /// <returns>Every resolved descriptor, or every deterministic resolution error.</returns>
    /// <remarks>
    /// Resolves manifest-declared type names by name (<see cref="Assembly.GetType(string, bool)"/>),
    /// which is fundamentally reflection-based and cannot be statically analyzed for trimming. This
    /// is a bounded, explicit reflection boundary: only assemblies the host itself passes in
    /// <paramref name="hostAssemblies"/> are consulted, matching the apps the host references
    /// (ADR 0047, 0048). A trimmed host publish keeps every such assembly whole through the root
    /// descriptor the build generates from its manifests (ADR 0106 §2, ADR 0113).
    /// </remarks>
    [RequiresUnreferencedCode(
        "Resolves manifest entry-point types by name from an explicit host assembly list; a trimmed host " +
        "keeps those assemblies whole through its generated root descriptor (ADR 0113).")]
    public static AppDiscoveryResult Discover(
        AppCatalog catalog,
        IReadOnlyDictionary<string, Assembly> hostAssemblies,
        AppPlatformId? activePlatform = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hostAssemblies);

        AppPlatformId platform = activePlatform ?? WellKnownAppPlatforms.Desktop;
        List<AppDiscoveryError> errors = [];
        Dictionary<string, AppDescriptor> descriptors = new(StringComparer.Ordinal);

        foreach (AppManifest manifest in catalog.Manifests.Values.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            AppPlatformEntryPointResolution resolution = AppPlatformEntryPointResolver.Instance.Resolve(manifest, platform);
            if (resolution.Status == AppPlatformEntryPointResolutionStatus.PlatformUnsupported)
            {
                continue;
            }

            if (!resolution.IsResolved || resolution.EntryPoint is not AppEntryPointManifest entryPoint)
            {
                errors.Add(new AppDiscoveryError(
                    "discovery.platform.declaration-invalid",
                    manifest.Id,
                    "The manifest's entry-point/platform declaration is ambiguous or missing."));
                continue;
            }

            if (!hostAssemblies.TryGetValue(entryPoint.Assembly, out Assembly? assembly))
            {
                errors.Add(new AppDiscoveryError(
                    "discovery.assembly.not-allowed",
                    manifest.Id,
                    $"Assembly '{entryPoint.Assembly}' is not in the host's explicit assembly list."));
                continue;
            }

            Type? entryType = assembly.GetType(entryPoint.Type, throwOnError: false);
            if (entryType is null)
            {
                errors.Add(new AppDiscoveryError(
                    "discovery.type.not-found",
                    manifest.Id,
                    $"Type '{entryPoint.Type}' was not found in assembly '{entryPoint.Assembly}'."));
                continue;
            }

            if (!entryType.IsClass || entryType.IsAbstract)
            {
                errors.Add(new AppDiscoveryError(
                    "discovery.type.not-concrete",
                    manifest.Id,
                    $"Type '{entryType.FullName}' must be a concrete, non-abstract class."));
                continue;
            }

            string expectedBase = ExpectedBaseTypeFullNames[manifest.Kind];
            if (!DerivesFrom(entryType, expectedBase))
            {
                errors.Add(new AppDiscoveryError(
                    "discovery.type.wrong-base",
                    manifest.Id,
                    $"Type '{entryType.FullName}' must derive from '{expectedBase}' for app kind '{manifest.Kind}'."));
                continue;
            }

            if (manifest.SupportsBack && !Implements(entryType, AppBackHandlerInterfaceFullName))
            {
                errors.Add(new AppDiscoveryError(
                    "discovery.backHandler.missing",
                    manifest.Id,
                    $"Type '{entryType.FullName}' declares supportsBack but does not implement '{AppBackHandlerInterfaceFullName}'."));
                continue;
            }

            descriptors.Add(manifest.Id, new AppDescriptor(manifest, entryType, assembly)
            {
                PlatformEntryPointTypes = ResolveOtherPlatformTypes(manifest, platform, entryPoint, hostAssemblies)
            });
        }

        return errors.Count > 0
            ? new AppDiscoveryResult(null, errors)
            : new AppDiscoveryResult(descriptors, []);
    }

    /// <summary>
    /// Resolves the entry points the manifest declares for the other platforms (ADR 0171 §1): a mobile window
    /// next to the desktop one. Only a type found in the host's assemblies, concrete and of the same base
    /// as the main entry point is kept; anything else falls back to the main entry point, so a platform
    /// variant can never stop the app from launching.
    /// </summary>
    [RequiresUnreferencedCode("Resolves manifest entry-point types by name from an explicit host assembly list.")]
    private static IReadOnlyDictionary<AppPlatformId, Type> ResolveOtherPlatformTypes(
        AppManifest manifest,
        AppPlatformId platform,
        AppEntryPointManifest mainEntryPoint,
        IReadOnlyDictionary<string, Assembly> hostAssemblies)
    {
        Dictionary<AppPlatformId, Type> types = [];
        if (AppManifestPlatformSupport.Resolve(manifest) is not { } resolution)
        {
            return types;
        }

        string expectedBase = ExpectedBaseTypeFullNames[manifest.Kind];
        foreach ((AppPlatformId other, AppEntryPointManifest entryPoint) in resolution.EntryPointsByPlatform)
        {
            if (other == platform || entryPoint.Equals(mainEntryPoint)
                || !hostAssemblies.TryGetValue(entryPoint.Assembly, out Assembly? assembly)
                || assembly.GetType(entryPoint.Type, throwOnError: false) is not { IsClass: true, IsAbstract: false } type
                || !DerivesFrom(type, expectedBase))
            {
                continue;
            }

            types[other] = type;
        }

        return types;
    }

    /// <summary>Walks a type's base-type chain, comparing full names without loading the ancestor assembly by reference.</summary>
    private static bool DerivesFrom(Type candidate, string expectedBaseFullName)
    {
        for (Type? current = candidate.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, expectedBaseFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks a type's implemented interfaces by full name, the same reflection-only,
    /// no-project-reference approach <see cref="DerivesFrom"/> uses for base types.
    /// </summary>
    private static bool Implements(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type candidate,
        string expectedInterfaceFullName) =>
        candidate.GetInterfaces().Any(i => string.Equals(i.FullName, expectedInterfaceFullName, StringComparison.Ordinal));
}
