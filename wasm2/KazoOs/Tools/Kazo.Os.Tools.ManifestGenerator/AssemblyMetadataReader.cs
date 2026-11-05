using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Reads assembly-level facts from a file's metadata without loading it for execution (it may be a
/// reference assembly, or a version the tool already holds another copy of).
/// </summary>
internal static class AssemblyMetadataReader
{
    /// <summary>
    /// Reads the <c>AssemblyInformationalVersionAttribute</c> and the assembly version of
    /// <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Assembly file.</param>
    /// <param name="informationalVersion">The attribute's value, or <see langword="null"/> when absent.</param>
    /// <param name="version">The assembly version.</param>
    /// <returns><see langword="false"/> when the file has no readable metadata.</returns>
    public static bool TryReadInformationalVersion(string path, out string? informationalVersion, out Version? version)
    {
        informationalVersion = null;
        version = null;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            if (!pe.HasMetadata)
            {
                return false;
            }

            MetadataReader reader = pe.GetMetadataReader();
            AssemblyDefinition definition = reader.GetAssemblyDefinition();
            version = definition.Version;
            foreach (CustomAttributeHandle handle in definition.GetCustomAttributes())
            {
                CustomAttribute attribute = reader.GetCustomAttribute(handle);
                if (AttributeTypeName(reader, attribute) != "AssemblyInformationalVersionAttribute")
                {
                    continue;
                }

                // ECMA-335 II.23.3: prolog 0x0001, then the constructor's single string argument.
                BlobReader blob = reader.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() == 1)
                {
                    informationalVersion = blob.ReadSerializedString();
                }

                break;
            }

            return true;
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? AttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle parent = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default
        };
        return parent.Kind switch
        {
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => null
        };
    }
}
