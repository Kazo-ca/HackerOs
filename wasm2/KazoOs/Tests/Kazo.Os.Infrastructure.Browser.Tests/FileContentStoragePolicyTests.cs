using Kazo.Os.Infrastructure.Browser.Schema;

namespace Kazo.Os.Infrastructure.Browser.Tests;

public sealed class FileContentStoragePolicyTests
{
    [Fact]
    public void DefaultPolicy_UsesReasonableLimits()
    {
        FileContentStoragePolicy policy = FileContentStoragePolicy.Default;

        Assert.Equal(16 * 1024 * 1024, policy.MaxFileSizeBytes);
        Assert.Equal(256 * 1024, policy.MaxChunkSizeBytes);
        Assert.Equal("SHA-256", policy.ContentHashAlgorithm);
        Assert.True(policy.DeduplicateChunks);
        Assert.Equal(TimeSpan.FromDays(30), policy.OrphanRetention);
    }

    [Fact]
    public void RequiresChunking_UsesConfiguredChunkSize()
    {
        FileContentStoragePolicy policy = FileContentStoragePolicy.Default;

        Assert.False(policy.RequiresChunking(policy.MaxChunkSizeBytes));
        Assert.True(policy.RequiresChunking(policy.MaxChunkSizeBytes + 1));
    }

    [Fact]
    public void ChunkCountFor_UsesCeilingDivision()
    {
        FileContentStoragePolicy policy = FileContentStoragePolicy.Default;

        Assert.Equal(1, policy.ChunkCountFor(1));
        Assert.Equal(2, policy.ChunkCountFor(policy.MaxChunkSizeBytes + 1));
        Assert.Equal(3, policy.ChunkCountFor(policy.MaxChunkSizeBytes * 2 + 1));
    }

    [Fact]
    public void Constructor_RejectsInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileContentStoragePolicy(0, 1, "SHA-256", true, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileContentStoragePolicy(1, 0, "SHA-256", true, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileContentStoragePolicy(1, 2, "SHA-256", true, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentException>(() => new FileContentStoragePolicy(1, 1, "", true, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileContentStoragePolicy(1, 1, "SHA-256", true, TimeSpan.Zero));
    }
}
