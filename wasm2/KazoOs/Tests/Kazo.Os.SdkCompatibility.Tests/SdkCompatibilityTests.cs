using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.SdkCompatibility.Tests;

/// <summary>
/// ADR 0098 §8, ADR 0114, ADR 0132 §1, ADR 0149 §5, ADR 0161 §3, ADR 0228: the apps kept in
/// <c>Tests/fixtures/sdk-compat</c>, each compiled once against a published SDK. An app of the
/// current major (<c>3.0.0</c>) is admitted by the current catalog, binds to the current SDK
/// assemblies and runs; an app of an earlier major is refused and must be rebuilt: 1.x
/// (<c>1.0.0</c>, <c>1.1.0</c>) since SDK 2.0 removed <c>Kazo.Os.AppSdk.FileView</c>, 2.x
/// (<c>2.0.0</c>) since SDK 3.0 renamed every <c>HackerOs.*</c> assembly to <c>Kazo.Os.*</c>. The Chromium counterpart,
/// which boots a host with the packages, is in <c>Kazo.Os.Templates.E2E.Tests</c>.
/// </summary>
public sealed class SdkCompatibilityTests
{
    /// <summary>The fixture packages compiled against the current major of the SDK (3.0.0).</summary>
    public static TheoryData<string> AdmittedPackages() => PackagesWhere(sameMajor: true);

    /// <summary>The fixture packages compiled against an earlier major of the SDK (1.0.0, 1.1.0, 2.0.0).</summary>
    public static TheoryData<string> RefusedPackages() => PackagesWhere(sameMajor: false);

    [Fact]
    public void The_SDK_1_x_and_2_x_fixtures_are_still_kept_as_proof_of_refusal() =>
        Assert.Equal(
            ["Contoso.Sdk11Compat.1.1.0.nupkg", "Contoso.Sdk1Compat.1.0.0.nupkg", "Contoso.Sdk2Compat.2.0.0.nupkg"],
            ((IEnumerable<object[]>)RefusedPackages()).Select(row => (string)row[0]).Order(StringComparer.Ordinal));

    [Fact]
    public void The_SDK_3_0_0_fixture_is_present_and_needs_SDK_3_0()
    {
        FixturePackage fixture = FixturePackage.Open("Contoso.Sdk3Compat.3.0.0.nupkg");
        Assert.All(fixture.Manifests, manifest => Assert.Equal("3.0.0", manifest.SdkCompatibility!.MinimumVersion));
    }

    /// <summary>
    /// ADR 0228: the 2.0.0 app references the <c>HackerOs.*</c> assemblies, which no longer exist; the
    /// 3.0 catalog refuses it on its SDK range before anything tries to load them.
    /// </summary>
    [Fact]
    public void The_SDK_2_0_0_fixture_references_the_old_assembly_names()
    {
        FixturePackage fixture = FixturePackage.Open("Contoso.Sdk2Compat.2.0.0.nupkg");
        Assert.All(fixture.Manifests, manifest => Assert.Equal("2.0.0", manifest.SdkCompatibility!.MinimumVersion));
        Assert.Contains(fixture.Assembly.GetReferencedAssemblies(), reference => reference.Name == "HackerOs.AppSdk");
        Assert.DoesNotContain(fixture.Assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Kazo.Os.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The 3.0.0 window app registered a file menu entry at build (<c>[RegisterFileContextMenu]</c>,
    /// ADR 0151) and shows the OS file control (<c>KazoFileView</c>, ADR 0148): the promise of 3.x
    /// covers both.
    /// </summary>
    [Fact]
    public void The_SDK_3_0_0_window_registers_a_file_menu_entry_and_shows_the_file_control()
    {
        FixturePackage fixture = FixturePackage.Open("Contoso.Sdk3Compat.3.0.0.nupkg");
        AppManifest window = fixture.Manifests.Single(item => item.Id == "com.contoso.sdk3-window");

        AppContextMenuEntryManifest entry = Assert.Single(window.ContextMenus!);
        Assert.Equal("inspect", entry.Name);
        Assert.Equal("com.contoso.sdk3.inspect.v1", entry.Intent);
        Assert.Contains(AppCapabilities.ShellContextMenu, window.Capabilities);
        Assert.Contains(
            fixture.Assembly.GetType(window.EntryPoint!.Type, throwOnError: true)!.Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Kazo.Os.AppSdk.Blazor");
        using PEReader pe = new(new MemoryStream(fixture.Image));
        MetadataReader metadata = pe.GetMetadataReader();
        Assert.Contains(metadata.TypeReferences, handle => metadata.GetString(metadata.GetTypeReference(handle).Name) == "KazoFileView");
    }

    [Theory]
    [MemberData(nameof(AdmittedPackages))]
    public void The_current_catalog_admits_its_apps(string package)
    {
        FixturePackage fixture = FixturePackage.Open(package);
        Assert.NotEmpty(fixture.Manifests);

        AppCatalogBuildResult result = AppCatalog.Build(fixture.Manifests);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(error => error.ToString())));
        Assert.Empty(result.Catalog!.Rejected);
        Assert.All(fixture.Manifests, manifest => Assert.True(
            KazoOsSdk.IsCompatible(manifest.SdkCompatibility!),
            $"{manifest.Id} needs SDK {manifest.SdkCompatibility!.MinimumVersion}, the OS has {KazoOsSdk.Version}."));
    }

    /// <summary>
    /// ADR 0149: an app compiled against an earlier major (SDK 1.x or 2.x) is refused by the 3.0 catalog (another major), never
    /// loaded, and listed as incompatible with the range it asked for.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedPackages))]
    public void The_current_catalog_refuses_its_apps(string package)
    {
        FixturePackage fixture = FixturePackage.Open(package);
        Assert.NotEmpty(fixture.Manifests);

        AppCatalogBuildResult result = AppCatalog.Build(fixture.Manifests);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(error => error.ToString())));
        Assert.Empty(result.Catalog!.Manifests);
        Assert.Equal(
            fixture.Manifests.Select(manifest => manifest.Id).Order(StringComparer.Ordinal),
            result.Catalog.Rejected.Select(rejection => rejection.AppId).Order(StringComparer.Ordinal));
        Assert.All(result.Catalog.Rejected, rejection => Assert.Equal("catalog.sdk.incompatible", rejection.Code));
        Assert.All(fixture.Manifests, manifest => Assert.False(KazoOsSdk.IsCompatible(manifest.SdkCompatibility!)));
    }

    /// <summary>
    /// Every Kazo OS type and member the app's metadata references still exists in the current SDK
    /// (a removed or renamed API is reported by name).
    /// </summary>
    [Theory]
    [MemberData(nameof(AdmittedPackages))]
    public void Every_SDK_type_and_member_it_references_still_exists(string package)
    {
        FixturePackage fixture = FixturePackage.Open(package);
        using PEReader pe = new(new MemoryStream(fixture.Image));
        MetadataReader metadata = pe.GetMetadataReader();
        List<string> missing = [];
        int checkedReferences = 0;

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference
                || ResolveKazoOsType(metadata, (TypeReferenceHandle)member.Parent, missing) is not { } type)
            {
                continue;
            }

            string name = metadata.GetString(member.Name);
            checkedReferences++;
            if (type.GetMember(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0)
            {
                missing.Add($"{type.FullName}.{name}");
            }
        }

        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            ResolveKazoOsType(metadata, handle, missing);
        }

        Assert.True(checkedReferences > 0, "The fixture references no Kazo OS member: it tests nothing.");
        Assert.Empty(missing.Distinct());
    }

    /// <summary>
    /// Every method of the app compiles against the current SDK: the JIT resolves each call, field
    /// and type the method body names, and throws <see cref="MissingMethodException"/>,
    /// <see cref="MissingFieldException"/> or <see cref="TypeLoadException"/> on a binary break
    /// (a changed signature, a type moved to another assembly).
    /// </summary>
    [Theory]
    [MemberData(nameof(AdmittedPackages))]
    public void Every_method_binds_to_the_current_SDK(string package)
    {
        FixturePackage fixture = FixturePackage.Open(package);
        int compiled = 0;
        foreach (Type type in fixture.Assembly.GetTypes().Where(type => !type.ContainsGenericParameters))
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            IEnumerable<MethodBase> methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
            foreach (MethodBase method in methods.Where(method => !method.ContainsGenericParameters && method.GetMethodBody() is not null))
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                compiled++;
            }
        }

        Assert.True(compiled > 0);
    }

    /// <summary>
    /// The 3.0.0 command, started the way the OS starts a terminal app, writes a file and copies it
    /// with the SDK 2.0 copy rules, renamed in 3.0 (<c>KazoFileOperations</c>, ADR 0161 §2), which name the copy.
    /// </summary>
    [Fact]
    public async Task The_SDK_3_0_0_command_runs_on_the_current_SDK()
    {
        (int exitCode, string stdout, string stderr, FakeAppFileSystemGateway fileSystem) =
            await RunCommandAsync("Contoso.Sdk3Compat.3.0.0.nupkg", "com.contoso.sdk3-command", ["still", "binds"]);

        Assert.Equal(string.Empty, stderr);
        Assert.Equal(0, exitCode);
        Assert.Equal("still binds", fileSystem.ContentOf("/home/user/sdk3-compat.txt"));
        Assert.Equal("still binds", fileSystem.ContentOf("/home/user/sdk3-compat (copy).txt"));
        Assert.Equal(
            $"sdk3-compat: still binds [sdk3-compat (copy).txt] (compiled against 3.0.0, running on {KazoOsSdk.Version})",
            stdout);
    }

    /// <summary>The fixture packages whose SDK major is (or is not) the current one.</summary>
    private static TheoryData<string> PackagesWhere(bool sameMajor)
    {
        TheoryData<string> data = [];
        foreach (string path in FixturePackage.All())
        {
            FixturePackage fixture = FixturePackage.Open(Path.GetFileName(path));
            bool current = fixture.Manifests.All(manifest =>
                SemanticVersion.TryParse(manifest.SdkCompatibility!.MinimumVersion, out SemanticVersion minimum)
                && minimum.Major == KazoOsSdk.SemanticVersion.Major);
            if (current == sameMajor)
            {
                data.Add(Path.GetFileName(path));
            }
        }

        return data;
    }

    /// <summary>Starts a fixture command the way the OS starts a terminal app and captures its output.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr, FakeAppFileSystemGateway FileSystem)> RunCommandAsync(
        string package, string appId, IReadOnlyList<string> arguments)
    {
        FixturePackage fixture = FixturePackage.Open(package);
        AppManifest manifest = fixture.Manifests.Single(item => item.Id == appId);
        Type entryPoint = fixture.Assembly.GetType(manifest.EntryPoint!.Type, throwOnError: true)!;
        TerminalAppBase command = (TerminalAppBase)Activator.CreateInstance(entryPoint, manifest)!;

        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        MinimalAppExecutionContext app = new(manifest, fileSystem);
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app, [.. arguments], TextReader.Null, stdout, stderr, "/home/user",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = "/home/user", ["USER"] = "user" });

        int exitCode;
        using (KazoExecutionContext.Enter(app, "/home/user"))
        {
            exitCode = await command.ExecuteAsync(context, CancellationToken.None);
        }

        return (exitCode, stdout.ToString().TrimEnd(), stderr.ToString(), fileSystem);
    }

    /// <summary>
    /// Resolves a type reference into a Kazo OS assembly (the SDK), or <see langword="null"/> for a
    /// reference into the framework or a third-party package. A Kazo OS type that no longer exists is
    /// added to <paramref name="missing"/>.
    /// </summary>
    private static Type? ResolveKazoOsType(MetadataReader metadata, TypeReferenceHandle handle, List<string> missing)
    {
        TypeReference reference = metadata.GetTypeReference(handle);
        string name = metadata.GetString(reference.Name);
        string ns = metadata.GetString(reference.Namespace);
        string? assemblyName;
        switch (reference.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                assemblyName = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                break;
            case HandleKind.TypeReference:
                // A nested type: resolve its declaring type first.
                Type? declaring = ResolveKazoOsType(metadata, (TypeReferenceHandle)reference.ResolutionScope, missing);
                if (declaring is null)
                {
                    return null;
                }

                Type? nested = declaring.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
                if (nested is null)
                {
                    missing.Add($"{declaring.FullName}+{name}");
                }

                return nested;
            default:
                return null;
        }

        if (!assemblyName.StartsWith("Kazo.Os.", StringComparison.Ordinal))
        {
            return null;
        }

        string fullName = ns.Length == 0 ? name : $"{ns}.{name}";
        Type? type = Assembly.Load(assemblyName).GetType(fullName, throwOnError: false);
        if (type is null)
        {
            missing.Add($"{fullName}, {assemblyName}");
        }

        return type;
    }
}
