using Kazo.Os.Apps.SystemMonitor.Metrics;
using Xunit;

namespace Kazo.Os.Apps.SystemMonitor.Tests;

public sealed class UnitsTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(8L * 1024 * 1024 * 1024, "8 GiB")]
    [InlineData(-5, "0 B")]
    public void Bytes_use_binary_units(double bytes, string expected) => Assert.Equal(expected, Units.Bytes(bytes));

    [Fact]
    public void Rates_percentages_and_durations_are_culture_independent()
    {
        Assert.Equal("2 KiB/s", Units.Rate(2048));
        Assert.Equal("12.5%", Units.Percent(12.5));
        Assert.Equal("1d 02:03:04", Units.Duration(new TimeSpan(1, 2, 3, 4)));
        Assert.Equal("00:00:00", Units.Duration(TimeSpan.FromSeconds(-1)));
    }
}
