using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

public sealed class FileSystemContentContractTests
{
    [Fact]
    public void Content_descriptors_distinguish_binary_and_encoded_text()
    {
        FileSystemContentDescriptor binary = FileSystemContentDescriptor.Binary("IMAGE/PNG");
        FileSystemContentDescriptor text = FileSystemContentDescriptor.Text("TEXT/MARKDOWN", "UTF-8");

        Assert.Equal(FileSystemContentKind.Binary, binary.Kind);
        Assert.Equal("image/png", binary.MediaType);
        Assert.Null(binary.EncodingName);
        Assert.Equal(FileSystemContentKind.Text, text.Kind);
        Assert.Equal("text/markdown", text.MediaType);
        Assert.Equal("utf-8", text.EncodingName);
        Assert.Throws<ArgumentException>(() => FileSystemContentDescriptor.Binary("image/png; charset=utf-8"));
    }

    [Fact]
    public async Task Read_handle_streams_non_seekable_content_and_owns_disposal()
    {
        TrackingChunkStream stream = new([1, 2, 3, 4, 5], maximumChunkSize: 2);
        FileSystemContentReadHandle handle = new(
            CreateSnapshot(),
            FileSystemContentDescriptor.Binary(),
            stream);
        byte[] buffer = new byte[3];

        int first = await handle.Content.ReadAsync(buffer);
        int second = await handle.Content.ReadAsync(buffer.AsMemory(first));
        await handle.DisposeAsync();
        await handle.DisposeAsync();

        Assert.Equal(2, first);
        Assert.Equal(1, second);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
        Assert.Equal(1, stream.DisposeCount);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public async Task Content_source_opens_an_owned_stream_without_exposing_a_buffer()
    {
        ChunkSource source = new([9, 8, 7, 6]);

        await using Stream stream = await source.OpenReadAsync();
        byte[] buffer = new byte[2];
        int count = await stream.ReadAsync(buffer);

        Assert.Equal(2, count);
        Assert.Equal(new byte[] { 9, 8 }, buffer);
        Assert.Equal(4, source.Length);
        Assert.Equal(FileSystemContentKind.Binary, source.Descriptor.Kind);
    }

    [Fact]
    public void Read_handle_rejects_a_non_readable_stream()
    {
        using MemoryStream target = new();
        using Stream writer = new WriteOnlyStream(target);

        Assert.Throws<ArgumentException>(() => new FileSystemContentReadHandle(
            CreateSnapshot(),
            FileSystemContentDescriptor.Binary(),
            writer));
    }

    private static FileSystemEntrySnapshot CreateSnapshot()
    {
        DateTimeOffset timestamp = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        return new FileSystemEntrySnapshot(
            VirtualPath.Parse("/home/user/data.bin"),
            new FileMetadata(
                FileSystemEntryId.Parse("15f88b8c98a4479d9463d68867d35e15"),
                "user",
                "users",
                FileSystemPermissions.FromMode(0x01A4),
                new FileSystemTimestamps(timestamp, timestamp, timestamp),
                1,
                5));
    }

    private sealed class ChunkSource(byte[] content) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } =
            FileSystemContentDescriptor.Binary();

        public long? Length => content.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new TrackingChunkStream(content, 2));
        }
    }

    private sealed class TrackingChunkStream(byte[] content, int maximumChunkSize) : Stream
    {
        private int _position;

        public int DisposeCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.LongLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(content.Length - _position, Math.Min(count, maximumChunkSize));
            if (available <= 0)
            {
                return 0;
            }

            content.AsSpan(_position, available).CopyTo(buffer.AsSpan(offset, available));
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int available = Math.Min(content.Length - _position, Math.Min(buffer.Length, maximumChunkSize));
            if (available <= 0)
            {
                return ValueTask.FromResult(0);
            }

            content.AsMemory(_position, available).CopyTo(buffer);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class WriteOnlyStream(Stream target) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => target.Length;

        public override long Position
        {
            get => target.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => target.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => target.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => target.Write(buffer, offset, count);
    }
}