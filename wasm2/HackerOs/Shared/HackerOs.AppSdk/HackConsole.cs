namespace HackerOs.AppSdk;

/// <summary>
/// The standard streams of one app instance (ADR 0064): the terminal (or pipe, or redirected
/// file) of the shell that started it, or nothing when it was started from the desktop. A
/// window app started from a shell writes to <see cref="Out"/> and the text appears in that
/// terminal, as a Linux GUI program's <c>printf</c> does.
/// </summary>
public interface IAppConsoleGateway
{
    /// <summary>Gets whether the instance was started with streams (by a shell); otherwise every stream is empty.</summary>
    bool IsAttached { get; }

    /// <summary>Gets the standard input (end of stream at once when detached).</summary>
    TextReader In { get; }

    /// <summary>Gets the standard output (discarded when detached).</summary>
    TextWriter Out { get; }

    /// <summary>Gets the standard error (discarded when detached).</summary>
    TextWriter Error { get; }
}

/// <summary>The streams of an instance started without a shell: <c>/dev/null</c> for all three.</summary>
public sealed class DetachedAppConsoleGateway : IAppConsoleGateway
{
    /// <summary>Gets the shared detached console.</summary>
    public static DetachedAppConsoleGateway Instance { get; } = new();

    /// <inheritdoc />
    public bool IsAttached => false;

    /// <inheritdoc />
    public TextReader In => TextReader.Null;

    /// <inheritdoc />
    public TextWriter Out => TextWriter.Null;

    /// <inheritdoc />
    public TextWriter Error => TextWriter.Null;
}

/// <summary>
/// <c>System.Console</c> for Kazo OS apps (ADR 0064): the standard streams of the app running
/// in the ambient <see cref="HackExecutionContext"/>. A terminal command gets the same streams as
/// <see cref="TerminalExecutionContext.StandardOutput"/>; a window app started from a shell
/// (<c>text-editor notes.txt</c>) writes into that shell's terminal; an app started from the
/// desktop writes nowhere.
/// </summary>
/// <example>
/// <code>
/// HackConsole.WriteLine($"Opened {path}");   // visible in the terminal that launched the app
/// </code>
/// </example>
public static class HackConsole
{
    private static IAppConsoleGateway Console => HackExecutionContext.Current.Console;

    /// <summary>Gets whether the running app has a terminal (or pipe, or file) attached.</summary>
    public static bool IsAttached => HackExecutionContext.CurrentOrNull?.Console.IsAttached ?? false;

    /// <summary>Gets the standard input.</summary>
    public static TextReader In => Console.In;

    /// <summary>Gets the standard output.</summary>
    public static TextWriter Out => Console.Out;

    /// <summary>Gets the standard error.</summary>
    public static TextWriter Error => Console.Error;

    /// <summary>Writes text to the standard output.</summary>
    public static void Write(string? value) => Out.Write(value);

    /// <summary>Writes a line to the standard output.</summary>
    public static void WriteLine(string? value = null) => Out.WriteLine(value);

    /// <summary>Writes a line to the standard output, waiting for room in a pipe.</summary>
    public static Task WriteLineAsync(string? value = null) => Out.WriteLineAsync(value);

    /// <summary>Reads one line of the standard input; <see langword="null"/> at end of input.</summary>
    public static ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) => In.ReadLineAsync(cancellationToken);
}
