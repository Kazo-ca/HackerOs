using HackerOs.AppSdk.Globalization;

namespace HackerOs.AppSdk.Tests.Globalization;

/// <summary>ADR 0193: the pure time zone rules of the SDK.</summary>
public sealed class HackTimeZoneTests
{
    [Theory]
    [InlineData("America/Toronto", "America/Toronto")]
    [InlineData(":Europe/Paris", "Europe/Paris")]
    [InlineData(" Asia/Tokyo ", "Asia/Tokyo")]
    [InlineData("UTC", "UTC")]
    [InlineData("Etc/UTC", "UTC")]
    public void TryFind_accepts_iana_names_with_or_without_the_posix_colon(string name, string expectedId)
    {
        Assert.True(HackTimeZone.TryFind(name, out TimeZoneInfo? zone));
        Assert.Equal(expectedId, HackTimeZone.GetId(zone));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Not/AZone")]
    public void TryFind_fails_for_auto_blank_and_unknown_names(string? name)
    {
        Assert.False(HackTimeZone.TryFind(name, out _));
    }

    [Fact]
    public void Resolve_falls_back_to_the_given_zone_then_the_device()
    {
        TimeZoneInfo fallback = TimeZoneInfo.CreateCustomTimeZone("Test/Fallback", TimeSpan.FromHours(2), "Fallback", "Fallback");

        Assert.Same(fallback, HackTimeZone.Resolve("auto", fallback));
        Assert.Same(HackTimeZone.Device, HackTimeZone.Resolve(null));
        Assert.Equal("Asia/Tokyo", HackTimeZone.GetId(HackTimeZone.Resolve("Asia/Tokyo", fallback)));
    }

    [Fact]
    public void FormatOffset_follows_daylight_saving_time()
    {
        TimeZoneInfo toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        TimeZoneInfo kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

        Assert.Equal("UTC-04:00", HackTimeZone.FormatOffset(toronto, new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("UTC-05:00", HackTimeZone.FormatOffset(toronto, new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("UTC+05:30", HackTimeZone.FormatOffset(kolkata, DateTimeOffset.UnixEpoch));
        Assert.Equal("UTC", HackTimeZone.FormatOffset(TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Available_lists_utc_and_region_city_zones_sorted_without_legacy_aliases()
    {
        IReadOnlyList<string> ids = HackTimeZone.Available.Select(HackTimeZone.GetId).ToList();

        Assert.Contains("UTC", ids);
        Assert.Contains("America/Toronto", ids);
        Assert.Contains("Europe/Paris", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("Etc/", StringComparison.Ordinal));
        Assert.DoesNotContain("EST5EDT", ids);
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    [Fact]
    public void ToLocal_converts_to_the_given_zone()
    {
        DateTimeOffset utc = new(2026, 10, 29, 14, 0, 0, TimeSpan.Zero);

        DateTimeOffset paris = HackTimeZone.ToLocal(utc, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"));

        Assert.Equal(new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.FromHours(1)), paris);
        Assert.Equal(TimeSpan.FromHours(1), paris.Offset);
    }
}
