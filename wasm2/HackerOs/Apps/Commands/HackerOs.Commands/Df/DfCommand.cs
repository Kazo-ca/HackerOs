using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.IO;
using HackerOs.Commands.Common;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Df;

/// <summary>
/// <c>df [-h] [-k] [-T] [-a]</c> (ADR 0064): space of the mounted disks from
/// <see cref="HackDriveInfo"/> (the IndexedDB disk <c>/dev/hda</c> on <c>/</c>, ADR 0057), in
/// 1K-blocks or human-readable sizes, and the network drives of ADR 0181 (<c>host:&lt;folder&gt;</c>, no size); <c>-a</c> also lists the pseudo filesystems of
/// <c>/proc/mounts</c> (<c>proc</c>, <c>devtmpfs</c>) with no blocks, as Linux does.
/// </summary>
public sealed class DfCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool human = false, showType = false, all = false;
        foreach (string arg in context.Arguments)
        {
            if (arg is "--human-readable" or "--print-type" or "--all")
            {
                human |= arg == "--human-readable";
                showType |= arg == "--print-type";
                all |= arg == "--all";
                continue;
            }

            if (!arg.StartsWith('-'))
            {
                continue;
            }

            foreach (char flag in arg[1..])
            {
                switch (flag)
                {
                    case 'h' or 'H': human = true; break;
                    case 'T': showType = true; break;
                    case 'a': all = true; break;
                    case 'k' or 'P' or 'l': break;
                    default:
                        await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["InvalidOption", flag]);
                        return 1;
                }
            }
        }

        List<string[]> rows = [];
        HackDriveInfo[] drives;
        try
        {
            drives = await HackDriveInfo.GetDrivesAsync(cancellationToken);
        }
        catch (NotSupportedException)
        {
            drives = [];
        }

        foreach (HackDriveInfo drive in drives)
        {
            long used = drive.TotalSize - drive.AvailableFreeSpace;
            string percent = drive.TotalSize == 0 ? "-" : $"{(int)Math.Ceiling(used * 100.0 / drive.TotalSize)}%";
            if (drive.DriveType == HackDriveType.Network)
            {
                // ADR 0181: the browser gives neither the size nor the free space of a host folder.
                rows.Add(["host:" + drive.VolumeLabel, drive.DriveFormat, "-", "-", "-", "-", drive.Name]);
                continue;
            }

            rows.Add(
            [
                "/dev/" + drive.VolumeLabel, drive.DriveFormat, Size(drive.TotalSize, human), Size(used, human),
                Size(drive.AvailableFreeSpace, human), percent, drive.Name
            ]);
        }

        if (all)
        {
            foreach ((string device, string type, string mount) in await ReadPseudoMountsAsync(cancellationToken))
            {
                if (rows.All(row => row[6] != mount))
                {
                    rows.Add([device, type, "0", "0", "0", "-", mount]);
                }
            }
        }

        string sizeHeader = human ? "Size" : "1K-blocks";
        string[] header = ["Filesystem", "Type", sizeHeader, "Used", human ? "Avail" : "Available", "Use%", "Mounted on"];
        List<string[]> table = [header, .. rows];
        int[] widths = [.. Enumerable.Range(0, header.Length).Select(column => table.Max(row => row[column].Length))];
        foreach (string[] row in table)
        {
            List<string> cells = [row[0].PadRight(widths[0])];
            if (showType)
            {
                cells.Add(row[1].PadRight(widths[1]));
            }

            for (int column = 2; column < 6; column++)
            {
                cells.Add(row[column].PadLeft(widths[column]));
            }

            cells.Add(row[6]);
            await context.StandardOutput.WriteLineAsync(string.Join(' ', cells));
        }

        return 0;
    }

    private static string Size(long bytes, bool human) =>
        human ? CommandIO.HumanSize(bytes) : ((bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads <c>device mountpoint type …</c> lines of <c>/proc/mounts</c>; empty when unreadable.</summary>
    private static async Task<List<(string Device, string Type, string Mount)>> ReadPseudoMountsAsync(CancellationToken cancellationToken)
    {
        List<(string, string, string)> mounts = [];
        try
        {
            foreach (string line in await HackFile.ReadAllLinesAsync("/proc/mounts", cancellationToken))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 3)
                {
                    mounts.Add((fields[0], fields[2], fields[1]));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without /proc access, df shows the disks only.
        }

        return mounts;
    }
}
