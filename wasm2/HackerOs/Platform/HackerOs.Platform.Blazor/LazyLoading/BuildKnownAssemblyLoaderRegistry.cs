using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace HackerOs.Platform.Blazor.LazyLoading;

/// <summary>Loads only assemblies declared by the build and coalesces concurrent requests.</summary>
public sealed class BuildKnownAssemblyLoaderRegistry
{
    private readonly Lock _knownGate = new();
    private IReadOnlySet<string> _knownAssemblies;
    private readonly IBuildKnownAssemblyTransport _transport;
    private readonly HostBuildUpdateMonitor? _buildMonitor;
    private readonly ConcurrentDictionary<string, Lazy<Task<BuildKnownAssemblyLoadOutcome>>> _loads = new(StringComparer.Ordinal);

    /// <summary>Creates the registry for the lazy-loadable assemblies the build declared.</summary>
    /// <param name="knownAssemblies">The file names (e.g. <c>HackerOs.Apps.Foo.dll</c>) the build published as lazy assets; any other name is refused.</param>
    /// <param name="transport">The browser transport that actually fetches and loads the assemblies.</param>
    public BuildKnownAssemblyLoaderRegistry(IEnumerable<string> knownAssemblies, IBuildKnownAssemblyTransport transport)
    {
        ArgumentNullException.ThrowIfNull(knownAssemblies);
        _knownAssemblies = new HashSet<string>(knownAssemblies, StringComparer.Ordinal);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Creates the registry and, when a load fails, asks <paramref name="buildMonitor"/> whether the
    /// OS was updated under the page (ADR 0162): the asset of the running build is then gone.
    /// </summary>
    /// <param name="knownAssemblies">The file names the build published as lazy assets; any other name is refused.</param>
    /// <param name="transport">The browser transport that actually fetches and loads the assemblies.</param>
    /// <param name="buildMonitor">Compares the running build with the one the origin serves now.</param>
    public BuildKnownAssemblyLoaderRegistry(
        IEnumerable<string> knownAssemblies,
        IBuildKnownAssemblyTransport transport,
        HostBuildUpdateMonitor buildMonitor)
        : this(knownAssemblies, transport)
    {
        _buildMonitor = buildMonitor ?? throw new ArgumentNullException(nameof(buildMonitor));
    }

    /// <summary>
    /// Declares more loadable assemblies: those of apps installed from packages after the OS started
    /// (ADR 0214 §1). A name already declared stays declared; nothing is ever withdrawn, since a
    /// loaded assembly stays in the page until it restarts.
    /// </summary>
    /// <param name="assemblyNames">File names such as <c>Org.Example.Notes.dll</c>.</param>
    public void AddKnownAssemblies(IEnumerable<string> assemblyNames)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);
        lock (_knownGate)
        {
            HashSet<string> known = new(_knownAssemblies, StringComparer.Ordinal);
            known.UnionWith(assemblyNames.Where(static name => !string.IsNullOrWhiteSpace(name)));
            Volatile.Write(ref _knownAssemblies, known);
        }
    }

    /// <summary>Loads one declared optional assembly at most once for the host lifetime.</summary>
    [RequiresUnreferencedCode("Lazy-loaded assemblies require their own trim-safe descriptors.")]
    public async Task<BuildKnownAssemblyLoadOutcome> LoadAsync(string assemblyName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assemblyName) || !Volatile.Read(ref _knownAssemblies).Contains(assemblyName))
            return BuildKnownAssemblyLoadOutcome.NotDeclared(assemblyName);

        Task<BuildKnownAssemblyLoadOutcome> sharedLoad = _loads.GetOrAdd(assemblyName, name => new Lazy<Task<BuildKnownAssemblyLoadOutcome>>(
            () => LoadDeclaredAsync(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        try
        {
            // Cancelling one launch must not cancel the single shared browser fetch;
            // another concurrent launcher may still need the declared asset.
            return await sharedLoad.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BuildKnownAssemblyLoadOutcome.Cancelled(assemblyName);
        }
    }

    [RequiresUnreferencedCode("Lazy-loaded assemblies require their own trim-safe descriptors.")]
    private async Task<BuildKnownAssemblyLoadOutcome> LoadDeclaredAsync(string assemblyName)
    {
        try
        {
            IReadOnlyList<Assembly> assemblies = await _transport.LoadAsync([assemblyName], CancellationToken.None);
            Assembly? assembly = assemblies.SingleOrDefault(item => string.Equals(item.GetName().Name + ".dll", assemblyName, StringComparison.Ordinal))
                ?? assemblies.SingleOrDefault();
            return assembly is null
                ? BuildKnownAssemblyLoadOutcome.Missing(assemblyName)
                : BuildKnownAssemblyLoadOutcome.Loaded(assemblyName, assembly);
        }
        catch (OperationCanceledException) { return BuildKnownAssemblyLoadOutcome.Cancelled(assemblyName); }
        catch (Exception exception)
        {
            // A rebuild or a deployment replaced the fingerprinted assets of the running build.
            return _buildMonitor is not null && await _buildMonitor.CheckAsync(CancellationToken.None).ConfigureAwait(false)
                ? BuildKnownAssemblyLoadOutcome.HostBuildChanged(assemblyName)
                : BuildKnownAssemblyLoadOutcome.Failed(assemblyName, exception.Message);
        }
    }
}

/// <summary>Browser-specific transport boundary implemented with <c>LazyAssemblyLoader</c> by the WASM host.</summary>
public interface IBuildKnownAssemblyTransport
{
    /// <summary>Fetches and loads the named assemblies into the running WebAssembly runtime.</summary>
    /// <param name="assemblyNames">The declared assembly file names to load.</param>
    /// <param name="cancellationToken">Cancels the browser request.</param>
    /// <returns>The assemblies that were loaded.</returns>
    [RequiresUnreferencedCode("The browser lazy loader loads metadata discovered after trimming.")]
    Task<IReadOnlyList<Assembly>> LoadAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken);
}

/// <summary>Typed, recoverable result of a build-known assembly load request.</summary>
public sealed record BuildKnownAssemblyLoadOutcome(string AssemblyName, BuildKnownAssemblyLoadStatus Status, Assembly? Assembly, string? Detail)
{
    /// <summary>The assembly was loaded.</summary>
    /// <param name="name">The requested assembly file name.</param>
    /// <param name="assembly">The loaded assembly.</param>
    /// <returns>A <see cref="BuildKnownAssemblyLoadStatus.Loaded"/> outcome.</returns>
    public static BuildKnownAssemblyLoadOutcome Loaded(string name, Assembly assembly) => new(name, BuildKnownAssemblyLoadStatus.Loaded, assembly, null);
    /// <summary>The name is not declared by this build, so no request was made.</summary>
    /// <param name="name">The requested assembly file name.</param>
    /// <returns>A <see cref="BuildKnownAssemblyLoadStatus.NotDeclared"/> outcome.</returns>
    public static BuildKnownAssemblyLoadOutcome NotDeclared(string name) => new(name, BuildKnownAssemblyLoadStatus.NotDeclared, null, "The assembly is not declared by this build.");
    /// <summary>The transport finished without returning the assembly.</summary>
    /// <param name="name">The requested assembly file name.</param>
    /// <returns>A <see cref="BuildKnownAssemblyLoadStatus.Missing"/> outcome.</returns>
    public static BuildKnownAssemblyLoadOutcome Missing(string name) => new(name, BuildKnownAssemblyLoadStatus.Missing, null, "The published asset did not load.");
    /// <summary>The request was cancelled.</summary>
    /// <param name="name">The requested assembly file name.</param>
    /// <returns>A <see cref="BuildKnownAssemblyLoadStatus.Cancelled"/> outcome.</returns>
    public static BuildKnownAssemblyLoadOutcome Cancelled(string name) => new(name, BuildKnownAssemblyLoadStatus.Cancelled, null, "The browser cancelled the assembly request.");
    /// <summary>The transport threw.</summary>
    /// <param name="name">The requested assembly file name.</param>
    /// <param name="detail">The exception message, kept for diagnostics.</param>
    /// <returns>A <see cref="BuildKnownAssemblyLoadStatus.Failed"/> outcome.</returns>
    public static BuildKnownAssemblyLoadOutcome Failed(string name, string detail) => new(name, BuildKnownAssemblyLoadStatus.Failed, null, detail);
    /// <summary>The asset failed to load because the origin now serves another build (ADR 0162).</summary>
    /// <param name="name">The requested assembly file name.</param>
    /// <returns>A <see cref="BuildKnownAssemblyLoadStatus.HostBuildChanged"/> outcome.</returns>
    public static BuildKnownAssemblyLoadOutcome HostBuildChanged(string name) => new(
        name,
        BuildKnownAssemblyLoadStatus.HostBuildChanged,
        null,
        "HackerOS was updated while it was running: restart it to open this app.");
}

/// <summary>Why a <see cref="BuildKnownAssemblyLoaderRegistry.LoadAsync"/> request succeeded or not.</summary>
public enum BuildKnownAssemblyLoadStatus
{
    /// <summary>The assembly was fetched and loaded into the runtime.</summary>
    Loaded,

    /// <summary>The name is not in the build's list of lazy-loadable assemblies, so nothing was requested.</summary>
    NotDeclared,

    /// <summary>The transport completed but returned no assembly for the declared asset.</summary>
    Missing,

    /// <summary>The caller's token, or the browser, cancelled the request.</summary>
    Cancelled,

    /// <summary>The transport threw; <see cref="BuildKnownAssemblyLoadOutcome.Detail"/> holds the message.</summary>
    Failed,

    /// <summary>The transport threw and the origin serves another build than the running one (ADR 0162): a restart loads it.</summary>
    HostBuildChanged,
}
