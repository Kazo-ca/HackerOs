using System.Diagnostics;
using HackerOs.App.Abstractions;

namespace HackerOs.Tools.ManifestGenerator.Tests;

/// <summary>
/// Builds the out-of-solution fixture projects under <c>Tests/ManifestGenerator.Fixtures.*</c> with
/// the real <c>build/HackerOs.Apps.targets</c> wiring, like <c>RazorAssetValidationTests</c> does.
/// </summary>
public sealed class MsBuildIntegrationTests
{
    [Fact]
    public async Task Single_definition_project_generates_manifest_json()
    {
        (int exitCode, string output) = await BuildFixtureAsync("SingleApp");

        Assert.True(exitCode == 0, output);
        AppManifest manifest = AppManifestJsonSerializer.DeserializeStrict(
            await File.ReadAllTextAsync(Path.Combine(FixtureDirectory("SingleApp"), "manifest.json")));
        Assert.Equal("org.hackeros.fixtures.hello", manifest.Id);
        Assert.Equal("ManifestGenerator.Fixtures.SingleApp.dll", manifest.EntryPoint!.Assembly);
    }

    [Fact]
    public async Task Multi_definition_project_generates_one_file_per_app()
    {
        (int exitCode, string output) = await BuildFixtureAsync("MultiApp");

        Assert.True(exitCode == 0, output);
        string directory = Path.Combine(FixtureDirectory("MultiApp"), "manifests");
        Assert.Equal(
            ["org.hackeros.fixtures.alpha.json", "org.hackeros.fixtures.beta.json"],
            Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(FixtureDirectory("MultiApp"), "manifest.json")));
    }

    [Fact]
    public async Task Project_without_definition_fails_with_HOSAPP0001()
    {
        (int exitCode, string output) = await BuildFixtureAsync("NoDefinition");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("error HOSAPP0001", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HOSAPP0099", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(FixtureDirectory("NoDefinition"), "manifest.json")));
    }

    [Fact]
    public async Task App_referencing_platform_core_fails_with_HOSAPP0030()
    {
        (int exitCode, string output) = await BuildFixtureAsync("ForbiddenReference");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("error HOSAPP0030", output, StringComparison.Ordinal);
        Assert.Contains("Platform/HackerOs.Platform.Core/HackerOs.Platform.Core.csproj", output, StringComparison.Ordinal);
        // Allowed references (App Abstractions, AppSdk) and the generator's build-order reference are not reported.
        Assert.DoesNotContain("HackerOs.AppSdk.csproj' ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ManifestGenerator.csproj'", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_injecting_an_os_service_fails_with_HOSAPP0031_and_writes_no_manifest()
    {
        (int exitCode, string output) = await BuildFixtureAsync("ForbiddenInjection");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("error HOSAPP0031", output, StringComparison.Ordinal);
        Assert.Contains("ISessionService", output, StringComparison.Ordinal);
        Assert.DoesNotContain("HOSAPP0099", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(FixtureDirectory("ForbiddenInjection"), "manifest.json")));
    }

    [Fact]
    public async Task Host_embeds_BuiltInApplications_with_system_flags_and_ignores_non_app_projects()
    {
        (int exitCode, string output) = await BuildFixtureAsync("Host");

        Assert.True(exitCode == 0, output);
        string assemblyPath = Path.Combine(FixtureDirectory("Host"), "bin", "Debug", "net10.0", "ManifestGenerator.Fixtures.Host.dll");
        System.Runtime.Loader.AssemblyLoadContext context = new("host-fixture", isCollectible: true);
        try
        {
            using Stream resource = context.LoadFromStream(new MemoryStream(await File.ReadAllBytesAsync(assemblyPath)))
                .GetManifestResourceStream(BuiltInApplicationsJsonSerializer.ResourceName)
                ?? throw new InvalidOperationException("BuiltInApplications.json is not embedded.");
            using StreamReader reader = new(resource);
            IReadOnlyList<BuiltInApplication> applications = BuiltInApplicationsJsonSerializer.DeserializeStrict(await reader.ReadToEndAsync());

            Assert.Equal(
                [("org.hackeros.fixtures.alpha", false), ("org.hackeros.fixtures.beta", false), ("org.hackeros.fixtures.hello", true)],
                applications.Select(item => (item.Manifest.Id, item.IsSystemApp)));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public async Task HackerOsPack_signs_the_package_with_the_author_key_of_the_environment()
    {
        string output = Directory.CreateTempSubdirectory("hackeros-pack-").FullName;
        (string privateKey, string publicKey) = PackageSigner.CreateKeyPair();
        try
        {
            (int exitCode, string log) = await BuildFixtureAsync(
                "SingleApp",
                new Dictionary<string, string> { [PackageSigner.KeyVariable] = privateKey },
                "-t:HackerOsPack",
                $"-p:HackerOsPackageOutput={output}");

            Assert.True(exitCode == 0, log);
            Assert.DoesNotContain(GeneratorCodes.UnsignedPackage, log, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", log, StringComparison.Ordinal);
            Platform.Core.Apps.Packages.AppPackageIndex index = Platform.Core.Apps.Packages.AppPackageIndex.Parse(
                await File.ReadAllBytesAsync(Path.Combine(output, "index.json")));
            Platform.Core.Apps.Packages.AppPackageIndexEntry entry = Assert.Single(index.Versions);
            byte[] package = await File.ReadAllBytesAsync(Path.Combine(output, entry.Path));
            Assert.True(PackageSigner.Verify(package, entry.Signature!, publicKey));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task HackerOsKeyGen_requires_the_key_file_property()
    {
        (int exitCode, string output) = await BuildFixtureAsync("SingleApp", null, "-t:HackerOsKeyGen");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("error HOSAPP0061", output, StringComparison.Ordinal);
    }

    private static string FixtureDirectory(string name) =>
        Path.Combine(FindSolutionDirectory(), "Tests", $"ManifestGenerator.Fixtures.{name}");

    private static Task<(int ExitCode, string Output)> BuildFixtureAsync(string name) => BuildFixtureAsync(name, null);

    private static async Task<(int ExitCode, string Output)> BuildFixtureAsync(
        string name, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        string solutionDirectory = FindSolutionDirectory();
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = solutionDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(Path.Combine(FixtureDirectory(name), $"ManifestGenerator.Fixtures.{name}.csproj"));
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-p:NuGetAudit=false");
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // A key set on the machine running the tests must not sign the fixtures by accident.
        startInfo.Environment.Remove(PackageSigner.KeyVariable);
        foreach ((string variable, string value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[variable] = value;
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the {name} fixture build.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HackerOs.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate HackerOs.sln.");
    }
}
