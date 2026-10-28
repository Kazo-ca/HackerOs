using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Platform.Core.Tests.Apps.Packages;

/// <summary>References checked against the running OS before an install (ADR 0182 §5.3, Q1).</summary>
public sealed class AppPackageApiCheckTests
{
    private static Dictionary<string, byte[]> RealAssemblies() => new(StringComparer.Ordinal)
    {
        ["HackerOs.Platform.Core.dll"] = File.ReadAllBytes(typeof(AppPackage).Assembly.Location),
        ["HackerOs.Platform.Core.Tests.dll"] = File.ReadAllBytes(typeof(AppPackageApiCheckTests).Assembly.Location),
    };

    [Fact]
    public void Real_assemblies_find_every_reference_in_an_untrimmed_runtime()
    {
        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(RealAssemblies(), LoadedRuntimeApiSurface.Instance);

        Assert.Empty(missing);
    }

    [Fact]
    public void A_member_the_runtime_lost_is_reported_once_with_its_assembly()
    {
        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(
            new Dictionary<string, byte[]> { ["HackerOs.Platform.Core.dll"] = File.ReadAllBytes(typeof(AppPackage).Assembly.Location) },
            new TrimmedSurface(type: "System.IO.Compression.ZipArchive", member: "get_Entries"));

        MissingRuntimeApi entry = Assert.Single(missing);
        Assert.Equal("HackerOs.Platform.Core.dll", entry.Assembly);
        Assert.Equal("System.IO.Compression.ZipArchive.get_Entries(0) in System.IO.Compression", entry.Member);
    }

    [Fact]
    public void A_missing_type_and_a_missing_assembly_are_reported()
    {
        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(
            new Dictionary<string, byte[]> { ["HackerOs.Platform.Core.dll"] = File.ReadAllBytes(typeof(AppPackage).Assembly.Location) },
            new TrimmedSurface(type: "System.IO.Compression.ZipArchive", member: null, assembly: "Microsoft.Extensions.Logging.Abstractions"));

        Assert.Contains(missing, item => item.Member == "System.IO.Compression.ZipArchive in System.IO.Compression");
        Assert.Contains(missing, item => item.Member == "assembly Microsoft.Extensions.Logging.Abstractions");
    }

    [Fact]
    public void A_file_that_is_not_an_assembly_is_refused()
    {
        Assert.Equal(
            "apps.package.bad-assembly",
            Assert.Throws<AppPackageException>(() => AppPackageApiCheck.FindMissing(new Dictionary<string, byte[]> { ["x.dll"] = [1, 2, 3] }, LoadedRuntimeApiSurface.Instance)).Code);
    }

    /// <summary>The real runtime, minus one member (or one whole type) and optionally one assembly.</summary>
    private sealed class TrimmedSurface(string type, string? member, string? assembly = null) : IRuntimeApiSurface
    {
        public bool HasAssembly(string assemblyName) =>
            assemblyName != assembly && LoadedRuntimeApiSurface.Instance.HasAssembly(assemblyName);

        public bool HasType(string assemblyName, string typeName) =>
            !(member is null && typeName == type) && LoadedRuntimeApiSurface.Instance.HasType(assemblyName, typeName);

        public bool HasMethod(string assemblyName, string typeName, string methodName, int genericArity, int parameterCount) =>
            !(typeName == type && methodName == member)
            && LoadedRuntimeApiSurface.Instance.HasMethod(assemblyName, typeName, methodName, genericArity, parameterCount);

        public bool HasField(string assemblyName, string typeName, string fieldName) =>
            LoadedRuntimeApiSurface.Instance.HasField(assemblyName, typeName, fieldName);
    }
}
