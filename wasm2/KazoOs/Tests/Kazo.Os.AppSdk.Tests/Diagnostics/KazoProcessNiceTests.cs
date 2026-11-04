using Kazo.Os.AppSdk.Diagnostics;

namespace Kazo.Os.AppSdk.Tests.Diagnostics;

/// <summary>ADR 0127 §1: <see cref="KazoProcess.Nice"/> is read from field 19 of <c>/proc/&lt;pid&gt;/stat</c>.</summary>
public sealed class KazoProcessNiceTests
{
    [Theory]
    [InlineData("42 (sleep) S 1 42 42 0 -1 4194304 0 0 0 0 150 25 0 0 25 5 1 0 100 0 3", 5)]
    [InlineData("42 (sleep) S 1 42 42 0 -1 4194304 0 0 0 0 150 25 0 0 0 -20 1 0 100 0 3", -20)]
    [InlineData("42 (a (weird) name) R 1 42 42 0 -1 0 0 0 0 0 0 0 0 0 30 10 1 0 0 0 0", 10)]
    public void ParseStatNice_reads_the_nice_field_after_the_command_name(string stat, int expected)
    {
        Assert.Equal(expected, KazoProcess.ParseStatNice(stat));
    }

    [Theory]
    [InlineData("")]
    [InlineData("42 (sleep) S 1")]
    [InlineData("42 (sleep) S 1 42 42 0 -1 0 0 0 0 0 0 0 0 0 20 not-a-number 1")]
    public void ParseStatNice_falls_back_to_zero_for_a_short_or_invalid_line(string stat)
    {
        Assert.Equal(0, KazoProcess.ParseStatNice(stat));
    }

    [Fact]
    public void ParseStatNice_clamps_to_the_linux_range()
    {
        Assert.Equal(19, KazoProcess.ParseStatNice("1 (x) S 0 0 0 0 0 0 0 0 0 0 0 0 0 0 20 99 1"));
    }
}
