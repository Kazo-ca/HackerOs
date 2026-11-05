using System.Diagnostics;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>
/// A <see cref="TheoryAttribute"/> skipped when <c>node</c> is not on the PATH: the shared rings of
/// ADR 0131 are JavaScript (<c>wwwroot/sharedRing.js</c>), tested with Node's own
/// <c>SharedArrayBuffer</c> and <c>Atomics</c>.
/// </summary>
public sealed class NodeTheoryAttribute : TheoryAttribute
{
    /// <summary>Skips the theory when Node cannot be started.</summary>
    public NodeTheoryAttribute()
    {
        if (!NodeRunner.IsAvailable)
        {
            Skip = "node is not on the PATH";
        }
    }
}

/// <summary>Runs a Node script and returns its exit code and output.</summary>
internal static class NodeRunner
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            (int code, _, _) = Run(["--version"], TimeSpan.FromSeconds(10));
            return code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    /// <summary>Gets whether <c>node</c> starts.</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>
    /// Runs <c>node</c> with <paramref name="arguments"/>; kills it after <paramref name="timeout"/>.
    /// Returns the standard output and, apart, the standard error (Node's warnings go there).
    /// </summary>
    public static (int ExitCode, string Output, string Error) Run(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ProcessStartInfo start = new("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            return (-1, string.Empty, "timed out");
        }

        return (process.ExitCode, output.Result, error.Result);
    }
}
