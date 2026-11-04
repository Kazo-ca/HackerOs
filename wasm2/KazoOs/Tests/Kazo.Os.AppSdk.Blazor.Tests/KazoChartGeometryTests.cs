using Kazo.Os.AppSdk.Blazor.Charts;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>The arithmetic of <see cref="KazoLineChart"/> (ADR 0058, section 2).</summary>
public sealed class KazoChartGeometryTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.7, 1)]
    [InlineData(43, 50)]
    [InlineData(101, 200)]
    [InlineData(43117, 50000)]
    [InlineData(1000, 1000)]
    public void Auto_scale_rounds_up_to_one_two_or_five_times_a_power_of_ten(double largest, double expected)
    {
        double maximum = KazoChartGeometry.NiceMaximum([new KazoChartSeries("a", [largest / 2, largest])]);

        Assert.Equal(expected, maximum, 6);
    }

    [Fact]
    public void Auto_scale_never_goes_below_the_floor()
    {
        Assert.Equal(5000, KazoChartGeometry.NiceMaximum([new KazoChartSeries("idle", [0, 3])], floor: 4096), 6);
    }

    [Fact]
    public void The_newest_sample_sits_on_the_right_edge_and_values_are_clipped()
    {
        // Capacity 3 over a width of 600: slots at x = 0, 300, 600.
        Assert.Equal("300,50 600,0", KazoChartGeometry.BuildPoints([50, 250], capacity: 3, maximum: 100));
        Assert.Equal("0,100 300,75 600,0", KazoChartGeometry.BuildPoints([5, 0, 25, 100], capacity: 3, maximum: 100));
        Assert.Equal(string.Empty, KazoChartGeometry.BuildPoints([], capacity: 3, maximum: 100));
    }

    [Fact]
    public void The_area_closes_on_the_axis()
    {
        Assert.Equal("300,100 300,50 600,0 600,100", KazoChartGeometry.BuildArea([50, 100], capacity: 3, maximum: 100));
    }

    [Fact]
    public void Coordinates_ignore_the_current_culture()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            Assert.Equal("0,66.67 600,33.33", KazoChartGeometry.BuildPoints([1, 2], capacity: 2, maximum: 3));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
