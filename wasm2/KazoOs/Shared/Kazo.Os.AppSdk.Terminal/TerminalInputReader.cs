using System.Text;
using Kazo.Os.AppSdk;

namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// The standard input a terminal gives the command line it runs (ADR 0064): a tty in canonical
/// mode. The terminal's line discipline edits and echoes what is typed and pushes each finished
/// line (Enter) here; Ctrl+D on an empty line is an end of input that the next read reports once,
/// as on Linux (<c>cat</c> then ends, a later read waits again). When the command line ends, the
/// input it did not read goes back to the shell as type-ahead.
/// </summary>
public sealed class TerminalInputReader : TextReader, ITerminalInputMode
{
    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _endOfInput;
    private bool _closed;

    /// <inheritdoc />
    /// <remarks>The terminal's line discipline reads it for every key (ADR 0120); on by default.</remarks>
    public bool EchoEnabled { get; set; } = true;

    /// <summary>Gets whether a command is waiting for input (the terminal may show it differently).</summary>
    public bool IsWaiting { get; private set; }

    /// <summary>Adds typed text (a finished line with its newline, or a partial line pushed by Ctrl+D).</summary>
    public void Push(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _buffer.Append(text);
            Signal();
        }
    }

    /// <summary>Ctrl+D on an empty line: the next read returns end of input, once.</summary>
    public void PushEndOfInput()
    {
        lock (_sync)
        {
            _endOfInput = true;
            Signal();
        }
    }

    /// <summary>The command line ended: readers see end of input from now on.</summary>
    public void CloseInput()
    {
        lock (_sync)
        {
            _closed = true;
            Signal();
        }
    }

    /// <summary>Takes what was typed but never read, to hand back to the shell as type-ahead.</summary>
    public string TakeUnread()
    {
        lock (_sync)
        {
            string text = _buffer.ToString();
            _buffer.Clear();
            return text;
        }
    }

    /// <inheritdoc />
    public override int Peek()
    {
        lock (_sync)
        {
            return _buffer.Length > 0 ? _buffer[0] : -1;
        }
    }

    /// <inheritdoc />
    /// <remarks>A synchronous read cannot wait for the keyboard in the browser: it only takes what is already typed.</remarks>
    public override int Read()
    {
        lock (_sync)
        {
            if (_buffer.Length == 0)
            {
                _endOfInput = false;
                return -1;
            }

            char ch = _buffer[0];
            _buffer.Remove(0, 1);
            return ch;
        }
    }

    /// <inheritdoc />
    public override int Read(Span<char> buffer)
    {
        lock (_sync)
        {
            int count = Math.Min(buffer.Length, _buffer.Length);
            for (int i = 0; i < count; i++)
            {
                buffer[i] = _buffer[i];
            }

            _buffer.Remove(0, count);
            if (count == 0)
            {
                _endOfInput = false;
            }

            return count;
        }
    }

    /// <inheritdoc />
    public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

    /// <inheritdoc />
    public override string? ReadLine()
    {
        lock (_sync)
        {
            return TakeLine(atEnd: true);
        }
    }

    /// <inheritdoc />
    public override string ReadToEnd()
    {
        lock (_sync)
        {
            string text = _buffer.ToString();
            _buffer.Clear();
            _endOfInput = false;
            return text;
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_buffer.Length > 0)
                {
                    IsWaiting = false;
                    return Read(buffer.Span);
                }

                if (TakeEnd())
                {
                    return 0;
                }

                IsWaiting = true;
                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(char[] buffer, int index, int count) =>
        ReadAsync(buffer.AsMemory(index, count)).AsTask();

    /// <inheritdoc />
    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    /// <inheritdoc />
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                bool end = _endOfInput || _closed;
                if (TakeLine(atEnd: end) is { } line)
                {
                    IsWaiting = false;
                    if (end && !_closed && _buffer.Length == 0)
                    {
                        // A partial line ended by Ctrl+D is returned; the end itself is consumed.
                        _endOfInput = false;
                    }

                    return line;
                }

                if (TakeEnd())
                {
                    return null;
                }

                IsWaiting = true;
                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task<string> ReadToEndAsync() => ReadToEndAsync(CancellationToken.None);

    /// <inheritdoc />
    public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        char[] chunk = new char[256];
        int read;
        while ((read = await ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            text.Append(chunk, 0, read);
        }

        return text.ToString();
    }

    /// <summary>Consumes a pending end of input; a closed reader stays at end of input.</summary>
    private bool TakeEnd()
    {
        if (_closed)
        {
            return true;
        }

        if (_endOfInput)
        {
            _endOfInput = false;
            return true;
        }

        return false;
    }

    /// <summary>Takes one line (without its newline); at end of input, the partial line left.</summary>
    private string? TakeLine(bool atEnd)
    {
        for (int i = 0; i < _buffer.Length; i++)
        {
            if (_buffer[i] == '\n')
            {
                string line = _buffer.ToString(0, i);
                _buffer.Remove(0, i + 1);
                return line;
            }
        }

        if (atEnd && _buffer.Length > 0)
        {
            string rest = _buffer.ToString();
            _buffer.Clear();
            return rest;
        }

        return null;
    }

    private void Signal()
    {
        TaskCompletionSource changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }
}
