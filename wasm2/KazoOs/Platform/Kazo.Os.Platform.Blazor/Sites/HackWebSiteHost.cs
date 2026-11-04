using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Web.Blazor.Sites;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Platform.Blazor.Sites;

/// <summary>
/// Starts the renderer of a Blazor site for the OS browser (ADR 0084, section 3). Finds the
/// installed web app that declares the page's origin in <c>web.urls</c>, refuses a boot manifest
/// naming another app, lazy-loads the app's assembly as a launch would, checks the root component,
/// then starts an auxiliary process under the tab's user as a child of the browser's process.
/// </summary>
public sealed class HackWebSiteHost : IHackWebSiteHost
{
    /// <summary>Starts the auxiliary process (<c>AppLifecycleOrchestrator.StartAuxiliaryAsync</c>).</summary>
    public delegate Task<AppLaunchResult> StartRenderer(
        string appId, AuthenticatedPrincipal principal, ProcessId parentPid, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>The <c>argv</c> flag of a renderer process, as <c>chrome --type=renderer</c>.</summary>
    public const string RendererArgument = "--type=renderer";

    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly ISessionService _sessions;
    private readonly Func<string, CancellationToken, Task<(AppDescriptor? Descriptor, string? Error)>> _descriptors;
    private readonly StartRenderer _start;
    private readonly Func<ProcessId, ProcessExitReason, Task<bool>> _stop;

    /// <summary>Initializes the host over the OS catalog and lifecycle.</summary>
    /// <param name="catalog">Build-known apps and their manifests.</param>
    /// <param name="enablement">Installed state: an uninstalled site is not served.</param>
    /// <param name="sessions">The session whose user the renderer runs as.</param>
    /// <param name="descriptors">Gets an app's descriptor, lazy-loading its assembly (<c>AppLifecycleOrchestrator.EnsureDescriptorAsync</c>).</param>
    /// <param name="start">Starts the renderer process (<c>AppLifecycleOrchestrator.StartAuxiliaryAsync</c>).</param>
    /// <param name="stop">Stops a process (<c>AppLifecycleOrchestrator.StopAsync</c>).</param>
    public HackWebSiteHost(
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        ISessionService sessions,
        Func<string, CancellationToken, Task<(AppDescriptor? Descriptor, string? Error)>> descriptors,
        StartRenderer start,
        Func<ProcessId, ProcessExitReason, Task<bool>> stop)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
    }

    /// <summary>
    /// Finds the installed web app serving <paramref name="pageUrl"/>: the one whose binding has the
    /// page's scheme, host and port and the longest path prefix.
    /// </summary>
    /// <param name="pageUrl">The page's absolute URL.</param>
    public AppManifest? FindSite(Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(pageUrl);
        AppManifest? best = null;
        int bestPath = -1;
        foreach (AppManifest manifest in _catalog.Manifests.Values)
        {
            if (manifest.Kind != AppKind.Web || manifest.Web is null || !_enablement.IsEnabled(manifest.Id))
            {
                continue;
            }

            foreach (string url in manifest.Web.Urls)
            {
                if (!WebAppUrls.TryParse(url, allowWildcardHost: true, out WebBinding binding, out _)
                    || !string.Equals(binding.Scheme, pageUrl.Scheme, StringComparison.OrdinalIgnoreCase)
                    || binding.Port != pageUrl.Port
                    || (!binding.IsWildcard && !string.Equals(binding.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string prefix = binding.Path.EndsWith('/') ? binding.Path : binding.Path + "/";
                string path = pageUrl.AbsolutePath.EndsWith('/') ? pageUrl.AbsolutePath : pageUrl.AbsolutePath + "/";
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > bestPath)
                {
                    best = manifest;
                    bestPath = prefix.Length;
                }
            }
        }

        return best;
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Root components come from build-known manifests whose assemblies a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "Root components come from build-known manifests whose assemblies a trimmed host keeps whole (ADR 0113: generated root descriptor).")]
    public async Task<HackWebSiteStart> StartAsync(HackWebSiteStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AppManifest? site = FindSite(request.PageUrl);
        if (site is null)
        {
            return HackWebSiteStart.Failed($"No installed site serves {request.PageUrl.GetLeftPart(UriPartial.Authority)}.");
        }

        // A page may only start the code of the site that serves it (ADR 0084 §3).
        if (!string.Equals(site.Id, request.Boot.AppId, StringComparison.Ordinal))
        {
            return HackWebSiteStart.Failed($"blazor.boot.json names '{request.Boot.AppId}', but {request.PageUrl.Host} is served by '{site.Id}'.");
        }

        if (site.Web?.Blazor is not { } blazor)
        {
            return HackWebSiteStart.Failed($"The site '{site.Id}' declares no Blazor root component.");
        }

        if (_sessions.CurrentPrincipal is not { } principal)
        {
            return HackWebSiteStart.Failed("No user session is open.");
        }

        (AppDescriptor? descriptor, string? error) = await _descriptors(site.Id, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return HackWebSiteStart.Failed(error ?? $"The site '{site.Id}' could not be loaded.");
        }

        // The manifest validator keeps the root in the entry-point assembly, the one lazy loading fetched.
        Type? root = descriptor.Assembly.GetType(blazor.Type, throwOnError: false);
        if (root is null || root.IsAbstract || !typeof(IComponent).IsAssignableFrom(root))
        {
            return HackWebSiteStart.Failed($"The site '{site.Id}' declares '{blazor.Type}', which is not a Blazor component.");
        }

        AppLaunchResult launch = await _start(
            site.Id, principal, request.Browser.ProcessId, [RendererArgument, request.PageUrl.AbsoluteUri], cancellationToken)
            .ConfigureAwait(false);
        if (launch.Status != AppLaunchStatus.Launched || launch.Context is null)
        {
            return HackWebSiteStart.Failed(launch.ErrorMessage ?? launch.ErrorCode ?? $"The renderer of '{site.Id}' did not start ({launch.Status}).");
        }

        return HackWebSiteStart.Started(new RendererProcess(site, root, launch.Context, _stop));
    }

    /// <summary>The renderer process handed to the browser: ends with the page, or when the OS stops it.</summary>
    private sealed class RendererProcess : IHackWebSiteProcess
    {
        private readonly Func<ProcessId, ProcessExitReason, Task<bool>> _stop;
        private readonly CancellationTokenRegistration _exit;
        private int _exited;

        public RendererProcess(
            AppManifest site,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type root,
            IAppExecutionContext context,
            Func<ProcessId, ProcessExitReason, Task<bool>> stop)
        {
            AppId = site.Id;
            AppName = site.Name;
            RootComponent = root;
            Context = context;
            _stop = stop;
            // The OS ended the process (task manager, kill, logout): the context's token is cancelled.
            _exit = context.CancellationToken.Register(OnExited);
        }

        public string AppId { get; }

        public string AppName { get; }

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
        public Type RootComponent { get; }

        public IAppExecutionContext Context { get; }

        public ProcessId ProcessId => Context.ProcessId;

        public bool HasExited => Volatile.Read(ref _exited) == 1;

        public event Action? Exited;

        public async Task StopAsync(bool faulted = false)
        {
            if (Interlocked.Exchange(ref _exited, 1) == 1)
            {
                return;
            }

            _exit.Dispose();
            await _stop(ProcessId, faulted ? ProcessExitReason.Fault : ProcessExitReason.CloseRequested).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => new(StopAsync());

        private void OnExited()
        {
            if (Interlocked.Exchange(ref _exited, 1) == 1)
            {
                return;
            }

            Exited?.Invoke();
        }
    }
}
