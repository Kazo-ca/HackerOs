using System.Diagnostics;
using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Stress;

/// <summary>
/// Implements <c>stress</c>, after the Linux load generator: <c>stress -c 1</c> keeps one CPU busy
/// and <c>stress --vm 1 --vm-bytes 256M</c> keeps memory allocated, until <c>-t</c> seconds have
/// passed or the process is killed.
/// </summary>
/// <remarks>
/// <para>
/// By default the CPU hog is a tight loop that never gives its thread back, like <c>stress</c>'s own
/// <c>sqrt()</c> loop: the case ADR 0070 exists for. In its worker the heartbeat stops ("Not
/// responding"), <c>kill</c> cannot be delivered, and only terminating the worker stops it
/// (ADR 0069, step 4) — the desktop never freezes.
/// </para>
/// <para>
/// With <c>--yield</c> it is a good citizen: it calls <see cref="HackProcess.CheckpointAsync"/> on
/// every slice, so it stays responsive and stops on <c>SIGTERM</c> (exit 130).
/// </para>
/// <para>
/// The memory hog (<c>-m</c>/<c>--vm</c>, ADR 0126) allocates <c>--vm-bytes</c> per hog (256 MiB by
/// default) in 1 MiB blocks, writes one byte in every 4 KiB page as the real one does, then frees it
/// all and starts again; <c>--vm-keep</c> keeps it instead. The memory is real managed memory of the
/// process's runtime, so the measured memory of ADR 0068 and the OOM killer see it. It always yields
/// between blocks: it is the memory that is under test, not the scheduler.
/// </para>
/// </remarks>
public sealed class StressCommand : TerminalAppBase
{
    /// <summary>Default <c>--vm-bytes</c>: 256 MiB, as in stress 1.0.</summary>
    internal const long DefaultVmBytes = 256L * 1024 * 1024;

    private const int BlockSize = 1024 * 1024;
    private const int PageSize = 4096;

    /// <summary>Initializes the command with its manifest.</summary>
    public StressCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        int? hogs = null;
        int vmHogs = 0;
        long vmBytes = DefaultVmBytes;
        bool vmKeep = false;
        double? timeout = null;
        bool yieldSlices = false;
        IReadOnlyList<string> args = context.Arguments;
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (arg is "-c" or "--cpu" && index + 1 < args.Count && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0)
            {
                hogs = count;
                index++;
            }
            else if (arg is "-m" or "--vm" && index + 1 < args.Count && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vm) && vm > 0)
            {
                vmHogs = vm;
                index++;
            }
            else if (arg == "--vm-bytes" && index + 1 < args.Count)
            {
                if (!TryParseBytes(args[index + 1], out vmBytes))
                {
                    await context.StandardError.WriteLineAsync(text["InvalidBytes", args[index + 1]]);
                    return 1;
                }

                index++;
            }
            else if (arg == "--vm-keep")
            {
                vmKeep = true;
            }
            else if (arg is "-t" or "--timeout" && index + 1 < args.Count && TryParseSeconds(args[index + 1], out double seconds))
            {
                timeout = seconds;
                index++;
            }
            else if (arg == "--yield")
            {
                yieldSlices = true;
            }
            else
            {
                await context.StandardError.WriteLineAsync(text["InvalidOption", arg]);
                await context.StandardError.WriteLineAsync(text["Usage"]);
                return 1;
            }
        }

        // Without any hog option, one CPU hog; with only --vm, memory hogs alone.
        int cpuHogs = hogs ?? (vmHogs > 0 ? 0 : 1);
        long pid = context.App.ProcessId.Value;
        await context.StandardOutput.WriteLineAsync(
            text["Dispatching", pid, cpuHogs, vmHogs]);
        await context.StandardOutput.FlushAsync(cancellationToken);

        // One thread per process: N hogs share it, as N processes would share one core.
        Stopwatch elapsed = Stopwatch.StartNew();
        long deadline = timeout is { } limit ? (long)(limit * Stopwatch.Frequency) : long.MaxValue;
        double sink = 0;
        List<byte[]> held = [];
        long target = vmHogs * vmBytes;
        while (elapsed.ElapsedTicks < deadline)
        {
            if (vmHogs > 0)
            {
                if (held.Count * (long)BlockSize < target)
                {
                    if (!TryAllocateBlock(held, (int)Math.Min(BlockSize, target - (held.Count * (long)BlockSize))))
                    {
                        held.Clear();
                        await context.StandardError.WriteLineAsync(text["VmAllocFailed", pid]);
                        return 1;
                    }
                }
                else if (!vmKeep)
                {
                    // stress frees its buffer and allocates it again; the runtime's heap stays grown.
                    held.Clear();
                }
                else if (cpuHogs == 0)
                {
                    // Holding the memory costs no CPU: sleep in short slices, still killable.
                    await Task.Delay(50, cancellationToken);
                }

                await HackProcess.CheckpointAsync(cancellationToken);
            }

            for (int hog = 0; hog < cpuHogs; hog++)
            {
                for (int i = 1; i < 20_000; i++)
                {
                    sink += Math.Sqrt(i);
                }
            }

            if (yieldSlices && vmHogs == 0)
            {
                await HackProcess.CheckpointAsync(cancellationToken);
            }
        }

        GC.KeepAlive(sink);
        GC.KeepAlive(held);
        await context.StandardOutput.WriteLineAsync(
            text["Completed", pid, elapsed.Elapsed.TotalSeconds]);
        return 0;
    }

    /// <summary>Allocates one block and writes a byte in each of its pages, as stress's <c>hogvm</c> does.</summary>
    private static bool TryAllocateBlock(List<byte[]> held, int size)
    {
        try
        {
            byte[] block = new byte[size];
            for (int offset = 0; offset < block.Length; offset += PageSize)
            {
                block[offset] = (byte)'Z';
            }

            held.Add(block);
            return true;
        }
        catch (OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Parses a size with an optional <c>B</c>, <c>K</c>, <c>M</c> or <c>G</c> suffix (powers of 1024).</summary>
    /// <param name="text">The size, e.g. <c>256M</c>.</param>
    /// <param name="bytes">The size in bytes.</param>
    internal static bool TryParseBytes(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        long multiplier = char.ToUpperInvariant(text[^1]) switch
        {
            'B' => 1,
            'K' => 1024,
            'M' => 1024 * 1024,
            'G' => 1024L * 1024 * 1024,
            _ => 0
        };
        string number = multiplier == 0 ? text : text[..^1];
        if (multiplier == 0)
        {
            multiplier = 1;
        }

        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= 0 || value > long.MaxValue / multiplier)
        {
            return false;
        }

        bytes = value * multiplier;
        return true;
    }

    private static bool TryParseSeconds(string text, out double seconds)
    {
        string number = text.TrimEnd('s');
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
    }
}
