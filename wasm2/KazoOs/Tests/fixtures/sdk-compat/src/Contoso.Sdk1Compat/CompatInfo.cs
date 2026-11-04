using System.Reflection;

namespace Contoso.Sdk1Compat;

/// <summary>What this assembly was compiled against.</summary>
internal static class CompatInfo
{
    /// <summary>
    /// The Kazo OS SDK version of the packages this assembly was compiled against, written at build
    /// as assembly metadata (the csproj): <see cref="Kazo.Os.App.Abstractions.HackerOsSdk.Version"/>
    /// is the SDK of the running OS, which is the point of the comparison.
    /// </summary>
    public static string CompiledAgainst { get; } = typeof(CompatInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "HackerOsSdkCompiledAgainst")?.Value ?? "?";
}
