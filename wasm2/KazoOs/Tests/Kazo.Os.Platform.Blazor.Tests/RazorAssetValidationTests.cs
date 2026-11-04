using System.Diagnostics;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>Proves the repository-wide Razor asset policy rejects invalid components.</summary>
public sealed class RazorAssetValidationTests
{
    /// <summary>Builds an isolated invalid fixture and verifies the policy fails it.</summary>
    [Fact]
    public async Task Inline_style_fixture_fails_the_build()
    {
        string solutionDirectory = FindSolutionDirectory();
        string fixture = Path.Combine(
            solutionDirectory,
            "Tests",
            "RazorAssetValidation.Invalid",
            "RazorAssetValidation.Invalid.csproj");
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = solutionDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(fixture);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the invalid Razor fixture build.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        string output = await stdout + await stderr;

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(
            "cannot contain inline CSS or JavaScript",
            output,
            StringComparison.Ordinal);
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
