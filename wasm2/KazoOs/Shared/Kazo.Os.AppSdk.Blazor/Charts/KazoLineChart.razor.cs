using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor.Charts;

/// <summary>Height presets of a <see cref="KazoLineChart"/> (no inline style is allowed, ADR 0007).</summary>
public enum KazoChartSize
{
    /// <summary>A sparkline-sized chart, e.g. one per CPU core.</summary>
    Small,

    /// <summary>The default height.</summary>
    Medium,

    /// <summary>A main chart filling a tab.</summary>
    Large
}

/// <summary>
/// A live line chart drawn in native SVG (ADR 0058, section 2): one or more
/// <see cref="KazoChartSeries"/>, the newest sample on the right, a fixed number of slots across
/// (60 seconds by default), and a legend with each series' latest value.
/// </summary>
/// <example>
/// <code>
/// &lt;KazoLineChart Title="CPU" Series="@([new KazoChartSeries("Total", cpuHistory)])"
///                Maximum="100" ValueFormatter="@(v =&gt; $"{v:0}%")" Filled="true" /&gt;
/// </code>
/// </example>
/// <remarks>
/// Colors come from the theme: series <c>N</c> uses <c>--hos-chart-series-N</c> (0 to 7), falling
/// back to the accent and status colors, so a theme can restyle every chart of every app.
/// </remarks>
public partial class KazoLineChart
{
    /// <summary>Number of color slots before they repeat.</summary>
    public const int ColorSlots = 8;

    private static readonly string[] GridRows =
        [Invariant(KazoChartGeometry.Height * 0.25), Invariant(KazoChartGeometry.Height * 0.5), Invariant(KazoChartGeometry.Height * 0.75)];

    private static readonly string PlotWidth = Invariant(KazoChartGeometry.Width);

    private static readonly string PlotHeight = Invariant(KazoChartGeometry.Height);

    /// <summary>The app the chart belongs to, cascaded by the window host: its language and formats apply (ADR 0104).</summary>
    [CascadingParameter]
    private IAppExecutionContext? AppContext { get; set; }

    /// <summary>Gets the chart's texts in the app's language, or the ambient app's without a cascaded one.</summary>
    private Microsoft.Extensions.Localization.IStringLocalizer<Strings> L => AppContext is { } context
        ? context.GetLocalizer<Strings>()
        : KazoLocalization.GetLocalizer<Strings>();

    /// <summary>Gets or sets the lines to draw, in legend order.</summary>
    [Parameter]
    public IReadOnlyList<KazoChartSeries> Series { get; set; } = [];

    /// <summary>Gets or sets the title shown above the chart.</summary>
    [Parameter]
    public string? Title { get; set; }

    /// <summary>Gets or sets the number of samples across the full width; at least 2. Defaults to 60.</summary>
    [Parameter]
    public int Capacity { get; set; } = 60;

    /// <summary>
    /// Gets or sets the value at the top edge (100 for a percentage). <see langword="null"/> scales
    /// to the data with <see cref="KazoChartGeometry.NiceMaximum"/>, never below <see cref="MinimumScale"/>.
    /// </summary>
    [Parameter]
    public double? Maximum { get; set; }

    /// <summary>Gets or sets the smallest top of an auto-scaled axis. Defaults to 1.</summary>
    [Parameter]
    public double MinimumScale { get; set; } = 1;

    /// <summary>Gets or sets how values are printed (scale and legend). Defaults to one decimal.</summary>
    [Parameter]
    public Func<double, string>? ValueFormatter { get; set; }

    /// <summary>Gets or sets whether the area under each line is shaded.</summary>
    [Parameter]
    public bool Filled { get; set; }

    /// <summary>Gets or sets whether the legend is shown. Defaults to <see langword="true"/>.</summary>
    [Parameter]
    public bool ShowLegend { get; set; } = true;

    /// <summary>Gets or sets the label of the left end of the time axis. Defaults to "<c>{Capacity} s</c>".</summary>
    [Parameter]
    public string? TimeSpanLabel { get; set; }

    /// <summary>Gets or sets the height preset.</summary>
    [Parameter]
    public KazoChartSize Size { get; set; } = KazoChartSize.Medium;

    /// <summary>Gets or sets extra CSS classes for the root element.</summary>
    [Parameter]
    public string? Class { get; set; }

    private double EffectiveMaximum => Maximum is > 0 and double maximum
        ? maximum
        : KazoChartGeometry.NiceMaximum(Series, MinimumScale);

    private int EffectiveCapacity => Math.Max(2, Capacity);

    private string ViewBox => string.Create(
        CultureInfo.InvariantCulture, $"0 0 {KazoChartGeometry.Width} {KazoChartGeometry.Height}");

    /// <summary>One vertical grid line every ten samples, like the task manager of Windows.</summary>
    private IEnumerable<string> GridColumns
    {
        get
        {
            double step = KazoChartGeometry.Width / (EffectiveCapacity - 1);
            for (int slot = 10; slot < EffectiveCapacity - 1; slot += 10)
            {
                yield return Invariant(slot * step);
            }
        }
    }

    private string AxisLabel => TimeSpanLabel ?? $"{EffectiveCapacity.ToString(CultureInfo.InvariantCulture)} s";

    private string SizeClass => Size switch
    {
        KazoChartSize.Small => "hos-chart-small",
        KazoChartSize.Large => "hos-chart-large",
        _ => "hos-chart-medium"
    };

    private string AccessibleLabel
    {
        get
        {
            Microsoft.Extensions.Localization.IStringLocalizer<Strings> text = L;
            return text["Chart.AccessibleLabel", Title ?? string.Empty, string.Join(", ", Series.Select(line =>
                $"{line.Name} {(line.Values.Count > 0 ? FormatValue(line.Values[^1]) : text["Chart.NoData"])}"))];
        }
    }

    private static string Invariant(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string SeriesClass(int index) => $"series-{(index % ColorSlots).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Formats a value with the host's formatter, or in the app's number format (<c>25,5</c> in French).</summary>
    private string FormatValue(double value) =>
        ValueFormatter?.Invoke(value) ?? value.ToString("0.#", AppContext?.FormatCulture ?? CultureInfo.InvariantCulture);
}
