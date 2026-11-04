using System.Globalization;
using System.Text;

namespace Kazo.Os.AppSdk.Blazor.Charts;

/// <summary>
/// One line of a <see cref="HackLineChart"/>: a name for the legend and its values, oldest first.
/// </summary>
/// <param name="Name">Legend label (<c>CPU</c>, <c>Read</c>, <c>eth0 rx</c>…).</param>
/// <param name="Values">Samples, oldest first; only the last <see cref="HackLineChart.Capacity"/> are drawn.</param>
public sealed record HackChartSeries(string Name, IReadOnlyList<double> Values);

/// <summary>
/// The pure arithmetic behind <see cref="HackLineChart"/>: scale and SVG coordinates. Kept apart
/// from the component so it is tested without rendering.
/// </summary>
public static class HackChartGeometry
{
    /// <summary>Width of the chart's SVG coordinate space; the SVG stretches it to the element.</summary>
    public const double Width = 600;

    /// <summary>Height of the chart's SVG coordinate space.</summary>
    public const double Height = 100;

    /// <summary>
    /// Picks the top of the vertical axis for auto-scaled data: the smallest "nice" number
    /// (1, 2 or 5 times a power of ten) at or above the largest value, so a throughput chart reads
    /// 0–50 kB/s instead of 0–43 117 B/s. Never below <paramref name="floor"/>.
    /// </summary>
    /// <param name="series">Every series drawn.</param>
    /// <param name="floor">Minimum top, so an idle chart does not zoom on noise.</param>
    public static double NiceMaximum(IEnumerable<HackChartSeries> series, double floor = 1)
    {
        ArgumentNullException.ThrowIfNull(series);
        double largest = series.SelectMany(static line => line.Values).Where(double.IsFinite).DefaultIfEmpty(0).Max();
        largest = Math.Max(largest, floor);
        if (largest <= 0)
        {
            return 1;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(largest)));
        foreach (double step in (ReadOnlySpan<double>)[1, 2, 5, 10])
        {
            if (largest <= step * magnitude)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    /// <summary>
    /// Builds the <c>points</c> attribute of a polyline. The newest sample sits on the right edge
    /// and each older one a slot further left, like the Windows task manager: a series shorter
    /// than <paramref name="capacity"/> starts partway across.
    /// </summary>
    /// <param name="values">Samples, oldest first.</param>
    /// <param name="capacity">Samples across the full width (60 for one minute at one per second).</param>
    /// <param name="maximum">Value drawn at the top edge; larger values are clipped to it.</param>
    public static string BuildPoints(IReadOnlyList<double> values, int capacity, double maximum)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        if (values.Count == 0 || maximum <= 0)
        {
            return string.Empty;
        }

        int count = Math.Min(values.Count, capacity);
        int skip = values.Count - count;
        double step = Width / (capacity - 1);
        StringBuilder builder = new();
        for (int index = 0; index < count; index++)
        {
            double x = (capacity - count + index) * step;
            double y = Height - (Clamp(values[skip + index], maximum) / maximum * Height);
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Format(x)).Append(',').Append(Format(y));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>points</c> of a polygon that fills the area under the line down to the axis,
    /// or an empty string when there is nothing to draw.
    /// </summary>
    /// <param name="values">Samples, oldest first.</param>
    /// <param name="capacity">Samples across the full width.</param>
    /// <param name="maximum">Value drawn at the top edge.</param>
    public static string BuildArea(IReadOnlyList<double> values, int capacity, double maximum)
    {
        string line = BuildPoints(values, capacity, maximum);
        if (line.Length == 0)
        {
            return string.Empty;
        }

        int count = Math.Min(values.Count, capacity);
        double firstX = (capacity - count) * (Width / (capacity - 1));
        return $"{Format(firstX)},{Format(Height)} {line} {Format(Width)},{Format(Height)}";
    }

    private static double Clamp(double value, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, maximum) : 0;

    private static string Format(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
