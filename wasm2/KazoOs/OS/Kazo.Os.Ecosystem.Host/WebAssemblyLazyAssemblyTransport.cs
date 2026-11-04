using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Microsoft.AspNetCore.Components.WebAssembly.Services;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// Bridges the platform's build-known loading boundary to Blazor's browser
/// loader. The registry supplies only build-declared names, so this transport
/// cannot install arbitrary post-publish assemblies.
/// </summary>
public sealed class WebAssemblyLazyAssemblyTransport(LazyAssemblyLoader loader) : IBuildKnownAssemblyTransport
{
    /// <inheritdoc />
    [RequiresUnreferencedCode("Blazor LazyAssemblyLoader requires metadata preserved by build-known lazy descriptors.")]
    public async Task<IReadOnlyList<Assembly>> LoadAsync(
        IReadOnlyList<string> assemblyNames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await loader.LoadAssembliesAsync(assemblyNames)).ToArray();
    }
}
