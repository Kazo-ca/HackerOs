namespace Kazo.Os.Infrastructure.Browser.Tests.Workers;

/// <summary>
/// The shared rings and synchronous call slot of a cross-origin isolated page (ADR 0131, section 4;
/// ADR 0143), run in Node: order, wrap-around, full ring and the worker's <c>Atomics.wait</c>.
/// </summary>
public sealed class SharedRingTests
{
    [NodeTheory]
    [InlineData("order")]
    [InlineData("wrap")]
    [InlineData("full")]
    [InlineData("sync")]
    public void Shared_ring_scenario_passes(string scenario)
    {
        string root = SolutionDirectory();
        (int code, string output, string error) = NodeRunner.Run(
            [
                Path.Combine(root, "Tests/Kazo.Os.Infrastructure.Browser.Tests/Workers/sharedRing.test.mjs"),
                Path.Combine(root, "Infrastructure/Kazo.Os.Infrastructure.Browser/wwwroot/sharedRing.js"),
                scenario
            ],
            TimeSpan.FromSeconds(30));

        Assert.True(code == 0 && output.Trim() == "ok", output + error);
    }

    private static string SolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Kazo.Os.sln not found above the test output.");
    }
}
