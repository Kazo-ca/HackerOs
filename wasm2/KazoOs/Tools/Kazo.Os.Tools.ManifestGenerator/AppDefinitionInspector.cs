using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>One manifest successfully produced from an <c>[AppManifest]</c> class.</summary>
/// <param name="DefinitionType">Full name of the definition class.</param>
/// <param name="Manifest">The produced manifest.</param>
/// <param name="CanonicalJson">The manifest's canonical JSON bytes (ADR 0010), as written to disk.</param>
public sealed record GeneratedManifest(string DefinitionType, AppManifest Manifest, string CanonicalJson)
{
    /// <summary>
    /// Gets the entry-point types of the definition (<c>EntryPoint</c>, then <c>MobileEntryPoint</c>
    /// when set). The SDK boundary check inspects their constructors (ADR 0055 §3).
    /// </summary>
    public IReadOnlyList<Type> EntryPointTypes { get; init; } = [];
}

/// <summary>Result of inspecting one app assembly: manifests when valid, diagnostics otherwise.</summary>
/// <param name="Manifests">Produced manifests, ordered by app id. Empty when any diagnostic exists.</param>
/// <param name="Diagnostics">Every problem found, in deterministic order.</param>
public sealed record InspectionResult(IReadOnlyList<GeneratedManifest> Manifests, IReadOnlyList<GeneratorDiagnostic> Diagnostics)
{
    /// <summary>Gets whether the assembly produced manifests with no error.</summary>
    public bool IsSuccess => Diagnostics.Count == 0;
}

/// <summary>
/// Applies the ADR 0047 §1 rules to every <c>[AppManifest]</c> class of an app assembly and maps the
/// valid ones to manifests. Never writes files: <see cref="ManifestWriter"/> does that, and only when
/// the whole assembly is valid (no partial manifest is ever written).
/// </summary>
public static class AppDefinitionInspector
{
    private const string AppManifestAttributeFullName = "Kazo.Os.App.Abstractions.AppManifestAttribute";
    private const string AbstractionsAssemblyName = "Kazo.Os.App.Abstractions";

    /// <summary>Inspects every type of <paramref name="assembly"/>.</summary>
    /// <param name="assembly">Compiled app assembly, typically loaded in an <see cref="AppAssemblyLoadContext"/>.</param>
    /// <returns>The manifests, or the diagnostics that must fail the build.</returns>
    public static InspectionResult Inspect(Assembly assembly) => Inspect(assembly, [], assetRoot: null);

    /// <summary>Inspects every type of <paramref name="assembly"/>, with the app's dependency list.</summary>
    /// <param name="assembly">Compiled app assembly, typically loaded in an <see cref="AppAssemblyLoadContext"/>.</param>
    /// <param name="dependencyPaths">The app's resolved dependencies, where the SDK version it was compiled against is read.</param>
    /// <param name="assetRoot">
    /// The folder declared asset paths are relative to (the app project's <c>wwwroot/</c>), whose
    /// files are hashed into <c>assets[].sha256</c> (ADR 0098 §5); <see langword="null"/> keeps the
    /// hashes the definition declares.
    /// </param>
    /// <returns>The manifests, or the diagnostics that must fail the build.</returns>
    public static InspectionResult Inspect(Assembly assembly, IEnumerable<string> dependencyPaths, string? assetRoot) =>
        Inspect(assembly, dependencyPaths, assetRoot, projectDirectory: null);

    /// <summary>Inspects every type of <paramref name="assembly"/>, with the app's dependency list and project directory.</summary>
    /// <param name="assembly">Compiled app assembly, typically loaded in an <see cref="AppAssemblyLoadContext"/>.</param>
    /// <param name="dependencyPaths">The app's resolved dependencies, where the SDK version it was compiled against is read.</param>
    /// <param name="assetRoot">Folder of the declared assets, hashed into the manifest; <see langword="null"/> keeps the declared hashes.</param>
    /// <param name="projectDirectory">
    /// The app project directory, whose <c>Resources/AppManifest*.resx</c> give the titles of the
    /// file menu entries (ADR 0159 §3); <see langword="null"/> makes each title key its English text.
    /// </param>
    /// <returns>The manifests, or the diagnostics that must fail the build.</returns>
    public static InspectionResult Inspect(Assembly assembly, IEnumerable<string> dependencyPaths, string? assetRoot, string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            string details = string.Join("; ", exception.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct());
            return Failure(new GeneratorDiagnostic(
                GeneratorCodes.AssemblyLoadFailed,
                assembly.GetName().Name ?? "?",
                $"The app assembly's types could not be loaded: {details}"));
        }

        return Inspect(assembly, types.Where(HasAppManifestAttribute), SdkMinimumVersionOf(assembly, dependencyPaths), assetRoot, projectDirectory);
    }

    /// <summary>
    /// Inspects an explicit set of definition types. Used by <see cref="Inspect(Assembly)"/> and by
    /// tests, which keep many deliberately broken definitions in one assembly.
    /// </summary>
    /// <param name="assembly">The assembly the definitions are expected to live in.</param>
    /// <param name="definitionTypes">Types marked <c>[AppManifest]</c>.</param>
    /// <param name="sdkMinimumVersion">Value written to <c>sdkCompatibility.minimumVersion</c>.</param>
    /// <param name="assetRoot">Folder of the declared assets, hashed into the manifest; <see langword="null"/> keeps the declared hashes.</param>
    /// <param name="projectDirectory">The app project directory for the menu titles (ADR 0159 §3); <see langword="null"/> makes each key its English text.</param>
    /// <returns>The manifests, or the diagnostics that must fail the build.</returns>
    public static InspectionResult Inspect(Assembly assembly, IEnumerable<Type> definitionTypes, string sdkMinimumVersion, string? assetRoot = null, string? projectDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(definitionTypes);

        Type[] candidates = [.. definitionTypes.OrderBy(type => type.FullName, StringComparer.Ordinal)];
        if (candidates.Length == 0)
        {
            return Failure(new GeneratorDiagnostic(
                GeneratorCodes.NoDefinition,
                assembly.GetName().Name ?? "?",
                "The app project contains no public class marked [AppManifest]. Add one deriving from AppDefinition (ADR 0047)."));
        }

        List<GeneratorDiagnostic> diagnostics = [];
        List<GeneratedManifest> manifests = [];
        foreach (Type type in candidates)
        {
            GeneratedManifest? manifest = InspectType(assembly, type, sdkMinimumVersion, assetRoot, projectDirectory, candidates.Length > 1, diagnostics);
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        foreach (IGrouping<string, GeneratedManifest> duplicate in manifests
            .GroupBy(item => item.Manifest.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            foreach (GeneratedManifest item in duplicate.Skip(1))
            {
                diagnostics.Add(new GeneratorDiagnostic(
                    GeneratorCodes.DuplicateIdInAssembly,
                    item.DefinitionType,
                    $"App id '{duplicate.Key}' is already declared by '{duplicate.First().DefinitionType}'."));
            }
        }

        return diagnostics.Count > 0
            ? new InspectionResult([], diagnostics)
            : new InspectionResult([.. manifests.OrderBy(item => item.Manifest.Id, StringComparer.Ordinal)], []);
    }

    /// <summary>
    /// Gets the App SDK version the assembly was compiled against (ADR 0098 §3), e.g.
    /// <c>1.0.0</c>: the informational version of the <c>Kazo.Os.App.Abstractions.dll</c>
    /// found first in <paramref name="candidatePaths"/> (the app's dependency list, as MSBuild
    /// resolved it), read from its metadata without loading it. The tool shares its own copy of that
    /// assembly with the app (one <c>AppDefinition</c> type identity), so the loaded copy can be a
    /// different version than the one the app references, from a package for instance. Falls back to
    /// the tool's own SDK version when no candidate is readable.
    /// </summary>
    /// <param name="assembly">Compiled app assembly.</param>
    /// <param name="candidatePaths">Dependency paths in priority order.</param>
    /// <returns>The SDK minimum version string.</returns>
    public static string SdkMinimumVersionOf(Assembly assembly, IEnumerable<string> candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(candidatePaths);
        foreach (string path in candidatePaths)
        {
            if (string.Equals(Path.GetFileName(path), AbstractionsAssemblyName + ".dll", StringComparison.OrdinalIgnoreCase)
                && File.Exists(path)
                && AssemblyMetadataReader.TryReadInformationalVersion(path, out string? informational, out Version? version))
            {
                return KazoOsSdk.NormalizeVersion(informational, version);
            }
        }

        return SdkMinimumVersionOf(assembly);
    }

    /// <summary>Gets the tool's own App SDK version, for an assembly inspected without a dependency list (tests).</summary>
    /// <param name="assembly">Compiled app assembly.</param>
    /// <returns>The SDK minimum version string.</returns>
    public static string SdkMinimumVersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return KazoOsSdk.ReadVersion(typeof(AppDefinition).Assembly);
    }

    private static GeneratedManifest? InspectType(
        Assembly assembly,
        Type type,
        string sdkMinimumVersion,
        string? assetRoot,
        string? projectDirectory,
        bool multi,
        List<GeneratorDiagnostic> diagnostics)
    {
        string subject = type.FullName ?? type.Name;
        int before = diagnostics.Count;

        if (!type.IsVisible)
        {
            diagnostics.Add(new(GeneratorCodes.NotPublic, subject, "An [AppManifest] class must be public."));
        }

        if (type.IsAbstract)
        {
            diagnostics.Add(new(GeneratorCodes.Abstract, subject, "An [AppManifest] class must not be abstract."));
        }

        if (!typeof(AppDefinition).IsAssignableFrom(type))
        {
            diagnostics.Add(new(GeneratorCodes.NotAnAppDefinition, subject, "An [AppManifest] class must derive from Kazo.Os.App.Abstractions.AppDefinition."));
        }

        if (type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is null)
        {
            diagnostics.Add(new(GeneratorCodes.NoParameterlessConstructor, subject, "An [AppManifest] class must have a public parameterless constructor."));
        }

        if (diagnostics.Count > before)
        {
            return null;
        }

        // Instantiate twice: pure getters give byte-identical canonical JSON (ADR 0047 §1).
        if (!TryProduce(type, sdkMinimumVersion, subject, diagnostics, out AppDefinition? first, out AppManifest? manifest, out string? json)
            || !TryProduce(type, sdkMinimumVersion, subject, diagnostics, out _, out _, out string? secondJson))
        {
            return null;
        }

        if (!string.Equals(json, secondJson, StringComparison.Ordinal))
        {
            diagnostics.Add(new(GeneratorCodes.NonDeterministic, subject,
                "Two instances produced different manifests. Getters must be pure: no service, clock, randomness or I/O."));
            return null;
        }

        if (assetRoot is not null && manifest!.Assets.Count > 0)
        {
            manifest = AssetHasher.Apply(manifest, assetRoot, subject, diagnostics);
            json = AppManifestJsonSerializer.SerializeCanonical(manifest);
        }

        // ADR 0151 §3, ADR 0159 §2: the registration method runs here, at build, never in the OS.
        if (ContextMenuRegistrationInspector.Run(type, subject, diagnostics) is { Count: > 0 } entries)
        {
            IReadOnlyList<AppContextMenuEntryManifest> titled = ContextMenuRegistrationInspector.ResolveTitles(
                entries, projectDirectory, manifest!.Id, multi, subject, diagnostics);
            manifest = manifest with { ContextMenus = titled };
            json = AppManifestJsonSerializer.SerializeCanonical(manifest);
        }

        if (diagnostics.Count > before)
        {
            return null;
        }

        CheckEntryPointAssembly(first!.EntryPoint, "EntryPoint", assembly, subject, diagnostics);
        if (first.MobileEntryPoint is Type mobile)
        {
            CheckEntryPointAssembly(mobile, "MobileEntryPoint", assembly, subject, diagnostics);
        }

        if (diagnostics.Count > before)
        {
            return null;
        }

        foreach (ManifestValidationError error in AppManifestValidator.Validate(manifest!).Errors)
        {
            // ADR 0073, section 3: a site asking for the file system has its own code.
            string code = error.Code switch
            {
                "manifest.web.filesystem" => GeneratorCodes.WebFileSystem,
                // ADR 0151 §6: entries without the capability have their own code.
                "manifest.context-menu.capability" => GeneratorCodes.ContextMenuCapability,
                _ when error.Code.StartsWith("manifest.context-menu.", StringComparison.Ordinal) => GeneratorCodes.InvalidContextMenu,
                _ => GeneratorCodes.InvalidManifest
            };
            diagnostics.Add(new(code, subject, $"{error.Path}: {error.Message} ({error.Code})"));
        }

        if (diagnostics.Count > before)
        {
            return null;
        }

        CheckEntryPointKinds(manifest!, assembly, subject, diagnostics);
        return diagnostics.Count > before
            ? null
            : new GeneratedManifest(subject, manifest!, json!)
            {
                EntryPointTypes = first.MobileEntryPoint is Type mobileEntryPoint ? [first.EntryPoint, mobileEntryPoint] : [first.EntryPoint]
            };
    }

    private static bool TryProduce(
        Type type,
        string sdkMinimumVersion,
        string subject,
        List<GeneratorDiagnostic> diagnostics,
        out AppDefinition? definition,
        out AppManifest? manifest,
        out string? json)
    {
        definition = null;
        manifest = null;
        json = null;
        try
        {
            definition = (AppDefinition)Activator.CreateInstance(type)!;
            manifest = AppDefinitionMapper.ToManifest(definition, sdkMinimumVersion);
            json = AppManifestJsonSerializer.SerializeCanonical(manifest);
            return true;
        }
        catch (Exception exception)
        {
            Exception root = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            diagnostics.Add(new(GeneratorCodes.DefinitionThrew, subject,
                $"The constructor or a getter threw {root.GetType().Name}: {root.Message}"));
            return false;
        }
    }

    private static void CheckEntryPointAssembly(Type? entryPoint, string property, Assembly assembly, string subject, List<GeneratorDiagnostic> diagnostics)
    {
        if (entryPoint is null)
        {
            diagnostics.Add(new(GeneratorCodes.EntryPointAssembly, subject, $"{property} must not be null."));
        }
        else if (entryPoint.Assembly != assembly)
        {
            diagnostics.Add(new(GeneratorCodes.EntryPointAssembly, subject,
                $"{property} '{entryPoint.FullName}' lives in '{entryPoint.Assembly.GetName().Name}', not in '{assembly.GetName().Name}'. " +
                "Lazy loading loads one DLL per app, so the entry point must be in the definition's assembly."));
        }
    }

    /// <summary>
    /// Runs the runtime <see cref="AppEntryPointDiscovery"/> rules (concrete class, base type matching
    /// the kind, back handler) for every platform the manifest supports, so a build only succeeds
    /// when the host will be able to activate the app.
    /// </summary>
    private static void CheckEntryPointKinds(AppManifest manifest, Assembly assembly, string subject, List<GeneratorDiagnostic> diagnostics)
    {
        // Dependencies and the SDK range are checked by the host's aggregation, where every app and the
        // host's SDK are known; here the catalog runs the app's own minimum so discovery always sees it.
        AppCatalogBuildResult catalogResult = AppCatalog.Build(
            [manifest with { Dependencies = [] }],
            [],
            SemanticVersion.TryParse(manifest.SdkCompatibility.MinimumVersion, out SemanticVersion minimum) ? minimum : KazoOsSdk.SemanticVersion);
        if (catalogResult.Catalog is not AppCatalog catalog)
        {
            foreach (AppCatalogError error in catalogResult.Errors)
            {
                diagnostics.Add(new(GeneratorCodes.InvalidManifest, subject, error.Message));
            }

            return;
        }

        AppManifestPlatformResolution? platforms = AppManifestPlatformSupport.Resolve(manifest);
        Dictionary<string, Assembly> hostAssemblies = new(StringComparer.Ordinal)
        {
            [assembly.GetName().Name + ".dll"] = assembly
        };
        foreach (AppPlatformId platform in platforms?.SupportedPlatforms ?? [])
        {
            AppDiscoveryResult discovery = AppEntryPointDiscovery.Discover(catalog, hostAssemblies, platform);
            foreach (AppDiscoveryError error in discovery.Errors)
            {
                diagnostics.Add(new(GeneratorCodes.EntryPointKind, subject, $"{platform}: {error.Message} ({error.Code})"));
            }
        }
    }

    private static bool HasAppManifestAttribute(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            string.Equals(attribute.AttributeType.FullName, AppManifestAttributeFullName, StringComparison.Ordinal));

    private static InspectionResult Failure(GeneratorDiagnostic diagnostic) => new([], [diagnostic]);
}
