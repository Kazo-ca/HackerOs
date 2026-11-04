using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Samples.TerminalApp;

/// <summary>
/// Sample terminal application deriving <see cref="TerminalAppBase"/>.
/// </summary>
/// <remarks>
/// Demonstrates:
/// <list type="bullet">
///   <item>Reading command-line arguments and flags.</item>
///   <item>Writing to standard output and standard error streams.</item>
///   <item>Inspecting current working directory and executing VFS operations.</item>
///   <item>Observing cancellation tokens.</item>
///   <item>Returning standard exit codes (0 for success, non-zero for error).</item>
///   <item><c>--processes</c>: a synchronous member of the process gateway, which a Web Worker can
///   answer only on a cross-origin isolated page (ADR 0131, section 4); elsewhere it is refused.
///   <c>--kill &lt;pid&gt;</c> does the same with <c>Kill</c>, whose refusal (no <c>process.manage</c>)
///   comes back from the OS as for a local call.</item>
///   <item><c>--isolation</c>: the page's cross-origin isolation through
///   <see cref="HackerOs.AppSdk.Diagnostics.HackCrossOriginIsolation"/> (ADR 0146), which needs no
///   capability and answers the same in a worker and on the main thread.</item>
/// </list>
/// </remarks>
public class SampleTerminalApp : TerminalAppBase
{
    public SampleTerminalApp()
        : this(AppDefinitionMapper.ToManifest(new TerminalSampleApp()))
    {
    }

    public SampleTerminalApp(AppManifest manifest)
        : base(manifest)
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Arguments.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            context.Arguments.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            await context.StandardOutput.WriteLineAsync(
                "Usage: sample-cmd [--verbose] [--processes | --kill <pid> | --isolation] [message]".AsMemory(), cancellationToken);
            return 0;
        }

        if (context.Arguments.Contains("--processes", StringComparer.OrdinalIgnoreCase))
        {
            return await ListProcessesAsync(context, cancellationToken);
        }

        if (context.Arguments.Contains("--isolation", StringComparer.OrdinalIgnoreCase))
        {
            HackerOs.AppSdk.Diagnostics.HackCrossOriginIsolationStatus isolation =
                await HackerOs.AppSdk.Diagnostics.HackCrossOriginIsolation.GetStatusAsync(cancellationToken);
            await context.StandardOutput.WriteLineAsync(
                $"isolation: {(isolation.IsIsolated ? "yes" : "no")} {isolation.Reason}".AsMemory(), cancellationToken);
            return 0;
        }

        int kill = Array.FindIndex(context.Arguments.ToArray(), static arg => string.Equals(arg, "--kill", StringComparison.OrdinalIgnoreCase));
        if (kill >= 0)
        {
            return await KillAsync(context, context.Arguments.ElementAtOrDefault(kill + 1), cancellationToken);
        }

        bool verbose = context.Arguments.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        string message = context.Arguments.FirstOrDefault(arg => !arg.StartsWith('-')) ?? "KazoOS SDK 1.0 Sample Command Executed.";

        if (verbose)
        {
            await context.StandardOutput.WriteLineAsync(
                $"Working Directory: {context.WorkingDirectory}".AsMemory(), cancellationToken);
            await context.StandardOutput.WriteLineAsync(
                $"User ID: {context.App.UserId}".AsMemory(), cancellationToken);
        }

        await context.StandardOutput.WriteLineAsync(message.AsMemory(), cancellationToken);

        context.App.Logging.Log(
            DiagnosticSeverity.Information,
            "SampleTerminalApp executed successfully.",
            new Dictionary<string, string> { ["Message"] = message });

        return 0;
    }

    /// <summary>
    /// Lists, synchronously, the processes this command may see (without <c>process.list</c>: its own).
    /// In a Web Worker the call blocks the worker until the OS answers, like a system call; without
    /// cross-origin isolation the worker cannot wait and the member is refused (exit code 1).
    /// </summary>
    private static async ValueTask<int> ListProcessesAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<HackerOs.Simulation.Abstractions.Processes.ProcessRecord> processes = context.App.Processes.ListProcesses();
            await context.StandardOutput.WriteLineAsync(
                $"processes: {string.Join(' ', processes.Select(static process => process.Pid.Value))}".AsMemory(), cancellationToken);
            return 0;
        }
        catch (NotSupportedException exception)
        {
            await context.StandardError.WriteLineAsync($"sample-cmd: {exception.Message}".AsMemory(), cancellationToken);
            return 1;
        }
    }

    /// <summary>Sends <c>SIGKILL</c> to <paramref name="target"/> with the synchronous <c>Kill</c> (see <see cref="ListProcessesAsync"/>).</summary>
    private static async ValueTask<int> KillAsync(TerminalExecutionContext context, string? target, CancellationToken cancellationToken)
    {
        if (!long.TryParse(target, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long pid))
        {
            await context.StandardError.WriteLineAsync("sample-cmd: --kill needs a process id".AsMemory(), cancellationToken);
            return 2;
        }

        try
        {
            HackerOs.Simulation.Abstractions.Processes.ProcessRecord killed =
                context.App.Processes.Kill(HackerOs.Simulation.Abstractions.Processes.ProcessId.FromInt64(pid));
            await context.StandardOutput.WriteLineAsync($"killed: {killed.Pid.Value}".AsMemory(), cancellationToken);
            return 0;
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException or KeyNotFoundException
            or HackerOs.Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException)
        {
            await context.StandardError.WriteLineAsync($"sample-cmd: {exception.Message}".AsMemory(), cancellationToken);
            return 1;
        }
    }
}
