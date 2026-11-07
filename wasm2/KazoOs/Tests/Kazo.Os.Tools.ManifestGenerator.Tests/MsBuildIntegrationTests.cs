using System.Diagnostics;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>
/// Builds the out-of-solution fixture projects under <c>Tests/ManifestGenerator.Fixtures.*</c> with
/// the real <c>build/Kazo.Os.Apps.targets</c> wiring, like <c>RazorAssetValidationTests</c> does.
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
        Assert.Equal("org.kazoos.fixtures.hello", manifest.Id);
        Assert.Equal("ManifestGenerator.Fixtures.SingleApp.dll", manifest.EntryPoint!.Assembly);
    }

    [Fact]
    public async Task Multi_definition_project_generates_one_file_per_app()
    {
        (int exitCode, string output) = await BuildFixtureAsync("MultiApp");

        Assert.True(exitCode == 0, output);
        string directory = Path.Combine(FixtureDirectory("MultiApp"), "manifests");
        Assert.Equal(
            ["org.kazoos.fixtures.alpha.json", "org.kazoos.fixtures.beta.json"],
            Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(FixtureDirectory("MultiApp"), "manifest.json")));
    }

    [Fact]
    public async Task Project_without_definition_fails_with_HOSAPP0001()
    {
        (int exitCode, string output) = await BuildFixtureAsync("NoDefinition");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("error KOSAPP0001", output, StringComparison.Ordinal);
        Assert.DoesNotContain("KOSAPP0099", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(FixtureDirectory("NoDefinition"), "manifest.json")));
    }

    [Fact]
    public async Task App_referencing_platform_core_fails_with_HOSAPP0030()
    {
        (int exitCode, string output) = await BuildFixtureAsync("ForbiddenReference");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("error KOSAPP0030", output, StringComparison.Ordinal);
        Assert.Contains("Platform/Kazo.Os.Platform.Core/Kazo.Os.Platform.Core.csproj", output, StringComparison.Ordinal);
        // Allowed references (App Abstractions, AppSdk) and the generator's build-order reference are not reported.
        Assert.DoesNotContain("Kazo.Os.AppSdk.csproj' ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ManifestGenerator.csproj'", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_injecting_an_os_service_fails_with_HOSAPP0031_and_writes_no_manifest()
    {
        (int exitCode, string output) = await BuildFixtureAsync("ForbiddenInjection");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("error KOSAPP0031", output, StringComparison.Ordinal);
        Assert.Contains("ISessionService", output, StringComparison.Ordinal);
        Assert.DoesNotContain("KOSAPP0099", output, StringComparison.Ordinal);
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
                [("org.kazoos.fixtures.alpha", false), ("org.kazoos.fixtures.beta", false), ("org.kazoos.fixtures.hello", true)],
                applications.Select(item => (item.Manifest.Id, item.IsSystemApp)));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public async Task KazoOsPack_never_signs_even_when_the_former_author_key_variable_is_set()
    {
        // ADR 0233: packages are signed only in the marketplace repository; the variable of ADR 0219
        // left in an app repository's workflow must change nothing.
        string output = Directory.CreateTempSubdirectory("kazoos-pack-").FullName;
        try
        {
            (int exitCode, string log) = await BuildFixtureAsync(
                "SingleApp",
                new Dictionary<string, string> { [FormerAuthorKeyVariable] = "not a key" },
                "-t:KazoOsPack",
                $"-p:KazoOsPackageOutput={output}");

            Assert.True(exitCode == 0, log);
            Assert.DoesNotContain("KOSAPP0063", log, StringComparison.Ordinal);
            Platform.Core.Apps.Packages.AppPackageIndex index = Platform.Core.Apps.Packages.AppPackageIndex.Parse(
                await File.ReadAllBytesAsync(Path.Combine(output, "index.json")));
            Platform.Core.Apps.Packages.AppPackageIndexEntry entry = Assert.Single(index.Versions);
            Assert.Null(entry.Signature);
            Assert.True(File.Exists(Path.Combine(output, entry.Path)));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>The author key variable of ADR 0219, superseded by ADR 0233.</summary>
    private const string FormerAuthorKeyVariable = "HACKEROS_APP_SIGNING_KEY";

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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Kazo.Os.sln.");
    }
}
