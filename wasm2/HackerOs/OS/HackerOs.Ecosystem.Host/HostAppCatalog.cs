using System.Reflection;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Blazor.LazyLoading;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Discovery;

namespace HackerOs.Ecosystem;

/// <summary>
/// The apps a host was built with (ADR 0047 §3, ADR 0098 §2): the
/// <c>HackerOs.BuiltInApplications.json</c> resource the build aggregates into the host project's own
/// assembly (<c>HackerOsAppHost</c>) from every app it references, as a project or as a package.
/// </summary>
/// <remarks>
/// Only JSON is read: app assemblies stay lazy and their <c>AppDefinition</c> classes are never
/// instantiated at run time. Like <c>/var/lib/dpkg/status</c>, it is the list of what the image
/// shipped with, decided at build time, never at run time.
/// </remarks>
public sealed class HostAppCatalog
{
    private readonly IReadOnlyList<BuiltInApplication> _applications;

    private HostAppCatalog(IReadOnlyList<BuiltInApplication> applications)
        : this(applications, AppCatalog.BuildFromBuiltInApplications(applications), [])
    {
    }

    private HostAppCatalog(IReadOnlyList<BuiltInApplication> applications, AppCatalogBuildResult result, IReadOnlyList<InstalledApp> installed)
    {
        if (applications.Count == 0)
        {
            throw new InvalidOperationException("The build contains no embedded app manifests.");
        }

        _applications = applications;
        Installed = installed;
        Catalog = result.Catalog ?? throw new InvalidOperationException(
            $"The host's app catalog is invalid: {string.Join("; ", result.Errors.Select(error => error.Message))}");
        Images = applications
            .Where(static application => application.Image is not null)
            .ToDictionary(static application => application.Manifest.Id, static application => application.Image!, StringComparer.Ordinal);
        LazyAssemblyNames =
        [.. Catalog.Manifests.Values
            .Select(manifest => AppPlatformEntryPointResolver.Instance.Resolve(manifest, WellKnownAppPlatforms.Desktop))
            .Where(resolution => resolution.IsResolved)
            .Select(resolution => resolution.EntryPoint!.Assembly)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Gets the apps installed from packages that joined the catalog at boot (ADR 0182 §5); empty
    /// for the catalog the host was built with.
    /// </summary>
    public IReadOnlyList<InstalledApp> Installed { get; }

    /// <summary>Gets the validated, immutable catalog (system apps per ADR 0048 included).</summary>
    public AppCatalog Catalog { get; }

    /// <summary>
    /// Gets the executable image of each app, by app id: the assembly sizes the host build measured
    /// (ADR 0068, image layer). An app missing here has no image layer.
    /// </summary>
    public IReadOnlyDictionary<string, AppImage> Images { get; }

    /// <summary>
    /// Gets the exact app assemblies the catalog declares, resolved for the Desktop platform (see
    /// <see cref="BuildKnownLazyAppDescriptorRegistry"/>'s matching default): the only names the
    /// lazy loader will ever fetch.
    /// </summary>
    public IReadOnlyList<string> LazyAssemblyNames { get; }

    /// <summary>
    /// Gets this host's catalog plus the apps installed from packages (ADR 0182 §5, Q5 answer C). An
    /// installed app the catalog refuses (an ID taken by a built-in app, an SDK it does not run, a
    /// conflict) is left out with a warning; built-in apps are never affected.
    /// </summary>
    /// <param name="image">The installed apps read at boot.</param>
    /// <param name="warnings">Receives one line per installed app left out.</param>
    /// <returns>The combined catalog, or this one when nothing joins.</returns>
    public HostAppCatalog WithInstalledApps(InstalledAppsImage image, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(image);
        List<string> skipped = [];
        List<InstalledApp> candidates = [];
        foreach (InstalledApp app in image.Apps)
        {
            if (Catalog.Manifests.ContainsKey(app.AppId))
            {
                skipped.Add($"apps: installed app {app.AppId} has the ID of an app built into this system, app skipped");
            }
            else
            {
                candidates.Add(app);
            }
        }

        while (candidates.Count > 0)
        {
            AppCatalogBuildResult result = AppCatalog.BuildFromBuiltInApplications(
                [.. _applications, .. candidates.Select(static app => new BuiltInApplication(false, app.Manifest))]);
            HashSet<string> refused = new(StringComparer.Ordinal);
            foreach (AppCatalogError error in result.Errors.Where(error => candidates.Any(app => app.AppId == error.AppId)))
            {
                refused.Add(error.AppId);
                skipped.Add($"apps: installed app {error.AppId} refused by the catalog ({error.Message}), app skipped");
            }

            foreach (AppCatalogRejection rejection in result.Catalog?.Rejected.Where(rejection => candidates.Any(app => app.AppId == rejection.AppId)) ?? [])
            {
                skipped.Add($"apps: installed app {rejection.AppId} cannot run here ({rejection.Message}), app skipped");
            }

            if (result.IsSuccess && refused.Count == 0)
            {
                warnings = skipped;
                HashSet<string> accepted = [.. result.Catalog!.Manifests.Keys];
                return new HostAppCatalog(_applications, result, [.. candidates.Where(app => accepted.Contains(app.AppId))]);
            }

            if (refused.Count == 0)
            {
                // An error that names no installed app: keep the host's own catalog untouched.
                skipped.AddRange(candidates.Select(static app => $"apps: installed app {app.AppId} skipped, the combined catalog is invalid"));
                break;
            }

            candidates.RemoveAll(app => refused.Contains(app.AppId));
        }

        warnings = skipped;
        return this;
    }

    /// <summary>Reads the catalog embedded in a host assembly.</summary>
    /// <param name="hostAssembly">The host project's assembly (for example <c>typeof(Program).Assembly</c>).</param>
    /// <returns>The host's apps.</returns>
    /// <exception cref="InvalidOperationException">
    /// The assembly embeds no catalog (its project does not set <c>HackerOsAppHost</c>), or the catalog is invalid.
    /// </exception>
    public static HostAppCatalog Load(Assembly hostAssembly)
    {
        ArgumentNullException.ThrowIfNull(hostAssembly);
        using Stream builtIn = hostAssembly.GetManifestResourceStream(BuiltInApplicationsJsonSerializer.ResourceName)
            ?? throw new InvalidOperationException(
                $"'{hostAssembly.GetName().Name}' does not embed '{BuiltInApplicationsJsonSerializer.ResourceName}': set HackerOsAppHost to true in its project (ADR 0047).");
        using StreamReader reader = new(builtIn);
        return new HostAppCatalog(BuiltInApplicationsJsonSerializer.DeserializeStrict(reader.ReadToEnd()));
    }
}
