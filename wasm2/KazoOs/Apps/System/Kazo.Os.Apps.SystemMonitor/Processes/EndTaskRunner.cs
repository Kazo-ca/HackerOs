using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Apps.SystemMonitor.Processes;

/// <summary>One process that "End task" could not end, and why.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">Process name.</param>
/// <param name="Reason">Why it failed, for the user.</param>
public sealed record EndTaskFailure(long Pid, string Name, string Reason);

/// <summary>What one "End task" did (ADR 0058, section 2).</summary>
/// <param name="Ended">The processes ended (or already gone).</param>
/// <param name="Failures">The processes left running, with the reason.</param>
public sealed record EndTaskReport(IReadOnlyList<ProcessRow> Ended, IReadOnlyList<EndTaskFailure> Failures);

/// <summary>How "End task" stops a process (ADR 0069, section 4).</summary>
public enum EndTaskMode
{
    /// <summary>"End task": <c>SIGTERM</c>, the process gets 5 seconds to end by itself (<c>HackProcess.TerminateAsync</c>).</summary>
    Terminate,

    /// <summary>"Force kill": <c>SIGKILL</c>, immediate (<c>HackProcess.KillAsync</c>).</summary>
    ForceKill
}

/// <summary>
/// Ends a selection of processes. A refusal for one process (<c>process.manage</c> missing, which
/// only matters for another app's process) is recorded and the others still end, like
/// <c>kill 12 13 14</c> reporting each PID it could not signal.
/// </summary>
/// <remarks>
/// The processes are signalled together, not one after the other: with <c>SIGTERM</c>, each has up
/// to 5 seconds to end, and three stuck processes must not cost 15 seconds. The report keeps the
/// order of the selection.
/// </remarks>
public static class EndTaskRunner
{
    /// <summary>Ends every row with <paramref name="end"/>.</summary>
    /// <param name="rows">The selected processes.</param>
    /// <param name="end">
    /// Ends one process: <c>HackProcess.TerminateAsync</c> for "End task", <c>HackProcess.KillAsync</c>
    /// for "Force kill".
    /// </param>
    /// <returns>The processes ended and the failures, in the order of <paramref name="rows"/>.</returns>
    public static async Task<EndTaskReport> EndAsync(IReadOnlyList<ProcessRow> rows, Func<ProcessRow, Task> end)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(end);

        EndTaskFailure?[] outcomes = await Task.WhenAll(rows.Select(row => EndOneAsync(row, end)));
        List<ProcessRow> ended = [];
        List<EndTaskFailure> failures = [];
        for (int i = 0; i < rows.Count; i++)
        {
            if (outcomes[i] is { } failure)
            {
                failures.Add(failure);
            }
            else
            {
                ended.Add(rows[i]);
            }
        }

        return new EndTaskReport(ended, failures);
    }

    /// <summary>
    /// Stops one process the way <paramref name="mode"/> says: <see cref="HackProcess.TerminateAsync"/>
    /// (<c>SIGTERM</c>, 5 s) or <see cref="HackProcess.KillAsync"/> (<c>SIGKILL</c>).
    /// </summary>
    /// <param name="process">The process.</param>
    /// <param name="mode">"End task" or "Force kill".</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static Task End(HackProcess process, EndTaskMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        return mode == EndTaskMode.ForceKill
            ? process.KillAsync(cancellationToken)
            : process.TerminateAsync(cancellationToken: cancellationToken);
    }

    /// <summary>Ends one process; <see langword="null"/> when it ended (or was already gone).</summary>
    private static async Task<EndTaskFailure?> EndOneAsync(ProcessRow row, Func<ProcessRow, Task> end)
    {
        try
        {
            await end(row);
            return null;
        }
        catch (HackUnauthorizedAccessException)
        {
            // The reason is shown in the window: in the language of the process running this code.
            return new EndTaskFailure(row.Pid, row.Name, HackLocalization.GetLocalizer<Strings>()["EndTask.AccessDenied"]);
        }
        catch (InvalidOperationException)
        {
            // Exited between the refresh and the click: the goal is reached.
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new EndTaskFailure(row.Pid, row.Name, exception.Message);
        }
    }
}
