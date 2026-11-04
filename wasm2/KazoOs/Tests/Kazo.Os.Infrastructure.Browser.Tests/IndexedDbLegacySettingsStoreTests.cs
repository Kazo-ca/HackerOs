using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Mapping of the retired IndexedDB settings records read by the ADR 0044 migration.</summary>
public sealed class IndexedDbLegacySettingsStoreTests
{
    [Fact]
    public void TryRead_maps_a_stored_app_user_record()
    {
        using JsonDocument json = JsonDocument.Parse(
            """{"id":"x","scope":1,"appId":"org.kazoos.terminal","userId":"u-1","installationId":null,"documentId":"settings","content":"fontSize=14\n","revision":3}""");

        Assert.True(IndexedDbLegacySettingsStore.TryRead(json.RootElement, out LegacySettingsRecord? record));

        Assert.Equal(
            new LegacySettingsRecord(SettingsScope.AppUser, "org.kazoos.terminal", "u-1", "settings", "fontSize=14\n"),
            record);
    }

    [Theory]
    [InlineData("""{"scope":1,"documentId":"settings"}""")]
    [InlineData("""{"scope":"one","documentId":"settings","content":""}""")]
    [InlineData("""{"scope":999,"documentId":"settings","content":""}""")]
    [InlineData("""{"scope":1,"content":""}""")]
    [InlineData("""[]""")]
    public void TryRead_skips_records_of_an_unexpected_shape(string text)
    {
        using JsonDocument json = JsonDocument.Parse(text);

        Assert.False(IndexedDbLegacySettingsStore.TryRead(json.RootElement, out _));
    }
}
