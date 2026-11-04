using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>One generated manifest file referenced by a host, with the host's system-app flag.</summary>
/// <param name="ManifestPath">Full path of the app project's generated manifest.</param>
/// <param name="IsSystemApp">The <c>IsSystemApp</c> metadata of the host's <c>ProjectReference</c>.</param>
/// <param name="AssemblyPath">The app's built assembly, measured for the image layer (ADR 0068); <see langword="null"/> when unknown.</param>
/// <param name="DependenciesFile">The app project's dependency list, to resolve the shared assemblies it maps.</param>
public sealed record AggregationInput(string ManifestPath, bool IsSystemApp, string? AssemblyPath = null, string? DependenciesFile = null);

/// <summary>Result of an aggregation: the document when valid, diagnostics otherwise.</summary>
/// <param name="Json">Canonical <c>BuiltInApplications.json</c>, or <see langword="null"/> on error.</param>
/// <param name="Diagnostics">Every problem found.</param>
/// <param name="TrimmerRoots">
/// The ILLink root descriptor of the same apps (ADR 0106 §2, <see cref="TrimmerRootDescriptorWriter"/>),
/// or <see langword="null"/> on error.
/// </param>
public sealed record AggregationResult(string? Json, IReadOnlyList<GeneratorDiagnostic> Diagnostics, string? TrimmerRoots = null);

/// <summary>
/// Builds a host's <c>BuiltInApplications.json</c> (ADR 0047 §3): re-reads and validates each
/// generated manifest, then builds the catalog with <see cref="AppCatalog.Build"/> so duplicate ids
/// and missing or cyclic dependencies fail the host build.
/// </summary>
public static class BuiltInApplicationsAggregator
{
    /// <summary>Aggregates <paramref name="inputs"/>.</summary>
    /// <param name="inputs">Manifest files from every referenced app project.</param>
    /// <returns>The canonical document or the diagnostics.</returns>
    public static AggregationResult Aggregate(IReadOnlyList<AggregationInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        List<GeneratorDiagnostic> diagnostics = [];
        List<BuiltInApplication> applications = [];
        foreach (AggregationInput input in inputs)
        {
            if (!File.Exists(input.ManifestPath))
            {
                diagnostics.Add(new(GeneratorCodes.UnreadableManifest, input.ManifestPath, "Manifest file not found; build the app project first."));
                continue;
            }

            if (ManifestFileReader.TryRead(input.ManifestPath, out AppManifest? manifest, out IReadOnlyList<GeneratorDiagnostic> readErrors))
            {
                // ADR 0104 section 4: the translated texts travel with the catalog, so the launcher
                // can show them before the app's assembly is ever loaded.
                IReadOnlyDictionary<string, AppManifestText> localized =
                    ManifestLocalizationGenerator.ReadReferenced(input.ManifestPath, manifest!, diagnostics);
                applications.Add(new BuiltInApplication(
                    input.IsSystemApp, manifest!, InspectImage(input), localized.Count > 0 ? localized : null));
            }
            else
            {
                diagnostics.AddRange(readErrors);
            }
        }

        if (diagnostics.Count > 0)
        {
            return new AggregationResult(null, diagnostics);
        }

        AppCatalogBuildResult catalog = AppCatalog.Build(applications.Select(item => item.Manifest));
        foreach (AppCatalogError error in catalog.Errors)
        {
            diagnostics.Add(new(GeneratorCodes.InvalidCatalog, error.AppId, $"{error.Message} ({error.Code})"));
        }

        // ADR 0098 §3: an app the host's own SDK would refuse at load fails the host build instead.
        foreach (AppCatalogRejection rejection in catalog.Catalog?.Rejected ?? [])
        {
            diagnostics.Add(new(GeneratorCodes.InvalidCatalog, rejection.AppId, $"{rejection.Message} ({rejection.Code})"));
        }

        if (diagnostics.Count == 0)
        {
            diagnostics.AddRange(FindDuplicateCommands(applications.Select(item => item.Manifest)));
            diagnostics.AddRange(FindNegativeOomScoreAdjust(applications));
            diagnostics.AddRange(FindInvalidContextMenuTargets(applications.Select(item => item.Manifest)));
        }

        return diagnostics.Count > 0
            ? new AggregationResult(null, diagnostics)
            : new AggregationResult(
                BuiltInApplicationsJsonSerializer.SerializeCanonical(applications),
                [],
                TrimmerRootDescriptorWriter.Build(applications.Select(item => item.Manifest)));
    }

    /// <summary>
    /// ADR 0098 §4, at build: every terminal command name and alias of the host's apps must be
    /// unique (case-insensitive) and must not be a name the shell runs itself
    /// (<see cref="Kazo.Os.Platform.Core.Shell.ShellInterpreter.ReservedNames"/>). One
    /// <c>HOSAPP0022</c> per offending app and name, in app id order.
    /// </summary>
    /// <param name="manifests">The host's manifests.</param>
    /// <returns>The diagnostics, empty when every name is unique.</returns>
    public static IReadOnlyList<GeneratorDiagnostic> FindDuplicateCommands(IEnumerable<AppManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        List<GeneratorDiagnostic> diagnostics = [];
        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        foreach (AppManifest manifest in manifests.Where(static item => item.Terminal is not null).OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            foreach (string name in manifest.Terminal!.Aliases.Prepend(manifest.Terminal.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Kazo.Os.Platform.Core.Shell.ShellInterpreter.ReservedNames.Contains(name))
                {
                    diagnostics.Add(new(GeneratorCodes.DuplicateCommand, manifest.Id, $"Command '{name}' is run by the shell itself (built-in); choose another name."));
                }
                else if (!owners.TryAdd(name, manifest.Id))
                {
                    diagnostics.Add(new(GeneratorCodes.DuplicateCommand, manifest.Id, $"Command '{name}' is already declared by '{owners[name]}'."));
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// ADR 0126, section 4, at build: a negative <c>oomScoreAdj</c> shields an app from the OOM killer,
    /// a right reserved to system apps. One <c>HOSAPP0024</c> per non-system app declaring one, in
    /// app id order.
    /// </summary>
    /// <param name="applications">The host's apps with their system flag.</param>
    /// <returns>The diagnostics, empty when no non-system app declares a negative value.</returns>
    public static IReadOnlyList<GeneratorDiagnostic> FindNegativeOomScoreAdjust(IEnumerable<BuiltInApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        return [.. applications
            .Where(static item => !item.IsSystemApp && item.Manifest.OomScoreAdj < 0)
            .OrderBy(static item => item.Manifest.Id, StringComparer.Ordinal)
            .Select(static item => new GeneratorDiagnostic(
                GeneratorCodes.NegativeOomScoreAdjust,
                item.Manifest.Id,
                $"oomScoreAdj {item.Manifest.OomScoreAdj} is negative: only a system app (IsSystemApp=\"true\" on the host's ProjectReference) may lower its OOM score."))];
    }

    /// <summary>
    /// ADR 0151 §5, at build: a file menu entry that targets another app needs that app in the host,
    /// declaring the entry's intent. One <c>HOSAPP0028</c> per offending entry, in app id order.
    /// </summary>
    /// <param name="manifests">The host's manifests.</param>
    /// <returns>The diagnostics, empty when every target is valid.</returns>
    public static IReadOnlyList<GeneratorDiagnostic> FindInvalidContextMenuTargets(IEnumerable<AppManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        AppManifest[] all = [.. manifests.OrderBy(static item => item.Id, StringComparer.Ordinal)];
        Dictionary<string, AppManifest> byId = all.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        List<GeneratorDiagnostic> diagnostics = [];
        foreach (AppManifest manifest in all)
        {
            foreach (AppContextMenuEntryManifest entry in manifest.ContextMenus ?? [])
            {
                if (entry.Target == AppContextMenus.SelfTarget || entry.Target == manifest.Id || entry.Intent is null)
                {
                    continue;
                }

                if (!byId.TryGetValue(entry.Target, out AppManifest? target))
                {
                    diagnostics.Add(new(GeneratorCodes.ContextMenuTarget, manifest.Id, $"Menu entry '{entry.Name}' targets '{entry.Target}', which this host does not have."));
                }
                else if (!target.Intents.Any(intent => string.Equals(intent.IntentId, entry.Intent, StringComparison.Ordinal)))
                {
                    diagnostics.Add(new(GeneratorCodes.ContextMenuTarget, manifest.Id, $"Menu entry '{entry.Name}' targets '{entry.Target}', which does not declare the intent '{entry.Intent}'."));
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Measures an app's image (ADR 0068) when its assembly is known; <see langword="null"/> otherwise,
    /// and the app's memory then has no image layer.
    /// </summary>
    private static AppImage? InspectImage(AggregationInput input)
    {
        if (input.AssemblyPath is not { Length: > 0 } assembly || !File.Exists(assembly))
        {
            return null;
        }

        IEnumerable<string> dependencies = input.DependenciesFile is { Length: > 0 } list && File.Exists(list)
            ? File.ReadAllLines(list)
            : [];
        return AppImageInspector.Inspect(assembly, dependencies);
    }

    /// <summary>
    /// Parses the list file written by the <c>CollectKazoOsBuiltInApplications</c> target: one
    /// <c>&lt;isSystemApp&gt;|&lt;manifest path&gt;[|&lt;assembly path&gt;|&lt;dependency list&gt;]</c>
    /// line per manifest (an empty flag means <c>false</c>; the last two fields, for the image layer of
    /// ADR 0068, are optional). Duplicate paths (a project referenced twice) collapse to one entry,
    /// system flag OR-ed.
    /// </summary>
    /// <param name="lines">File lines.</param>
    /// <returns>The inputs, ordered by path.</returns>
    public static IReadOnlyList<AggregationInput> ParseList(IEnumerable<string> lines)
    {
        Dictionary<string, AggregationInput> byPath = new(StringComparer.Ordinal);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int separator = line.IndexOf('|', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw new ArgumentException($"Invalid aggregation list line '{line}'. Expected '<isSystemApp>|<path>'.");
            }

            bool isSystem = string.Equals(line[..separator].Trim(), "true", StringComparison.OrdinalIgnoreCase);
            string[] fields = line[(separator + 1)..].Split('|');
            string path = Path.GetFullPath(fields[0].Trim());
            string? assembly = fields.Length > 1 && fields[1].Trim() is { Length: > 0 } a ? Path.GetFullPath(a) : null;
            string? dependencies = fields.Length > 2 && fields[2].Trim() is { Length: > 0 } d ? Path.GetFullPath(d) : null;
            byPath[path] = byPath.TryGetValue(path, out AggregationInput? existing)
                ? existing with
                {
                    IsSystemApp = existing.IsSystemApp || isSystem,
                    AssemblyPath = existing.AssemblyPath ?? assembly,
                    DependenciesFile = existing.DependenciesFile ?? dependencies
                }
                : new AggregationInput(path, isSystem, assembly, dependencies);
        }

        return [.. byPath.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)];
    }
}
