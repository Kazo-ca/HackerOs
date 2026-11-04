using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Apps.SystemMonitor.Metrics;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.SystemMonitor.Tabs;

/// <summary>
/// The Network tab (ADR 0058, section 2): received and sent throughput of each interface of
/// <c>/proc/net/dev</c>, by difference between two readings, with the packet, error and drop
/// counters. The bytes are real: the OS counts every exchange of the apps' network gateway (ADR 0057).
/// </summary>
public partial class NetworkTab
{
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Gets or sets the live measurements.</summary>
    [Parameter, EditorRequired]
    public SystemMetrics Metrics { get; set; } = default!;

    /// <summary>The tab's texts in the language of the System Monitor process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the numbers shown (the process's locale).</summary>
    private CultureInfo Formats => AppContext?.FormatCulture ?? CultureInfo.InvariantCulture;

    /// <summary>The throughput chart's value formatter, in the process's locale.</summary>
    private string FormatRate(double value) => Units.Rate(value, Formats);
}
