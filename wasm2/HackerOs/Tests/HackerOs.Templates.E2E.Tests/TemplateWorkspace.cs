using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace HackerOs.Templates.E2E.Tests;

/// <summary>
/// A workspace outside the repository that consumes KazoOS the way another repository would
/// (ADR 0098 §2, §6): the local package feed through a <c>nuget.config</c>, an isolated NuGet cache
/// (a re-packed version is never served stale) and an isolated <c>dotnet new</c> home with
/// HackerOs.Templates installed.
/// </summary>
public sealed class TemplateWorkspace : IAsyncLifetime
{
    /// <summary>Gets the KazoOS root (the folder of <c>HackerOs.sln</c>).</summary>
    public string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Gets the workspace folder the projects are generated in.</summary>
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hackeros-templates-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Gets the version every KazoOS package carries (<c>build/Versions.props</c>).</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Gets the local feed.</summary>
    public string Feed { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the feed of app packages compiled against earlier SDK versions and never rebuilt
    /// (<c>Tests/fixtures/sdk-compat/packages</c>, ADR 0114).
    /// </summary>
    public string FixtureFeed => Path.Combine(RepositoryRoot, "Tests", "fixtures", "sdk-compat", "packages");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        Version = XDocument.Load(Path.Combine(RepositoryRoot, "build", "Versions.props"))
            .Descendants("HackerOsVersion").Single().Value.Trim();

        Feed = Environment.GetEnvironmentVariable("HACKEROS_LOCAL_FEED") is { Length: > 0 } feed
            ? Path.GetFullPath(feed)
            : await PackAsync();

        File.WriteAllText(Path.Combine(Root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value="{Path.Combine(Root, "packages")}" /></config>
              <packageSources>
                <clear />
                <add key="hackeros-local" value="{Feed}" />
                <add key="hackeros-sdk-fixtures" value="{FixtureFeed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="hackeros-local"><package pattern="HackerOs.*" /></packageSource>
                <packageSource key="hackeros-sdk-fixtures"><package pattern="Contoso.*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        string templates = Path.Combine(Feed, $"HackerOs.Templates.{Version}.nupkg");
        await RunCheckedAsync(Root, "new", "install", templates);
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A process may still hold a file; the temp folder is cleaned by the OS.
        }

        return Task.CompletedTask;
    }

    /// <summary>Runs <c>dotnet</c> in <paramref name="directory"/> and fails with its output on a non-zero exit.</summary>
    public async Task<string> RunCheckedAsync(string directory, params string[] arguments)
    {
        (int exitCode, string output) = await RunAsync(directory, arguments);
        Assert.True(exitCode == 0, $"dotnet {string.Join(' ', arguments)} exited with {exitCode}:\n{output}");
        return output;
    }

    /// <summary>Starts <c>dotnet</c> without waiting (a host being served).</summary>
    public Process Start(string directory, params string[] arguments)
    {
        ProcessStartInfo start = CreateStartInfo(directory, arguments);
        return Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
    }

    /// <summary>Runs <c>dotnet</c> in <paramref name="directory"/> and returns its exit code and output (a build expected to fail).</summary>
    public async Task<(int ExitCode, string Output)> RunAsync(string directory, params string[] arguments)
    {
        using Process process = Start(directory, arguments);
        StringBuilder output = new();
        Task stdout = Task.Run(async () => output.Append(await process.StandardOutput.ReadToEndAsync()));
        Task stderr = Task.Run(async () => output.Append(await process.StandardError.ReadToEndAsync()));
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, output.ToString());
    }

    private ProcessStartInfo CreateStartInfo(string directory, string[] arguments)
    {
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(Root, "home");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["NUGET_PACKAGES"] = Path.Combine(Root, "packages");
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        // Variables of the outer test build must not leak into the generated projects' builds.
        start.Environment.Remove("MSBuildExtensionsPath");
        start.Environment.Remove("MSBuildSDKsPath");
        start.Environment.Remove("MSBUILD_EXE_PATH");
        return start;
    }

    /// <summary>Packs every KazoOS package with build/pack-local into a feed of this workspace.</summary>
    private async Task<string> PackAsync()
    {
        string feed = Path.Combine(Root, "feed");
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? new("pwsh", ["-NoProfile", "-File", Path.Combine(RepositoryRoot, "build", "pack-local.ps1"), "-Output", feed])
            : new("bash", [Path.Combine(RepositoryRoot, "build", "pack-local.sh"), "--output", feed]);
        start.WorkingDirectory = RepositoryRoot;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"pack-local failed:\n{await stdout}\n{await stderr}");
        return feed;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HackerOs.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("HackerOs.sln not found above the test output folder.");
    }
}

/// <summary>One workspace (and one pack) for every test of the suite.</summary>
[CollectionDefinition(Name)]
public sealed class TemplateWorkspaceCollection : ICollectionFixture<TemplateWorkspace>
{
    /// <summary>The collection name.</summary>
    public const string Name = "templates";
}
