using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// The types and members the running OS really has. The host publish trims the .NET framework
/// (ADR 0113), so an assembly installed later can name a member that was removed (ADR 0182 §5.3).
/// </summary>
public interface IRuntimeApiSurface
{
    /// <summary>Gets whether the runtime can load <paramref name="assemblyName"/>.</summary>
    bool HasAssembly(string assemblyName);

    /// <summary>Gets whether a type exists. Nested types use <c>+</c>, e.g. <c>System.Outer+Inner</c>.</summary>
    bool HasType(string assemblyName, string typeName);

    /// <summary>Gets whether a type declares or inherits a method with this name, generic arity and parameter count.</summary>
    bool HasMethod(string assemblyName, string typeName, string methodName, int genericArity, int parameterCount);

    /// <summary>Gets whether a type declares or inherits a field with this name.</summary>
    bool HasField(string assemblyName, string typeName, string fieldName);
}

/// <summary>The surface of the runtime this code runs in, read by reflection.</summary>
[SuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Reflects over what survived trimming on purpose: a member missing here is exactly what the check reports.")]
[SuppressMessage("Trimming", "IL2070", Justification = "Same: the members that trimming removed are the answer.")]
[SuppressMessage("Trimming", "IL2075", Justification = "Same: the members that trimming removed are the answer.")]
public sealed class LoadedRuntimeApiSurface : IRuntimeApiSurface
{
    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    /// <summary>Gets the shared instance.</summary>
    public static LoadedRuntimeApiSurface Instance { get; } = new();

    private LoadedRuntimeApiSurface()
    {
    }

    /// <inheritdoc />
    public bool HasAssembly(string assemblyName) => Load(assemblyName) is not null;

    /// <inheritdoc />
    public bool HasType(string assemblyName, string typeName) => FindType(assemblyName, typeName) is not null;

    /// <inheritdoc />
    public bool HasMethod(string assemblyName, string typeName, string methodName, int genericArity, int parameterCount)
    {
        Type? type = FindType(assemblyName, typeName);
        if (type is null)
        {
            return false;
        }

        if (methodName is ".ctor" or ".cctor")
        {
            return type.GetConstructors(AllMembers & ~BindingFlags.FlattenHierarchy).Any(ctor =>
                ctor.IsStatic == (methodName == ".cctor") && ctor.GetParameters().Length == parameterCount)
                || (type.IsValueType && methodName == ".ctor" && parameterCount == 0);
        }

        return AllTypes(type).Any(candidate => candidate.GetMethods(AllMembers).Any(method =>
            method.Name == methodName
            && (method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0) == genericArity
            && method.GetParameters().Length == parameterCount));
    }

    /// <inheritdoc />
    public bool HasField(string assemblyName, string typeName, string fieldName)
    {
        Type? type = FindType(assemblyName, typeName);
        return type is not null && AllTypes(type).Any(candidate => candidate.GetField(fieldName, AllMembers) is not null);
    }

    private static IEnumerable<Type> AllTypes(Type type) => type.IsInterface ? [type, .. type.GetInterfaces(), typeof(object)] : [type];

    private static Assembly? Load(string assemblyName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException or IOException)
        {
            return null;
        }
    }

    private static Type? FindType(string assemblyName, string typeName) => Load(assemblyName)?.GetType(typeName, throwOnError: false);
}

/// <summary>One type or member a package assembly names that the runtime lacks.</summary>
/// <param name="Assembly">Package assembly that names it.</param>
/// <param name="Member">What is missing, e.g. <c>System.Text.Json.JsonSerializer.Serialize`1(2) in System.Text.Json</c>.</param>
public sealed record MissingRuntimeApi(string Assembly, string Member);

/// <summary>
/// Checks every type and member reference of a package's assemblies against the running OS
/// (ADR 0182 §5.3, answer A to Q1): a reference the trimmed runtime lacks refuses the installation
/// instead of failing when the app runs.
/// </summary>
/// <remarks>
/// Methods are matched by name, generic arity and parameter count, not by full signature: trimming
/// removes whole members, so this finds what it removed without decoding every type in a signature.
/// References between the package's own assemblies are not checked.
/// </remarks>
public static class AppPackageApiCheck
{
    /// <summary>Gets the references of <paramref name="assemblies"/> missing from <paramref name="runtime"/>.</summary>
    /// <param name="assemblies">Package assemblies, by file name.</param>
    /// <param name="runtime">What the running OS has.</param>
    /// <exception cref="AppPackageException">An assembly is not a valid .NET assembly.</exception>
    public static IReadOnlyList<MissingRuntimeApi> FindMissing(IReadOnlyDictionary<string, byte[]> assemblies, IRuntimeApiSurface runtime)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(runtime);
        HashSet<string> ownNames = new(
            assemblies.Keys.Select(static file => Path.GetFileNameWithoutExtension(file)),
            StringComparer.OrdinalIgnoreCase);
        List<MissingRuntimeApi> missing = [];
        foreach ((string file, byte[] bytes) in assemblies.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Check(file, bytes, ownNames, runtime, missing);
        }

        return missing;
    }

    private static void Check(string file, byte[] bytes, HashSet<string> ownNames, IRuntimeApiSurface runtime, List<MissingRuntimeApi> missing)
    {
        try
        {
            using PEReader pe = new(ImmutableArray.Create(bytes));
            if (!pe.HasMetadata)
            {
                throw new AppPackageException("apps.package.bad-assembly", $"lib/{file} is not a .NET assembly.");
            }

            MetadataReader reader = pe.GetMetadataReader();
            HashSet<string> reported = new(StringComparer.Ordinal);
            void Report(string member)
            {
                if (reported.Add(member))
                {
                    missing.Add(new MissingRuntimeApi(file, member));
                }
            }

            foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
            {
                string name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (!ownNames.Contains(name) && !runtime.HasAssembly(name))
                {
                    Report($"assembly {name}");
                }
            }

            foreach (TypeReferenceHandle handle in reader.TypeReferences)
            {
                if (Resolve(reader, handle, ownNames) is { } type && runtime.HasAssembly(type.Assembly) && !runtime.HasType(type.Assembly, type.Name))
                {
                    Report($"{type.Name} in {type.Assembly}");
                }
            }

            foreach (MemberReferenceHandle handle in reader.MemberReferences)
            {
                MemberReference member = reader.GetMemberReference(handle);
                if (ResolveParent(reader, member.Parent, ownNames) is not { } type
                    || !runtime.HasType(type.Assembly, type.Name))
                {
                    continue;
                }

                string name = reader.GetString(member.Name);
                BlobReader signature = reader.GetBlobReader(member.Signature);
                SignatureHeader header = signature.ReadSignatureHeader();
                if (header.Kind == SignatureKind.Field)
                {
                    if (!runtime.HasField(type.Assembly, type.Name, name))
                    {
                        Report($"{type.Name}.{name} in {type.Assembly}");
                    }
                }
                else if (header.Kind == SignatureKind.Method)
                {
                    int arity = header.IsGeneric ? signature.ReadCompressedInteger() : 0;
                    int parameters = signature.ReadCompressedInteger();
                    if (!runtime.HasMethod(type.Assembly, type.Name, name, arity, parameters))
                    {
                        string generic = arity > 0 ? $"`{arity}" : string.Empty;
                        Report($"{type.Name}.{name}{generic}({parameters}) in {type.Assembly}");
                    }
                }
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new AppPackageException("apps.package.bad-assembly", $"lib/{file} is not a valid .NET assembly: {exception.Message}");
        }
    }

    private static ExternalType? ResolveParent(MetadataReader reader, EntityHandle parent, HashSet<string> ownNames)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                return Resolve(reader, (TypeReferenceHandle)parent, ownNames);
            case HandleKind.TypeSpecification:
                BlobReader blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                {
                    return null;
                }

                blob.ReadSignatureTypeCode();
                EntityHandle definition = blob.ReadTypeHandle();
                return definition.Kind == HandleKind.TypeReference ? Resolve(reader, (TypeReferenceHandle)definition, ownNames) : null;
            default:
                return null;
        }
    }

    private static ExternalType? Resolve(MetadataReader reader, TypeReferenceHandle handle, HashSet<string> ownNames)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);
        string ns = reader.GetString(type.Namespace);
        switch (type.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                string assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
                return ownNames.Contains(assembly) ? null : new ExternalType(assembly, ns.Length == 0 ? name : $"{ns}.{name}");
            case HandleKind.TypeReference:
                return Resolve(reader, (TypeReferenceHandle)type.ResolutionScope, ownNames) is { } outer
                    ? outer with { Name = $"{outer.Name}+{name}" }
                    : null;
            default:
                return null;
        }
    }

    private sealed record ExternalType(string Assembly, string Name);
}
