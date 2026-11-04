using Kazo.Os.Platform.Blazor.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Kazo.Os.Ecosystem;

/// <summary>Wraps the WASM host environment for the render-mode-agnostic host environment abstraction.</summary>
public sealed class WebAssemblyEcosystemHostEnvironment(IWebAssemblyHostEnvironment inner) : IEcosystemHostEnvironment
{
    /// <summary>Gets whether the WebAssembly host runs in the Development environment.</summary>
    public bool IsDevelopment => inner.IsDevelopment();
}
