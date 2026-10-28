using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using HackerOs.App.Abstractions;
using HackerOs.Infrastructure.Browser.Isolation;
using HackerOs.Infrastructure.Browser.Workers;
using HackerOs.Platform.Blazor.Hosting;
using HackerOs.Platform.Blazor.LazyLoading;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Isolation;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Platform.RemoteRendering;
using HackerOs.Platform.RemoteRendering.Host;
using HackerOs.Platform.WorkerHost;
using HackerOs.Simulation.Abstractions.Workers;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HackerOs.Ecosystem;

/// <summary>Options of <see cref="HackerOsBrowserHostExtensions.AddHackerOsBrowserHost"/>.</summary>
public sealed record HackerOsBrowserHostOptions
{
    /// <summary>
    /// Gets the app assemblies every Web Worker loads while it warms up (ADR 0070/0071), so the first
    /// command run in a worker does not wait for them. Default: the built-in commands
    /// (<c>HackerOs.Commands.dll</c>) when the host ships them, nothing otherwise.
    /// </summary>
    public IReadOnlyList<string>? WorkerPreloadAssemblies { get; init; }

    /// <summary>
    /// Gets the CSS selector of the element the desktop mounts in (default <c>#app</c>). When the page
    /// has no such element (a Blazor Web App host mounting the tree through its own markers), no
    /// root component is registered.
    /// </summary>
    public string RootSelector { get; init; } = "#app";
}

/// <summary>
/// The composition root of a standalone HackerOS host running in the browser (ADR 0098 §2): what
/// <c>OS/HackerOs.Ecosystem/Program.cs</c> did before the host was a package. A host's
/// <c>Program.cs</c> is then:
/// <code>
/// var builder = WebAssemblyHostBuilder.CreateDefault(args);
/// builder.AddHackerOsBrowserHost(HostAppCatalog.Load(typeof(Program).Assembly));
/// await builder.Build().RunAsync();
/// </code>
/// </summary>
public static partial class HackerOsBrowserHostExtensions
{
    /// <summary>
    /// Registers the desktop root component, the lazy app loader over the host's catalog, resource
    /// accounting (from <c>HackerOs:ResourceAccounting</c> in <c>appsettings.json</c>), every OS
    /// service (<see cref="EcosystemServiceCollectionExtensions.AddHackerOsEcosystem"/>), app asset
    /// integrity checks over the page's <c>_content/</c> (ADR 0098 §5) and the Web Worker pool.
    /// </summary>
    /// <param name="builder">The WebAssembly host builder.</param>
    /// <param name="apps">The apps the host was built with (<see cref="HostAppCatalog.Load"/>).</param>
    /// <param name="options">Optional settings; <see langword="null"/> uses the defaults.</param>
    /// <returns>The same builder.</returns>
    public static WebAssemblyHostBuilder AddHackerOsBrowserHost(
        this WebAssemblyHostBuilder builder, HostAppCatalog apps, HackerOsBrowserHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(apps);
        options ??= new HackerOsBrowserHostOptions();

        // The same bundle boots two ways: as a standalone page whose index.html has the root element,
        // and as the interactive WebAssembly island of a Blazor Web App host (test/test), which mounts
        // the tree through server-rendered markers and has no such element. Registering a root for a
        // selector that does not exist there aborts the whole boot, so only register it when present.
        if (!OperatingSystem.IsBrowser() || RootElementExists(options.RootSelector))
        {
            builder.RootComponents.Add<App>(options.RootSelector);
            builder.RootComponents.Add<HeadOutlet>("head::after");
        }

        builder.Services.AddSingleton<IEcosystemHostEnvironment, WebAssemblyEcosystemHostEnvironment>();
        // ADR 0182 §5: the apps installed from packages join the catalog once, before the OS starts
        // (Program.cs calls LoadInstalledAppsAsync); everything below reads the final catalog.
        InstalledAppsBoot installedApps = new(apps);
        builder.Services.AddSingleton(installedApps);
        builder.Services.AddSingleton<WebAssemblyLazyAssemblyTransport>();
        builder.Services.AddSingleton<IBuildKnownAssemblyTransport>(provider => new InstalledAppAssemblyTransport(
            provider.GetRequiredService<WebAssemblyLazyAssemblyTransport>(), installedApps));
        // ADR 0162: an app asset that fails to load after a rebuild or a deployment means the OS
        // was updated under the page; the host then offers a restart instead of a dead launch.
        builder.Services.AddSingleton<IHostBuildProbe, BrowserHostBuildProbe>();
        builder.Services.AddSingleton(provider => new HostBuildUpdateMonitor(provider.GetRequiredService<IHostBuildProbe>()));
        // ADR 0174: every five minutes, the OS reads the version.json its origin serves and shows
        // a newer release next to the restart offer.
        builder.Services.AddSingleton<IHostReleaseProbe>(new BrowserHostReleaseProbe(
            new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }));
        builder.Services.AddSingleton(provider => new HostReleaseMonitor(
            provider.GetRequiredService<IHostReleaseProbe>(),
            HackerOsSdk.Version,
            provider.GetRequiredService<HostBuildUpdateMonitor>()));
        builder.Services.AddSingleton(provider => new BuildKnownAssemblyLoaderRegistry(
            installedApps.Current.LazyAssemblyNames,
            provider.GetRequiredService<IBuildKnownAssemblyTransport>(),
            provider.GetRequiredService<HostBuildUpdateMonitor>()));
        builder.Services.AddSingleton(provider => new BuildKnownLazyAppDescriptorRegistry(
            installedApps.Current.Catalog,
            provider.GetRequiredService<BuildKnownAssemblyLoaderRegistry>()));
        // ADR 0098 §5: app assets are the static web assets of their package, checked at first read.
        builder.Services.AddSingleton<IAppAssetSource>(new InstalledAppAssetSource(
            new StaticWebAssetAppAssetSource(new Uri(builder.HostEnvironment.BaseAddress)), installedApps));
        // ADR 0134, ADR 0137: the OS asks its own origin whether a HackerOS server served it.
        Uri ownOrigin = new(builder.HostEnvironment.BaseAddress);
        builder.Services.AddScoped<IHostInfoClient>(provider => new HttpHostInfoClient(provider.GetRequiredService<HttpClient>(), ownOrigin));
        // ADR 0066: decided once from appsettings.json and what the build compiled in. Must precede
        // AddHackerOsEcosystem, and runs before any app code so the context-switch hook can be installed.
        builder.Services.AddHackerOsResourceAccounting(builder.Configuration, apps.Images);
        // ADR 0125: process limits (ulimit, limits.conf), on unless HackerOs:ResourceLimits:Enabled is false.
        builder.Services.AddHackerOsResourceLimits(builder.Configuration);
        builder.Services.AddHackerOsHardware(builder.Configuration);
        builder.Services.AddHackerOsEcosystem(
            apps.Catalog,
            provider => provider.GetRequiredService<BuildKnownLazyAppDescriptorRegistry>().Descriptors,
            provider => provider.GetRequiredService<BuildKnownLazyAppDescriptorRegistry>());
        builder.Services.Replace(ServiceDescriptor.Singleton<AppCatalog>(_ => installedApps.Current.Catalog));

        // ADR 0070/0071: commands and services run in Web Workers; the pool warms up once the desktop
        // is shown. The JavaScript ships in HackerOs.Infrastructure.Browser's wwwroot, imported by
        // BrowserWebWorkerRuntime itself. Every host runs the OS in the browser since ADR 0130.
        if (OperatingSystem.IsBrowser())
        {
            IReadOnlyList<string> preload = options.WorkerPreloadAssemblies
                ?? (apps.LazyAssemblyNames.Contains("HackerOs.Commands.dll", StringComparer.Ordinal) ? ["HackerOs.Commands.dll"] : []);
            // ADR 0131: the page says at boot whether it is cross-origin isolated
            // (HackerOs:CrossOriginIsolation: auto, on or off); the workers' JavaScript uses shared
            // rings and synchronous calls by itself when it is.
            CrossOriginIsolationMode isolationMode = CrossOriginIsolationPolicy.Parse(builder.Configuration[CrossOriginIsolationPolicy.SettingKey]);
            builder.Services.AddSingleton<CrossOriginIsolationState>();
            builder.Services.AddSingleton(provider => new CrossOriginIsolationProbe(
                provider.GetRequiredService<CrossOriginIsolationState>(),
                () => OperatingSystem.IsBrowser()
                    ? ProbeCrossOriginIsolationAsync(isolationMode)
                    : Task.FromResult(CrossOriginIsolationStatus.NotBrowser)));
            builder.Services.AddSingleton<IWebWorkerRuntime, BrowserWebWorkerRuntime>();
            // ADR 0124, section 4: a worker loads any app assembly known to this build on demand.
            builder.Services.AddHackerOsWorkerProcesses(workerOptions => workerOptions with
            {
                PreloadAssemblies = [.. preload],
                KnownAssemblies = [.. apps.LazyAssemblyNames]
            });
            // ADR 0129 trial, ADR 0133: window apps rendered in a worker, off unless
            // HackerOs:RemoteWindowRendering:Enabled is true in appsettings.json.
            builder.Services.AddHackerOsRemoteWindowRendering(ReadRemoteWindowRendering(builder.Configuration));
        }

        return builder;
    }

    /// <summary>Reads <c>HackerOs:RemoteWindowRendering</c> (ADR 0133): <c>Enabled</c>, and optionally <c>Apps</c>.</summary>
    private static RemoteWindowRenderingOptions ReadRemoteWindowRendering(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(RemoteWindowRenderingOptions.SectionName);
        RemoteWindowRenderingOptions options = new()
        {
            Enabled = bool.TryParse(section["Enabled"], out bool enabled) && enabled
        };
        string[] listed = [.. section.GetSection("Apps").GetChildren().Select(static app => app.Value).OfType<string>()];
        return listed.Length > 0 ? options with { Apps = listed } : options;
    }

    /// <summary>Asks the page whether it is cross-origin isolated (ADR 0131) and reads its answer.</summary>
    [SupportedOSPlatform("browser")]
    private static async Task<CrossOriginIsolationStatus> ProbeCrossOriginIsolationAsync(CrossOriginIsolationMode mode) =>
        CrossOriginIsolationProbe.Read(
            mode,
            await BrowserCrossOriginIsolation.ProbeAsync(CrossOriginIsolationPolicy.SendsHeaders(mode)).ConfigureAwait(true));

    [SupportedOSPlatform("browser")]
    private static bool RootElementExists(string selector) => QuerySelector(selector) is not null;

    [JSImport("globalThis.document.querySelector")]
    [SupportedOSPlatform("browser")]
    private static partial JSObject? QuerySelector(string selector);
}
