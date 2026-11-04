using System.Text;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// An in-memory pipe between two pipeline stages (ADR 0050, section 4), modeled on a Linux pipe:
/// a bounded buffer (<see cref="DefaultCapacity"/> characters, the 64 KiB of a default Linux pipe)
/// with a write end (<see cref="Writer"/>) and a read end (<see cref="Reader"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Blocking.</b> An asynchronous write waits while the buffer is full; an asynchronous read
/// waits while it is empty and the write end is still open. Closing the write end delivers end of
/// stream to the reader once the buffer is drained.
/// </para>
/// <para>
/// <b>Single browser thread.</b> WebAssembly cannot block its only thread, so synchronous calls
/// never wait: a synchronous read on an empty pipe whose write end is still open throws
/// <see cref="InvalidOperationException"/> asking for the asynchronous form, and a synchronous
/// write on a full pipe lets the buffer grow past its capacity rather than deadlock. Every
/// built-in command reads and writes asynchronously.
/// </para>
/// <para>
/// <b>Broken pipe.</b> Once the read end is closed (the reading stage exited), every write throws
/// <see cref="HackIOException"/> "Broken pipe"; the orchestrator maps an uncaught one to exit code
/// 141, like a process killed by <c>SIGPIPE</c>.
/// </para>
/// </remarks>
public sealed class ShellPipe
{
    /// <summary>Default capacity in characters: 64 Ki, the default size of a Linux pipe.</summary>
    public const int DefaultCapacity = 64 * 1024;

    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private readonly int _capacity;
    private readonly KernelMemoryCharge? _charge;
    private bool _writerClosed;
    private bool _readerClosed;
    private TaskCompletionSource _dataAvailable = NewSignal();
    private TaskCompletionSource _spaceAvailable = NewSignal();

    /// <summary>Creates an empty pipe.</summary>
    /// <param name="capacity">Buffer capacity in characters; asynchronous writes wait beyond it.</param>
    /// <param name="charge">
    /// Optional kernel-memory charge (ADR 0068) kept equal to the bytes waiting in the buffer (two per
    /// UTF-16 character) and released when the read end closes or the drained write end has closed.
    /// </param>
    public ShellPipe(int capacity = DefaultCapacity, KernelMemoryCharge? charge = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _charge = charge;
        Writer = new PipeWriter(this);
        Reader = new PipeReader(this);
    }

    /// <summary>Gets the write end. Disposing it closes the write end.</summary>
    public TextWriter Writer { get; }

    /// <summary>Gets the read end. Disposing it closes the read end.</summary>
    public TextReader Reader { get; }

    /// <summary>Closes the write end: the reader receives end of stream once the buffer is drained.</summary>
    public void CloseWriter()
    {
        lock (_sync)
        {
            _writerClosed = true;
            _dataAvailable.TrySetResult();
            UpdateCharge();
        }
    }

    /// <summary>Closes the read end: pending and later writes fail with "Broken pipe".</summary>
    public void CloseReader()
    {
        lock (_sync)
        {
            _readerClosed = true;
            _buffer.Clear();
            _spaceAvailable.TrySetResult();
            _charge?.Dispose();
        }
    }

    /// <summary>Keeps the kernel-memory charge equal to the buffered bytes; called under the lock.</summary>
    private void UpdateCharge()
    {
        if (_charge is null)
        {
            return;
        }

        if (_writerClosed && _buffer.Length == 0)
        {
            _charge.Dispose();
        }
        else
        {
            _charge.Resize((long)_buffer.Length * sizeof(char));
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HackIOException BrokenPipe() => new ShellBrokenPipeException();

    /// <summary>Appends without waiting (the synchronous write path).</summary>
    private void WriteNow(ReadOnlySpan<char> text)
    {
        lock (_sync)
        {
            if (_readerClosed)
            {
                throw BrokenPipe();
            }

            if (_writerClosed)
            {
                throw new ObjectDisposedException(nameof(ShellPipe), "The write end of the pipe is closed.");
            }

            _buffer.Append(text);
            _dataAvailable.TrySetResult();
            UpdateCharge();
        }
    }

    /// <summary>Appends, waiting for room whenever the buffer is full.</summary>
    private async Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (_readerClosed)
                {
                    throw BrokenPipe();
                }

                if (_writerClosed)
                {
                    throw new ObjectDisposedException(nameof(ShellPipe), "The write end of the pipe is closed.");
                }

                int room = _capacity - _buffer.Length;
                if (room > 0)
                {
                    int count = Math.Min(room, text.Length);
                    _buffer.Append(text.Span[..count]);
                    _dataAvailable.TrySetResult();
                    UpdateCharge();
                    text = text[count..];
                    if (text.IsEmpty)
                    {
                        return;
                    }
                }

                if (_spaceAvailable.Task.IsCompleted)
                {
                    _spaceAvailable = NewSignal();
                }

                wait = _spaceAvailable.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes up to <paramref name="max"/> characters (or, with <paramref name="untilNewLine"/>,
    /// one line). Returns <see langword="null"/> when nothing can be taken yet, an empty string at
    /// end of stream.
    /// </summary>
    private string? TryTake(int max, bool untilNewLine, bool all)
    {
        if (_buffer.Length == 0)
        {
            return _writerClosed ? string.Empty : null;
        }

        int count;
        if (all)
        {
            if (!_writerClosed)
            {
                return null;
            }

            count = _buffer.Length;
        }
        else if (untilNewLine)
        {
            int newLine = IndexOf(_buffer, '\n');
            if (newLine < 0 && !_writerClosed)
            {
                return null;
            }

            count = newLine < 0 ? _buffer.Length : newLine + 1;
        }
        else
        {
            count = Math.Min(max, _buffer.Length);
        }

        string taken = _buffer.ToString(0, count);
        _buffer.Remove(0, count);
        _spaceAvailable.TrySetResult();
        UpdateCharge();
        return taken;
    }

    private string? TakeNow(int max, bool untilNewLine, bool all, string operation)
    {
        lock (_sync)
        {
            string? taken = TryTake(max, untilNewLine, all);
            return taken ?? throw new InvalidOperationException(
                $"{operation} cannot wait on an empty pipe in the browser's single thread: use the asynchronous form ({operation}Async).");
        }
    }

    private async Task<string> TakeAsync(int max, bool untilNewLine, bool all, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (TryTake(max, untilNewLine, all) is { } taken)
                {
                    return taken;
                }

                if (_dataAvailable.Task.IsCompleted)
                {
                    _dataAvailable = NewSignal();
                }

                wait = _dataAvailable.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private int PeekNow()
    {
        lock (_sync)
        {
            if (_buffer.Length > 0)
            {
                return _buffer[0];
            }

            return _writerClosed
                ? -1
                : throw new InvalidOperationException("Peek cannot wait on an empty pipe in the browser's single thread: use ReadAsync.");
        }
    }

    private static int IndexOf(StringBuilder builder, char value)
    {
        for (int i = 0; i < builder.Length; i++)
        {
            if (builder[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Strips the line terminator from a line taken by <see cref="TryTake"/>; null at end of stream.</summary>
    private static string? ToLine(string taken)
    {
        if (taken.Length == 0)
        {
            return null;
        }

        string line = taken.EndsWith('\n') ? taken[..^1] : taken;
        return line.EndsWith('\r') ? line[..^1] : line;
    }

    /// <summary>The read end. Every async overload is overridden so none falls back to a blocking thread-pool read.</summary>
    private sealed class PipeReader(ShellPipe pipe) : TextReader
    {
        public override int Peek() => pipe.PeekNow();

        public override int Read()
        {
            string taken = pipe.TakeNow(1, untilNewLine: false, all: false, nameof(Read))!;
            return taken.Length == 0 ? -1 : taken[0];
        }

        public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

        public override int Read(Span<char> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            string taken = pipe.TakeNow(buffer.Length, untilNewLine: false, all: false, nameof(Read))!;
            taken.AsSpan().CopyTo(buffer);
            return taken.Length;
        }

        public override string? ReadLine() => ToLine(pipe.TakeNow(0, untilNewLine: true, all: false, nameof(ReadLine))!);

        public override string ReadToEnd() => pipe.TakeNow(0, untilNewLine: false, all: true, nameof(ReadToEnd))!;

        public override Task<int> ReadAsync(char[] buffer, int index, int count) =>
            ReadAsync(buffer.AsMemory(index, count)).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            string taken = await pipe.TakeAsync(buffer.Length, untilNewLine: false, all: false, cancellationToken).ConfigureAwait(false);
            taken.AsSpan().CopyTo(buffer.Span);
            return taken.Length;
        }

        public override Task<int> ReadBlockAsync(char[] buffer, int index, int count) =>
            ReadBlockAsync(buffer.AsMemory(index, count)).AsTask();

        public override async ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ToLine(await pipe.TakeAsync(0, untilNewLine: true, all: false, cancellationToken).ConfigureAwait(false));

        public override Task<string> ReadToEndAsync() => ReadToEndAsync(CancellationToken.None);

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
            pipe.TakeAsync(0, untilNewLine: false, all: true, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            pipe.CloseReader();
            base.Dispose(disposing);
        }
    }

    /// <summary>The write end. Lines end with <c>\n</c>, as on Linux.</summary>
    private sealed class PipeWriter : TextWriter
    {
        private readonly ShellPipe _pipe;

        public PipeWriter(ShellPipe pipe)
        {
            _pipe = pipe;
            CoreNewLine = ['\n'];
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => _pipe.WriteNow([value]);

        public override void Write(char[] buffer, int index, int count) => _pipe.WriteNow(buffer.AsSpan(index, count));

        public override void Write(ReadOnlySpan<char> buffer) => _pipe.WriteNow(buffer);

        public override void Write(string? value) => _pipe.WriteNow(value);

        public override Task WriteAsync(char value) => _pipe.WriteAsync(new[] { value }, CancellationToken.None);

        public override Task WriteAsync(string? value) => _pipe.WriteAsync((value ?? string.Empty).AsMemory(), CancellationToken.None);

        public override Task WriteAsync(char[] buffer, int index, int count) =>
            _pipe.WriteAsync(buffer.AsMemory(index, count), CancellationToken.None);

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            _pipe.WriteAsync(buffer, cancellationToken);

        public override Task WriteLineAsync() => _pipe.WriteAsync(CoreNewLine, CancellationToken.None);

        public override Task WriteLineAsync(char value) => _pipe.WriteAsync(new[] { value, '\n' }, CancellationToken.None);

        public override Task WriteLineAsync(string? value) => _pipe.WriteAsync((value + "\n").AsMemory(), CancellationToken.None);

        public override Task WriteLineAsync(char[] buffer, int index, int count) =>
            _pipe.WriteAsync((new string(buffer, index, count) + "\n").AsMemory(), CancellationToken.None);

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            _pipe.WriteAsync((new string(buffer.Span) + "\n").AsMemory(), cancellationToken);

        public override Task FlushAsync() => Task.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            _pipe.CloseWriter();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// "Broken pipe": a write to a pipe whose reader has exited (ADR 0050, section 4). Commands see an
/// ordinary <see cref="HackIOException"/>; the orchestrator recognizes this type to exit with 141.
/// </summary>
internal sealed class ShellBrokenPipeException() : HackIOException("Broken pipe");
