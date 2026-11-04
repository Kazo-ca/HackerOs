using System.Reflection;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Injection rule of the SDK boundary (ADR 0055 §3, code <c>HOSAPP0031</c> per ADR 0059): an app may
/// only obtain a service whose type is defined by an SDK library (<c>Kazo.Os.AppSdk</c>,
/// <c>Kazo.Os.AppSdk.*</c>), a MudBlazor service, or <c>IJSRuntime</c> (to import its own module —
/// how it is called is checked by a source test, not here).
/// <para>
/// Two injection points are inspected, because they are the only two the platform honours for an
/// app: every <c>[Inject]</c> property of every type of the assembly (<c>@inject</c> compiles to
/// one), and every parameter of the public constructors of the entry points, which the host builds
/// with <c>ActivatorUtilities</c> (ADR 0034 §2).
/// </para>
/// Types are compared by name, never by identity: the app assembly lives in its own load context.
/// </summary>
public static class SdkBoundaryInspector
{
    private const string InjectAttributeFullName = "Microsoft.AspNetCore.Components.InjectAttribute";
    private const string JsRuntimeFullName = "Microsoft.JSInterop.IJSRuntime";
    private const string SdkAssemblyName = "Kazo.Os.AppSdk";

    /// <summary>Inspects <paramref name="types"/> and the constructors of <paramref name="entryPoints"/>.</summary>
    /// <param name="types">Every type of the app assembly.</param>
    /// <param name="entryPoints">Entry-point types declared by the assembly's <c>[AppManifest]</c> classes.</param>
    /// <returns>One diagnostic per forbidden injection, in deterministic order.</returns>
    public static IReadOnlyList<GeneratorDiagnostic> Inspect(IEnumerable<Type> types, IEnumerable<Type> entryPoints)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(entryPoints);

        List<GeneratorDiagnostic> diagnostics = [];
        foreach (Type type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            // DeclaredOnly: an inherited [Inject] property is reported once, on the type declaring it.
            foreach (PropertyInfo property in type
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(HasInjectAttribute)
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (!IsAllowedService(property.PropertyType))
                {
                    diagnostics.Add(Forbidden(type, property.PropertyType, $"[Inject] property '{property.Name}'"));
                }
            }
        }

        foreach (Type entryPoint in entryPoints.Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            foreach (ConstructorInfo constructor in entryPoint.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    if (!IsAllowedConstructorParameter(parameter.ParameterType))
                    {
                        diagnostics.Add(Forbidden(entryPoint, parameter.ParameterType, $"constructor parameter '{parameter.Name}'"));
                    }
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Gets whether an app may obtain a service of type <paramref name="serviceType"/> (ADR 0055 §3):
    /// SDK type, MudBlazor type or <c>IJSRuntime</c>.
    /// </summary>
    /// <param name="serviceType">Injected type.</param>
    /// <returns><see langword="true"/> when the injection is allowed.</returns>
    public static bool IsAllowedService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        string? assembly = serviceType.Assembly.GetName().Name;
        return IsSdkAssembly(assembly)
            || string.Equals(assembly, "MudBlazor", StringComparison.Ordinal)
            || string.Equals(serviceType.FullName, JsRuntimeFullName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets whether <paramref name="assemblyName"/> is an SDK library: <c>Kazo.Os.AppSdk</c> or
    /// <c>Kazo.Os.AppSdk.*</c>.
    /// </summary>
    /// <param name="assemblyName">Simple assembly name.</param>
    /// <returns><see langword="true"/> for an SDK library.</returns>
    public static bool IsSdkAssembly(string? assemblyName) =>
        string.Equals(assemblyName, SdkAssemblyName, StringComparison.Ordinal)
        || (assemblyName?.StartsWith(SdkAssemblyName + ".", StringComparison.Ordinal) ?? false);

    /// <summary>
    /// Constructor parameters of an entry point are services, except:
    /// <list type="bullet">
    /// <item>the data the host passes explicitly to <c>ActivatorUtilities</c>: the app's
    /// <c>AppManifest</c> (ADR 0034 §2), a record of App Abstractions, not an OS service;</item>
    /// <item>value types and <see cref="string"/>: never registered as services, so such a
    /// constructor is only usable by tests (e.g. a tick interval) and never gives access to the OS.</item>
    /// </list>
    /// </summary>
    private static bool IsAllowedConstructorParameter(Type parameterType) =>
        IsAllowedService(parameterType)
        || parameterType.IsValueType
        || string.Equals(parameterType.FullName, "System.String", StringComparison.Ordinal)
        || string.Equals(parameterType.FullName, "Kazo.Os.App.Abstractions.AppManifest", StringComparison.Ordinal);

    private static bool HasInjectAttribute(PropertyInfo property) =>
        property.GetCustomAttributesData().Any(attribute =>
            string.Equals(attribute.AttributeType.FullName, InjectAttributeFullName, StringComparison.Ordinal));

    private static GeneratorDiagnostic Forbidden(Type owner, Type serviceType, string injectionPoint) =>
        new(GeneratorCodes.ForbiddenInjection,
            owner.FullName ?? owner.Name,
            $"{injectionPoint} injects '{serviceType.FullName ?? serviceType.Name}' from '{serviceType.Assembly.GetName().Name}'. " +
            "An app only obtains OS services through the SDK (Kazo.Os.AppSdk*), MudBlazor services or IJSRuntime (ADR 0055 §3).");
}
