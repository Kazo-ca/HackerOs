using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Shell;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>The in-memory pipe between pipeline stages (ADR 0050, section 4).</summary>
public sealed class ShellPipeTests
{
    [Fact]
    public async Task Lines_written_reach_the_reader_then_end_of_stream_after_close()
    {
        ShellPipe pipe = new();
        await pipe.Writer.WriteLineAsync("one");
        await pipe.Writer.WriteAsync("two");
        pipe.Writer.Dispose();

        Assert.Equal("one", await pipe.Reader.ReadLineAsync());
        Assert.Equal("two", await pipe.Reader.ReadLineAsync());
        Assert.Null(await pipe.Reader.ReadLineAsync());
        Assert.Equal(string.Empty, await pipe.Reader.ReadToEndAsync());
    }

    [Fact]
    public async Task A_reader_waits_for_data_and_a_writer_waits_for_room()
    {
        ShellPipe pipe = new(capacity: 4);
        char[] buffer = new char[3];

        Task<int> earlyRead = pipe.Reader.ReadAsync(buffer, 0, 3);
        Assert.False(earlyRead.IsCompleted);

        Task write = pipe.Writer.WriteAsync("0123456789");
        Assert.False(write.IsCompleted); // 10 characters do not fit in 4

        string received = new(buffer, 0, await earlyRead.WaitAsync(TimeSpan.FromSeconds(5)));
        while (received.Length < 10)
        {
            int read = await pipe.Reader.ReadAsync(buffer, 0, 3).WaitAsync(TimeSpan.FromSeconds(5));
            received += new string(buffer, 0, read);
        }

        await write.WaitAsync(TimeSpan.FromSeconds(5));
        pipe.CloseWriter();
        Assert.Equal("0123456789", received);
        Assert.Equal(0, await pipe.Reader.ReadAsync(buffer, 0, 3));
    }

    [Fact]
    public void Synchronous_reads_on_an_empty_open_pipe_ask_for_the_async_form()
    {
        ShellPipe pipe = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => pipe.Reader.ReadLine());
        Assert.Contains("ReadLineAsync", error.Message);
        Assert.Throws<InvalidOperationException>(() => pipe.Reader.ReadToEnd());

        pipe.Writer.Write("x\n");
        Assert.Equal("x", pipe.Reader.ReadLine());
        pipe.CloseWriter();
        Assert.Null(pipe.Reader.ReadLine());
        Assert.Equal(-1, pipe.Reader.Read());
    }

    [Fact]
    public async Task Writing_after_the_reader_exited_is_a_broken_pipe()
    {
        ShellPipe pipe = new(capacity: 2);
        Task blocked = pipe.Writer.WriteAsync("abcdef");

        pipe.Reader.Dispose();

        HackIOException pending = await Assert.ThrowsAnyAsync<HackIOException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Broken pipe", pending.Message);
        Assert.ThrowsAny<HackIOException>(() => pipe.Writer.Write("x"));
    }
}
