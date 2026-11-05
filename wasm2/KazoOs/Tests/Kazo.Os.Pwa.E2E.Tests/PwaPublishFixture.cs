using System.Diagnostics;
using System.Text;

namespace Kazo.Os.Pwa.E2E.Tests;

/// <summary>
/// Publishes OS/Kazo.Os.Ecosystem twice into separate output directories so the suite
/// can test "two releases never mix cached assets" (P2-ACC-016). `service-worker-assets.js`'s
/// `assetsManifest.version` is a hash over the published asset set, but it is otherwise
/// content-deterministic: two publishes of byte-identical source produce the *same* hash
/// (confirmed empirically — an earlier assumption that any two independent publishes would
/// differ was wrong). Each publish therefore passes a distinct `-p:Version=`, which changes
/// the compiled assembly's version metadata bytes and so the resulting hash, exactly the way
/// two real releases would legitimately differ — no source files are touched.
/// Publishing runs once for the whole test collection (each publish takes real time).
/// </summary>
public sealed class PwaPublishFixture : IAsyncLifetime
{
    public string SolutionDirectory { get; private set; } = string.Empty;
    public string V1WwwrootPath { get; private set; } = string.Empty;
    public string V2WwwrootPath { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        SolutionDirectory = FindSolutionDirectory();
        string outputRoot = Path.Combine(SolutionDirectory, "artifacts", "pwa-test-publish");
        if (Directory.Exists(outputRoot))
        {
            Directory.Delete(outputRoot, recursive: true);
        }

        string v1Output = Path.Combine(outputRoot, "v1");
        string v2Output = Path.Combine(outputRoot, "v2");

        await PublishAsync(v1Output, "1.0.0-pwatest-v1");
        await PublishAsync(v2Output, "1.0.0-pwatest-v2");

        V1WwwrootPath = Path.Combine(v1Output, "wwwroot");
        V2WwwrootPath = Path.Combine(v2Output, "wwwroot");

        if (!File.Exists(Path.Combine(V1WwwrootPath, "service-worker-assets.js"))
            || !File.Exists(Path.Combine(V2WwwrootPath, "service-worker-assets.js")))
        {
            throw new InvalidOperationException(
                "Publish succeeded but service-worker-assets.js is missing from the output. " +
                "The published-content service worker swap (Kazo.Os.Ecosystem.csproj's " +
                "<ServiceWorker> item) may not have run.");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Isolating this publish's obj/bin via -p:BaseIntermediateOutputPath was tried and
    // reverted: the WASM SDK's restore does not honor that override reliably for the
    // browser-wasm RID graph (fails with NETSDK1047, "no target for net10.0/browser-wasm",
    // even after an explicit prior `dotnet restore` with the same override). Publishing
    // against the project's normal shared obj/bin instead, with a retry: a solution-wide test
    // run observed one transient collision here ("The process cannot access the file
    // '...\obj\Release\net10.0\webcil\...' because it is being used by another process"),
    // most likely a build-server/node-reuse process still finishing prior work against the
    // same project when this fixture's publish started.
    private async Task PublishAsync(string outputPath, string version)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ProcessStartInfo startInfo = new("dotnet")
            {
                WorkingDirectory = SolutionDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("publish");
            startInfo.ArgumentList.Add("OS/Kazo.Os.Ecosystem/Kazo.Os.Ecosystem.csproj");
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add($"-p:Version={version}");

            StringBuilder log = new();
            using Process process = new() { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return;
            }

            bool transientFileLock = log.ToString().Contains(
                "because it is being used by another process", StringComparison.OrdinalIgnoreCase);
            if (!transientFileLock || attempt == maxAttempts)
            {
                throw new InvalidOperationException(
                    $"dotnet publish into '{outputPath}' failed with exit code {process.ExitCode} " +
                    $"(attempt {attempt}/{maxAttempts}):\n{log}");
            }

            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }
    }

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate Kazo.Os.sln.");
    }
}

[CollectionDefinition(Name)]
public sealed class PwaPublishCollection : ICollectionFixture<PwaPublishFixture>
{
    public const string Name = "PWA published output";
}
