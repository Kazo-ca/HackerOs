using System.Text;

namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// The terminal end of a command's standard output or error (ADR 0050, section 4): every write
/// is handed to the terminal at once, so output appears as the command produces it instead of
/// when it exits. Lines end with <c>\n</c>, as on Linux.
/// </summary>
/// <remarks>
/// Every asynchronous overload completes synchronously: the base <see cref="TextWriter"/> would
/// otherwise queue a thread-pool work item per write, which the browser's single thread cannot
/// run in parallel anyway.
/// </remarks>
public sealed class TerminalOutputWriter : TextWriter
{
    private readonly Action<string> _append;

    /// <summary>Creates a writer that forwards every chunk of text to <paramref name="append"/>.</summary>
    /// <param name="append">Receives each written chunk, possibly a partial line or several lines.</param>
    public TerminalOutputWriter(Action<string> append)
    {
        _append = append ?? throw new ArgumentNullException(nameof(append));
        CoreNewLine = ['\n'];
    }

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <inheritdoc />
    public override void Write(char value) => _append(value.ToString());

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _append(value);
        }
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer) => Write(new string(buffer));

    /// <inheritdoc />
    public override Task WriteAsync(char value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteAsync(char[] buffer, int index, int count)
    {
        Write(buffer, index, count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteLineAsync()
    {
        WriteLine();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteLineAsync(char value)
    {
        Write(value + "\n");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteLineAsync(string? value)
    {
        Write(value + "\n");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteLineAsync(char[] buffer, int index, int count)
    {
        Write(new string(buffer, index, count) + "\n");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Write(new string(buffer.Span) + "\n");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task FlushAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
