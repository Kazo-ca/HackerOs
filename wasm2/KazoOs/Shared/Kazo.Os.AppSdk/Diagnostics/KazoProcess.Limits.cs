using System.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.Diagnostics;

/// <summary>Resource limits of <see cref="KazoProcess"/> (ADR 0125): <c>getrlimit</c>, <c>setrlimit</c>, <c>prlimit</c>.</summary>
public sealed partial class KazoProcess
{
    /// <summary>
    /// Reads the process's resource limits from <c>/proc/&lt;pid&gt;/limits</c>, like
    /// <c>prlimit --pid</c>: CPU time, resident memory, open files and processes, and how the OS
    /// enforces them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The limits.</returns>
    /// <exception cref="InvalidOperationException">The process has exited, or is not visible to this app.</exception>
    /// <example>
    /// <code>
    /// KazoProcess self = await KazoProcess.GetCurrentProcessAsync();
    /// KazoResourceLimits limits = await self.GetResourceLimitsAsync();
    /// int workers = (int)Math.Min(64, limits.NoFile.Soft - 3);
    /// </code>
    /// </example>
    public async Task<KazoResourceLimits> GetResourceLimitsAsync(CancellationToken cancellationToken = default)
    {
        string? text = await TryReadAsync("limits", cancellationToken).ConfigureAwait(false);
        return text is null
            ? throw Exited(new KeyNotFoundException($"/proc/{Id.ToString(CultureInfo.InvariantCulture)}/limits is not readable."))
            : KazoResourceLimits.Parse(text);
    }

    /// <summary>
    /// Changes one resource limit of the process, like <c>prlimit --pid</c> (ADR 0125, section 4).
    /// Any value may be lowered; the soft value may rise up to the hard one; only an elevated caller
    /// (<c>sudo</c>) raises the hard value or changes another user's process.
    /// </summary>
    /// <param name="kind">The resource.</param>
    /// <param name="soft">The value that applies; <see cref="ResourceLimit.Unlimited"/> for <c>unlimited</c>. Seconds for CPU, bytes for memory.</param>
    /// <param name="hard">Its ceiling; <see cref="ResourceLimit.Unlimited"/> for <c>unlimited</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">A negative value, or <paramref name="soft"/> above <paramref name="hard"/> (<c>EINVAL</c>).</exception>
    /// <exception cref="KazoUnauthorizedAccessException">
    /// Raising a hard limit without elevation, another user's process, another process without
    /// <c>process.manage</c>, or limits turned off by the host (<c>EPERM</c>).
    /// </exception>
    /// <exception cref="InvalidOperationException">The process has exited (<c>ESRCH</c>).</exception>
    /// <exception cref="NotSupportedException">The host does not manage resource limits.</exception>
    public async Task SetResourceLimitAsync(ResourceLimitKind kind, long soft, long hard, CancellationToken cancellationToken = default)
    {
        ResourceLimit limit = new(soft, hard);
        if (!Enum.IsDefined(kind) || !limit.IsValid)
        {
            throw new ArgumentException($"'{limit}' is not a valid {kind} limit: values must not be negative and soft must not exceed hard.", nameof(soft));
        }

        IAppProcessGateway gateway = KazoExecutionContext.Current.Processes;
        ResourceLimitChangeResult result;
        try
        {
            result = await gateway.SetResourceLimitAsync(ProcessId.FromInt64(Id), kind, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }

        switch (result)
        {
            case ResourceLimitChangeResult.Applied:
                return;
            case ResourceLimitChangeResult.InvalidArgument:
                throw new ArgumentException($"'{limit}' is not a valid {kind} limit.", nameof(soft));
            case ResourceLimitChangeResult.PermissionDenied:
                throw new KazoUnauthorizedAccessException(
                    $"Operation not permitted: cannot set the {kind} limit of process {Id.ToString(CultureInfo.InvariantCulture)} to {limit}.");
            default:
                throw Exited(new KeyNotFoundException($"No process {Id.ToString(CultureInfo.InvariantCulture)}."));
        }
    }
}

/// <summary>How the OS enforces the limits of one process (ADR 0125, section 2).</summary>
public enum KazoResourceLimitEnforcement
{
    /// <summary>The host turned limits off (<c>KazoOs:ResourceLimits:Enabled=false</c>): nothing applies.</summary>
    Off,

    /// <summary>The process runs on the browser's main thread: a CPU or memory limit is checked between two slices only.</summary>
    BestEffort,

    /// <summary>The process runs in a Web Worker: the OS terminates the worker when a limit is crossed.</summary>
    Strict
}

/// <summary>
/// The resource limits of one process, as <c>/proc/&lt;pid&gt;/limits</c> shows them (ADR 0125),
/// returned by <see cref="KazoProcess.GetResourceLimitsAsync"/>.
/// </summary>
/// <param name="Cpu">CPU time, in seconds.</param>
/// <param name="Rss">Resident memory, in bytes.</param>
/// <param name="NoFile">Open file descriptors (the three standard streams included).</param>
/// <param name="NProc">Live processes of the user.</param>
/// <param name="Enforcement">How the OS enforces them for this process.</param>
/// <param name="IsCpuMeasured">Whether CPU time is measured; when not, the CPU limit is inactive.</param>
/// <param name="IsMemoryMeasured">Whether memory is measured; when not, the memory limit is inactive.</param>
public sealed record KazoResourceLimits(
    ResourceLimit Cpu,
    ResourceLimit Rss,
    ResourceLimit NoFile,
    ResourceLimit NProc,
    KazoResourceLimitEnforcement Enforcement,
    bool IsCpuMeasured,
    bool IsMemoryMeasured)
{
    /// <summary>Gets one limit by kind.</summary>
    /// <param name="kind">The resource.</param>
    public ResourceLimit this[ResourceLimitKind kind] => kind switch
    {
        ResourceLimitKind.Cpu => Cpu,
        ResourceLimitKind.Rss => Rss,
        ResourceLimitKind.NoFile => NoFile,
        ResourceLimitKind.NProc => NProc,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource limit.")
    };

    /// <summary>Parses the text of <c>/proc/&lt;pid&gt;/limits</c>.</summary>
    /// <param name="text">The file's content.</param>
    /// <returns>The limits; a line that is missing reads <c>unlimited</c>.</returns>
    public static KazoResourceLimits Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ResourceLimit cpu = ResourceLimit.Infinite;
        ResourceLimit rss = ResourceLimit.Infinite;
        ResourceLimit noFile = ResourceLimit.Infinite;
        ResourceLimit nproc = ResourceLimit.Infinite;
        KazoResourceLimitEnforcement enforcement = KazoResourceLimitEnforcement.BestEffort;
        bool cpuMeasured = true;
        bool memoryMeasured = true;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                if (line.Contains("enforcement: off", StringComparison.Ordinal))
                {
                    enforcement = KazoResourceLimitEnforcement.Off;
                }
                else if (line.Contains("enforcement: strict", StringComparison.Ordinal))
                {
                    enforcement = KazoResourceLimitEnforcement.Strict;
                }
                else if (line.Contains("inactive", StringComparison.Ordinal))
                {
                    string subjects = line[1..line.IndexOf(':', StringComparison.Ordinal)];
                    cpuMeasured &= !subjects.Contains("cpu", StringComparison.Ordinal);
                    memoryMeasured &= !subjects.Contains("rss", StringComparison.Ordinal);
                }

                continue;
            }

            // The name column is 25 characters wide, then soft, hard and units.
            if (line.Length <= 26)
            {
                continue;
            }

            string name = line[..25].Trim();
            string[] values = line[26..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 2 || !TryParseValue(values[0], out long soft) || !TryParseValue(values[1], out long hard))
            {
                continue;
            }

            switch (name)
            {
                case "Max cpu time":
                    cpu = new ResourceLimit(soft, hard);
                    break;
                case "Max resident set":
                    rss = new ResourceLimit(soft, hard);
                    break;
                case "Max open files":
                    noFile = new ResourceLimit(soft, hard);
                    break;
                case "Max processes":
                    nproc = new ResourceLimit(soft, hard);
                    break;
            }
        }

        return new KazoResourceLimits(cpu, rss, noFile, nproc, enforcement, cpuMeasured, memoryMeasured);
    }

    private static bool TryParseValue(string text, out long value)
    {
        if (text == "unlimited")
        {
            value = ResourceLimit.Unlimited;
            return true;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
