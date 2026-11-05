using System.Security.Cryptography;
using System.Text;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Kazo.Os.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kazo.Os.Server.Tests;

// =============================================================================
// Content Blob Service Tests — ADR 0030
// =============================================================================

public sealed class ContentBlobServiceTests : IDisposable
{
    private readonly KazoOsServerDbContext _db;
    private readonly ContentBlobService _svc;
    private readonly string _blobRoot;

    private static readonly Guid AccountA = Guid.NewGuid();

    public ContentBlobServiceTests()
    {
        var options = new DbContextOptionsBuilder<KazoOsServerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new KazoOsServerDbContext(options);

        _blobRoot = Path.Combine(Path.GetTempPath(), "kazoos-blob-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BlobStorage:Root"] = _blobRoot })
            .Build();

        _svc = new ContentBlobService(_db, configuration);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_blobRoot))
        {
            Directory.Delete(_blobRoot, recursive: true);
        }
    }

    // ── Upload → download round trip ────────────────────────────────────────

    [Fact]
    public async Task UploadThenDownload_MultiChunkFile_RoundTripsExactBytes()
    {
        // Arrange: a file spanning three chunks (chunk size is 256 KiB).
        const int chunkSize = 256 * 1024;
        byte[] content = new byte[(2 * chunkSize) + 1024];
        new Random(42).NextBytes(content);
        string hash = ComputeHash(content);

        var initiate = await _svc.InitiateUploadAsync(
            AccountA, new InitiateContentUploadRequest(hash, content.Length, AccountA), CancellationToken.None);

        Assert.False(initiate.AlreadyExists);
        Assert.Equal(3, initiate.TotalChunks);

        for (int i = 0; i < initiate.TotalChunks; i++)
        {
            int start = i * chunkSize;
            int length = Math.Min(chunkSize, content.Length - start);
            byte[] chunk = content[start..(start + length)];
            await _svc.AcceptChunkAsync(AccountA, initiate.UploadSessionId, i, chunk, CancellationToken.None);
        }

        // Act: download every chunk back.
        using var reassembled = new MemoryStream();
        for (int i = 0; i < initiate.TotalChunks; i++)
        {
            byte[] chunk = await _svc.GetChunkAsync(AccountA, hash, i, CancellationToken.None);
            reassembled.Write(chunk);
        }

        // Assert: exact byte-for-byte round trip.
        Assert.Equal(content, reassembled.ToArray());
    }

    [Fact]
    public async Task Download_UnknownContentHash_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _svc.GetChunkAsync(AccountA, "0000000000000000000000000000000000000000000000000000000000000", 0, CancellationToken.None));
    }

    [Fact]
    public async Task Download_ChunkIndexPastEnd_ThrowsArgumentOutOfRange()
    {
        byte[] content = Encoding.UTF8.GetBytes("small file content");
        string hash = ComputeHash(content);

        var initiate = await _svc.InitiateUploadAsync(
            AccountA, new InitiateContentUploadRequest(hash, content.Length, AccountA), CancellationToken.None);
        await _svc.AcceptChunkAsync(AccountA, initiate.UploadSessionId, 0, content, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _svc.GetChunkAsync(AccountA, hash, 5, CancellationToken.None));
    }

    [Fact]
    public async Task Download_NegativeChunkIndex_ThrowsArgumentOutOfRange()
    {
        byte[] content = Encoding.UTF8.GetBytes("small file content");
        string hash = ComputeHash(content);

        var initiate = await _svc.InitiateUploadAsync(
            AccountA, new InitiateContentUploadRequest(hash, content.Length, AccountA), CancellationToken.None);
        await _svc.AcceptChunkAsync(AccountA, initiate.UploadSessionId, 0, content, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _svc.GetChunkAsync(AccountA, hash, -1, CancellationToken.None));
    }

    // ── Deduplication ────────────────────────────────────────────────────────

    [Fact]
    public async Task InitiateUpload_ExistingHash_ShortCircuitsWithoutChunkTransfer()
    {
        byte[] content = Encoding.UTF8.GetBytes("shared content");
        string hash = ComputeHash(content);

        var first = await _svc.InitiateUploadAsync(
            AccountA, new InitiateContentUploadRequest(hash, content.Length, AccountA), CancellationToken.None);
        await _svc.AcceptChunkAsync(AccountA, first.UploadSessionId, 0, content, CancellationToken.None);

        // Act: a second device initiates upload of the same content.
        var second = await _svc.InitiateUploadAsync(
            AccountA, new InitiateContentUploadRequest(hash, content.Length, AccountA), CancellationToken.None);

        // Assert: dedup short-circuit — no chunk transfer required.
        Assert.True(second.AlreadyExists);

        // The content is still downloadable via the original hash.
        byte[] downloaded = await _svc.GetChunkAsync(AccountA, hash, 0, CancellationToken.None);
        Assert.Equal(content, downloaded);
    }

    // ── Corruption rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task AcceptChunk_AssembledContentDoesNotMatchDeclaredHash_ThrowsInvalidData()
    {
        byte[] actualContent = Encoding.UTF8.GetBytes("actual content bytes");
        string wrongHash = ComputeHash(Encoding.UTF8.GetBytes("a completely different declared payload"));

        var initiate = await _svc.InitiateUploadAsync(
            AccountA, new InitiateContentUploadRequest(wrongHash, actualContent.Length, AccountA), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _svc.AcceptChunkAsync(AccountA, initiate.UploadSessionId, 0, actualContent, CancellationToken.None));
    }

    private static string ComputeHash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
