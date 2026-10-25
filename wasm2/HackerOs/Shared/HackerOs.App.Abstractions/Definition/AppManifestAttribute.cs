namespace HackerOs.App.Abstractions;

/// <summary>
/// Marks the single <see cref="AppDefinition"/> subclass that describes one application, per
/// <c>doc/en/wasm/dev/adr/0047-app-definition-and-generated-manifests.md</c>. The build-time manifest generator
/// (<c>Tools/HackerOs.Tools.ManifestGenerator</c>) discovers every marked class in a compiled app
/// assembly, instantiates it and writes the canonical <c>manifest.json</c> from it. The class is never
/// instantiated at runtime: hosts only read the generated JSON.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AppManifestAttribute : Attribute
{
    /// <summary>
    /// Gets or sets where the OS runs the app (ADR 0070, section 1). Left at
    /// <see cref="AppExecutionHost.Default"/>, the generator writes the default of the app's kind:
    /// <see cref="AppExecutionHost.MainThread"/> for a window app, <see cref="AppExecutionHost.Pool"/>
    /// for a command, <see cref="AppExecutionHost.Dedicated"/> for a service. A command or service that
    /// needs something a worker cannot offer (the DOM, the calling shell's own state) declares
    /// <see cref="AppExecutionHost.MainThread"/>, with the reason in a comment.
    /// </summary>
    public AppExecutionHost Execution { get; set; } = AppExecutionHost.Default;
}
