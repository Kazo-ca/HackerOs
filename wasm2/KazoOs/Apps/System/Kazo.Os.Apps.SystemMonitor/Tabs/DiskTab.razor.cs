using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Apps.SystemMonitor.Metrics;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.SystemMonitor.Tabs;

/// <summary>
/// The Disk tab (ADR 0058, section 2): used and free space of each <see cref="HackDriveInfo"/>, and
/// the read and write throughput of each disk of <c>/proc/diskstats</c>, by difference between two
/// readings. The bytes are real: IndexedDB transactions counted by the OS (ADR 0057).
/// </summary>
public partial class DiskTab
{
    /// <summary>Gets or sets the live measurements.</summary>
    [Parameter, EditorRequired]
    public SystemMetrics Metrics { get; set; } = default!;

    /// <summary>Gets or sets the drives of the last refresh.</summary>
    [Parameter]
    public IReadOnlyList<HackDriveInfo> Drives { get; set; } = [];

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The tab's texts in the language of the System Monitor process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the numbers shown (the process's locale).</summary>
    private CultureInfo Formats => AppContext?.FormatCulture ?? CultureInfo.InvariantCulture;

    /// <summary>The throughput chart's value formatter, in the process's locale.</summary>
    private string FormatRate(double value) => Units.Rate(value, Formats);

    /// <summary>The active-time chart's value formatter, in the process's locale.</summary>
    private string FormatPercent(double value) => Units.Percent(value, Formats);

    /// <summary>Width of the used part of the bar, in the bar's 0–100 coordinate space.</summary>
    private static string BarWidth(double percent) =>
        Math.Clamp(percent, 0, 100).ToString("0.##", CultureInfo.InvariantCulture);
}
