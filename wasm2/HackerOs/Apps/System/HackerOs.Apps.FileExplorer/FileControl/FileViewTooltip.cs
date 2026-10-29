using System.Globalization;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// The lines of the bubble shown after a pause of the mouse on an item (ADR 0185 §16): the full name, the
/// type, the size of a file, the modification date and, for a folder, how many items it holds.
/// </summary>
internal static class FileViewTooltip
{
    /// <summary>How long the mouse rests on an item before its bubble shows.</summary>
    internal const int DelayMilliseconds = 600;

    /// <summary>Describes <paramref name="item"/>; <paramref name="childCount"/> is the number of entries of a folder, when it could be read.</summary>
    internal static IReadOnlyList<string> Describe(FileViewItem item, int? childCount, IStringLocalizer<Strings> text)
    {
        List<string> lines =
        [
            item.Label,
            text["Tooltip.Type", FileViewDetails.FormatType(item, text)]
        ];
        if (item.Metadata is FileMetadata file)
        {
            lines.Add(text["Tooltip.Size", FileViewDetails.FormatBytes(file.Length, text)]);
        }

        lines.Add(text["Tooltip.Modified", item.Metadata.Timestamps.ContentModifiedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)]);
        if (item.IsDirectory && childCount is { } count)
        {
            lines.Add(count == 1 ? text["Tooltip.ItemCount.One"] : text["Tooltip.ItemCount.Many", count]);
        }

        return lines;
    }
}
