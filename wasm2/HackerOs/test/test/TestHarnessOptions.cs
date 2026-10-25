namespace test;

/// <summary>
/// Out-of-band signal that this host process was started specifically to run the
/// Playwright E2E suite. Never true for a normal <c>dotnet run</c>/user launch — only
/// set when the process is started with the <c>--test-demo</c> argument, which the
/// E2E test harness passes explicitly. See doc/wasm/dev/old/e2e-test-demo-mode.md.
/// </summary>
public sealed record TestHarnessOptions(bool TestDemoEnabled)
{
    private const string TestDemoFlag = "--test-demo";

    /// <summary>Parses harness options from the process command-line arguments.</summary>
    public static TestHarnessOptions FromArgs(string[] args) =>
        new(Array.Exists(args, static arg => string.Equals(arg, TestDemoFlag, StringComparison.OrdinalIgnoreCase)));
}
