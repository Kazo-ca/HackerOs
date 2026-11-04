using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Storage;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies browser quota reporting and persistent-storage requests.</summary>
public sealed class BrowserStorageManagerTests
{
    [Theory]
    [InlineData(943718400, 1073741824, false)]
    [InlineData(1017118720, 1073741824, true)]
    [InlineData(900000000, 1000000000, false)]
    [InlineData(900000001, 1000000000, true)]
    public async Task GetStatusAsync_applies_absolute_and_proportional_thresholds(
        long usageBytes,
        long quotaBytes,
        bool expectedLowSpace)
    {
        FakeStorageModule module = new(usageBytes, quotaBytes, isPersisted: true, persistenceGranted: true);
        FakeJsRuntime runtime = new(module);
        await using BrowserStorageManager manager = new(runtime);

        BrowserStorageStatus status = await manager.GetStatusAsync();
        bool granted = await manager.RequestPersistenceAsync();

        Assert.Equal(usageBytes, status.UsageBytes);
        Assert.Equal(quotaBytes - usageBytes, status.AvailableBytes);
        Assert.True(status.IsPersisted);
        Assert.Equal(expectedLowSpace, status.IsLowSpace);
        Assert.True(granted);
        Assert.Equal(1, runtime.ImportCount);
    }

    [Fact]
    public async Task GetStatusAsync_clamps_negative_available_space_and_reports_low_space()
    {
        await using BrowserStorageManager manager = new(
            new FakeJsRuntime(new FakeStorageModule(101, 100, isPersisted: false, persistenceGranted: false)));

        BrowserStorageStatus status = await manager.GetStatusAsync();

        Assert.Equal(0, status.AvailableBytes);
        Assert.False(status.IsPersisted);
        Assert.True(status.IsLowSpace);
        Assert.False(await manager.RequestPersistenceAsync());
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        internal int ImportCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("import", identifier);
            ImportCount++;
            return ValueTask.FromResult((TValue)module);
        }
    }

    private sealed class FakeStorageModule(
        long usageBytes,
        long quotaBytes,
        bool isPersisted,
        bool persistenceGranted) : IJSObjectReference
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "requestPersistence")
            {
                return ValueTask.FromResult((TValue)(object)persistenceGranted);
            }

            Assert.Equal("getStorageEstimate", identifier);
            string json = JsonSerializer.Serialize(new { usageBytes, quotaBytes, isPersisted });
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        }
    }
}