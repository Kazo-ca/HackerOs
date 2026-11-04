using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Kazo.Os.Platform.WorkerHost.Worker;

/// <summary>
/// Loads, inside a worker, the assembly of an app it runs (ADR 0070, section 4: "le worker charge les
/// assemblies du SDK et de l'app"). The browser implementation fetches lazy-loaded assemblies first.
/// </summary>
public interface IWorkerAssemblyLoader
{
    /// <summary>Makes lazy-loaded assemblies available (the built-in commands, while the worker warms up).</summary>
    /// <param name="assemblyFiles">File names as the manifests give them (<c>Kazo.Os.Commands.dll</c>).</param>
    Task PreloadAsync(IReadOnlyList<string> assemblyFiles);

    /// <summary>Loads an app's assembly and returns its entry-point type.</summary>
    /// <param name="assemblyFile">File name as the manifest gives it.</param>
    /// <param name="typeName">Full name of the entry-point type.</param>
    /// <returns>The type.</returns>
    /// <exception cref="TypeLoadException">The assembly or the type cannot be found.</exception>
    Task<Type> LoadEntryTypeAsync(string assemblyFile, string typeName);
}

/// <summary>
/// Loads from the assemblies the runtime can already resolve: the in-process loopback of the tests,
/// where every app assembly is a normal reference. Also the browser loader's last step.
/// </summary>
public class ResolvedAssemblyLoader : IWorkerAssemblyLoader
{
    /// <inheritdoc />
    public virtual Task PreloadAsync(IReadOnlyList<string> assemblyFiles) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task<Type> LoadEntryTypeAsync(string assemblyFile, string typeName) =>
        Task.FromResult(ResolveType(assemblyFile, typeName));

    /// <summary>Finds an already-loaded (or loadable by name) assembly and the type in it.</summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "The entry-point type was resolved and validated by the OS's discovery against the host's " +
            "explicit app allowlist before it was handed to the worker; a trimmed host keeps that assembly whole " +
            "(ADR 0113: generated root descriptor).")]
    protected static Type ResolveType(string assemblyFile, string typeName)
    {
        string name = Path.GetFileNameWithoutExtension(assemblyFile);
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, name, StringComparison.Ordinal))
            ?? Assembly.Load(new AssemblyName(name));
        return assembly.GetType(typeName, throwOnError: false)
            ?? throw new TypeLoadException($"Type '{typeName}' was not found in '{name}'.");
    }
}
