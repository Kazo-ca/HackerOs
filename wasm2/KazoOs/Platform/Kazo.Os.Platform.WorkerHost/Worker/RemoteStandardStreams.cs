using System.Text;
using System.Text.Json;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.WorkerHost.Protocol;

namespace Kazo.Os.Platform.WorkerHost.Worker;

/// <summary>
/// The standard output and error of one process in a worker (ADR 0070, section 4: "les flux standard
/// sont des tubes par messages"). Text is buffered and sent as <c>out</c> messages, in write order
/// across both streams, at each line end, before every remote call, and when the process ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flow control.</b> A synchronous <c>Write</c> never waits: a worker cannot block on a message.
/// <c>WriteAsync</c> and <c>FlushAsync</c> wait while more than <see cref="MaxCharactersInFlight"/>
/// characters are unacknowledged, like a writer blocking on a full pipe.
/// </para>
/// <para>
/// <b>Broken pipe.</b> When the OS reports that the reader of a stream went away, the next write
/// throws <see cref="RemoteBrokenPipeException"/>, the "Broken pipe" error of a local pipe.
/// </para>
/// </remarks>
internal sealed class RemoteProcessOutput
{
    /// <summary>Characters the process may have in flight before <c>WriteAsync</c> waits for the OS.</summary>
    public const int MaxCharactersInFlight = 64 * 1024;

    private const int FlushThreshold = 8 * 1024;

    private readonly long _pid;
    private readonly Action<string> _post;
    private readonly Lock _gate = new();
    private readonly StringBuilder _pending = new();
    private int _pendingStream;
    private long _inFlight;
    private bool _stdoutClosed;
    private bool _stderrClosed;
    private TaskCompletionSource? _creditWaiter;

    /// <summary>Initializes the output of process <paramref name="pid"/>.</summary>
    public RemoteProcessOutput(long pid, Action<string> post)
    {
        _pid = pid;
        _post = post;
        StandardOutput = new StreamWriter(this, 1);
        StandardError = new StreamWriter(this, 2);
    }

    /// <summary>Gets file descriptor 1.</summary>
    public TextWriter StandardOutput { get; }

    /// <summary>Gets file descriptor 2.</summary>
    public TextWriter StandardError { get; }

    /// <summary>Sends whatever is buffered.</summary>
    public void Flush()
    {
        string? text;
        int stream;
        lock (_gate)
        {
            (text, stream) = TakePending();
        }

        Send(stream, text);
    }

    /// <summary>The OS wrote <paramref name="characters"/> of this process's output.</summary>
    public void Acknowledge(long characters)
    {
        TaskCompletionSource? waiter = null;
        lock (_gate)
        {
            _inFlight = Math.Max(0, _inFlight - characters);
            if (_inFlight <= MaxCharactersInFlight)
            {
                waiter = _creditWaiter;
                _creditWaiter = null;
            }
        }

        waiter?.TrySetResult();
    }

    /// <summary>The reader of stream <paramref name="stream"/> went away (broken pipe).</summary>
    public void Close(int stream)
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            if (stream == 1)
            {
                _stdoutClosed = true;
            }
            else
            {
                _stderrClosed = true;
            }

            waiter = _creditWaiter;
            _creditWaiter = null;
        }

        waiter?.TrySetResult();
    }

    /// <summary>Releases a writer waiting for credit (the process ends or is killed).</summary>
    public void Abort()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            waiter = _creditWaiter;
            _creditWaiter = null;
        }

        waiter?.TrySetResult();
    }

    private void Write(int stream, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        string? ready = null;
        int readyStream = 0;
        bool flushNow;
        lock (_gate)
        {
            ThrowIfClosed(stream);
            if (_pending.Length > 0 && _pendingStream != stream)
            {
                // Keep the interleaving of stdout and stderr: send the other stream's text first.
                (ready, readyStream) = TakePending();
            }

            _pendingStream = stream;
            _pending.Append(text);
            flushNow = text.Contains('\n') || _pending.Length >= FlushThreshold;
        }

        Send(readyStream, ready);
        if (flushNow)
        {
            Flush();
        }
    }

    private async Task WriteAsync(int stream, ReadOnlyMemory<char> text, CancellationToken cancellationToken)
    {
        Write(stream, text.Span);
        await WaitForCreditAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushAsync(int stream, CancellationToken cancellationToken)
    {
        Flush();
        await WaitForCreditAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForCreditAsync(int stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                ThrowIfClosed(stream);
                if (_inFlight <= MaxCharactersInFlight)
                {
                    return;
                }

                _creditWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _creditWaiter.Task;
            }

            Flush();
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private (string? Text, int Stream) TakePending()
    {
        if (_pending.Length == 0)
        {
            return (null, 0);
        }

        string text = _pending.ToString();
        _pending.Clear();
        _inFlight += text.Length;
        return (text, _pendingStream);
    }

    private void Send(int stream, string? text)
    {
        if (text is null)
        {
            return;
        }

        _post(WorkerMessage.Create(WorkerMessageType.Output, writer =>
        {
            writer.WriteNumber("p", _pid);
            writer.WriteNumber("f", stream);
            writer.WriteString("d", text);
        }));
    }

    private void ThrowIfClosed(int stream)
    {
        if (stream == 1 ? _stdoutClosed : _stderrClosed)
        {
            throw new RemoteBrokenPipeException();
        }
    }

    /// <summary>One standard stream as a <see cref="TextWriter"/>.</summary>
    private sealed class StreamWriter(RemoteProcessOutput owner, int stream) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => owner.Write(stream, [value]);

        public override void Write(char[] buffer, int index, int count) => owner.Write(stream, buffer.AsSpan(index, count));

        public override void Write(ReadOnlySpan<char> buffer) => owner.Write(stream, buffer);

        public override void Write(string? value) => owner.Write(stream, value);

        public override void WriteLine(string? value)
        {
            owner.Write(stream, value);
            owner.Write(stream, CoreNewLine);
        }

        public override Task WriteAsync(char value) => owner.WriteAsync(stream, new[] { value }, CancellationToken.None);

        public override Task WriteAsync(string? value) => owner.WriteAsync(stream, (value ?? string.Empty).AsMemory(), CancellationToken.None);

        public override Task WriteAsync(char[] buffer, int index, int count) =>
            owner.WriteAsync(stream, buffer.AsMemory(index, count), CancellationToken.None);

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            owner.WriteAsync(stream, buffer, cancellationToken);

        public override Task WriteLineAsync() => owner.WriteAsync(stream, CoreNewLine, CancellationToken.None);

        public override Task WriteLineAsync(char value) => WriteLineAsync(value.ToString());

        public override Task WriteLineAsync(string? value) =>
            owner.WriteAsync(stream, string.Concat(value, new string(CoreNewLine)).AsMemory(), CancellationToken.None);

        public override Task WriteLineAsync(char[] buffer, int index, int count) =>
            WriteLineAsync(new string(buffer, index, count));

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            owner.WriteAsync(stream, string.Concat(buffer.Span, CoreNewLine).AsMemory(), cancellationToken);

        public override void Flush() => owner.Flush();

        public override Task FlushAsync() => owner.FlushAsync(stream, CancellationToken.None);

        public override Task FlushAsync(CancellationToken cancellationToken) => owner.FlushAsync(stream, cancellationToken);
    }
}

/// <summary>
/// The standard input of one process in a worker: text read from the shell's pipe or terminal on
/// demand, with an <c>in.read</c> call per chunk.
/// </summary>
/// <remarks>
/// Only the asynchronous reads can wait for the OS: a synchronous read served from what is already
/// buffered works, but one that would need the OS throws, since a worker's thread cannot block on a
/// message. Every built-in command reads its input asynchronously.
/// </remarks>
internal sealed class RemoteStandardInput : TextReader
{
    private const int ChunkSize = 16 * 1024;

    private readonly Func<int, CancellationToken, Task<string?>> _readChunk;
    private string _buffer = string.Empty;
    private int _position;
    private bool _endOfStream;

    /// <summary>Initializes the reader.</summary>
    /// <param name="readChunk">Asks the OS for at most that many characters; <see langword="null"/> at the end of the stream.</param>
    public RemoteStandardInput(Func<int, CancellationToken, Task<string?>> readChunk) => _readChunk = readChunk;

    private int Available => _buffer.Length - _position;

    public override int Peek()
    {
        EnsureBufferedSynchronously();
        return Available > 0 ? _buffer[_position] : -1;
    }

    public override int Read()
    {
        EnsureBufferedSynchronously();
        return Available > 0 ? _buffer[_position++] : -1;
    }

    public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

    public override int Read(Span<char> buffer)
    {
        EnsureBufferedSynchronously();
        return Take(buffer);
    }

    public override string? ReadLine()
    {
        EnsureBufferedSynchronously();
        int newline = _buffer.IndexOf('\n', _position);
        if (newline < 0 && !_endOfStream)
        {
            throw SynchronousReadUnsupported();
        }

        return TakeLine(newline);
    }

    public override string ReadToEnd()
    {
        if (!_endOfStream)
        {
            throw SynchronousReadUnsupported();
        }

        string rest = _buffer[_position..];
        _position = _buffer.Length;
        return rest;
    }

    public override Task<int> ReadAsync(char[] buffer, int index, int count) =>
        ReadAsync(buffer.AsMemory(index, count)).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        await FillAsync(cancellationToken).ConfigureAwait(false);
        return Take(buffer.Span);
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

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = _buffer.IndexOf('\n', _position);
            if (newline >= 0 || _endOfStream)
            {
                return TakeLine(newline);
            }

            await ReadMoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<string> ReadToEndAsync() => ReadToEndAsync(CancellationToken.None);

    public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
    {
        while (!_endOfStream)
        {
            await ReadMoreAsync(cancellationToken).ConfigureAwait(false);
        }

        string rest = _buffer[_position..];
        _position = _buffer.Length;
        return rest;
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (Available == 0 && !_endOfStream)
        {
            await ReadMoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadMoreAsync(CancellationToken cancellationToken)
    {
        string? chunk = await _readChunk(ChunkSize, cancellationToken).ConfigureAwait(false);
        if (chunk is null)
        {
            _endOfStream = true;
            return;
        }

        _buffer = _position < _buffer.Length ? string.Concat(_buffer.AsSpan(_position), chunk) : chunk;
        _position = 0;
    }

    private int Take(Span<char> destination)
    {
        int count = Math.Min(destination.Length, Available);
        _buffer.AsSpan(_position, count).CopyTo(destination);
        _position += count;
        return count;
    }

    private string? TakeLine(int newline)
    {
        if (newline < 0)
        {
            if (Available == 0)
            {
                return null;
            }

            string last = _buffer[_position..];
            _position = _buffer.Length;
            return last;
        }

        int end = newline > _position && _buffer[newline - 1] == '\r' ? newline - 1 : newline;
        string line = _buffer[_position..end];
        _position = newline + 1;
        return line;
    }

    private void EnsureBufferedSynchronously()
    {
        if (Available == 0 && !_endOfStream)
        {
            throw SynchronousReadUnsupported();
        }
    }

    private static InvalidOperationException SynchronousReadUnsupported() =>
        new("A process in a worker reads its standard input asynchronously (ReadAsync, ReadLineAsync): " +
            "its thread cannot block waiting for the OS (ADR 0070).");
}

/// <summary>
/// The reader of a process's output went away: the "Broken pipe" error a write to a closed pipe
/// gives in-process (ADR 0050, section 4). Escaping the entry point, it ends the process with 141.
/// </summary>
public sealed class RemoteBrokenPipeException : HackIOException
{
    /// <summary>Initializes the exception.</summary>
    public RemoteBrokenPipeException()
        : base("Broken pipe")
    {
    }
}

/// <summary>Reads the <c>in.read</c> answer: <c>d</c> (text) or <c>eof</c>.</summary>
internal static class StandardInputWire
{
    public static string? ReadChunk(JsonElement value) => value.GetFlag("eof") ? null : value.GetString("d");
}
