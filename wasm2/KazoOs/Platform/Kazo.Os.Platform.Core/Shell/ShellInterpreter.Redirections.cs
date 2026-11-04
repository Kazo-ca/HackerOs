using System.Text;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>Redirections (ADR 0050 section 4, extended by ADR 0064): files, descriptor copies and here-documents.</summary>
public sealed partial class ShellInterpreter
{
    private static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Opens a command's redirections left to right, with the shell's identity and capabilities:
    /// <c>&gt;</c> creates or empties (like <c>O_TRUNC|O_CREAT</c>), <c>&gt;&gt;</c> creates or
    /// appends, <c>&lt;</c> reads, <c>n&gt;&amp;m</c> copies a descriptor as it stands at that
    /// point (so <c>&gt;f 2&gt;&amp;1</c> and <c>2&gt;&amp;1 &gt;f</c> differ as in bash), and a
    /// here-document becomes the input. <c>/dev/null</c>, <c>/dev/stdin</c>, <c>/dev/stdout</c>,
    /// <c>/dev/stderr</c> and <c>/dev/tty</c> are the shell's own streams. Returns
    /// <see langword="null"/> after printing <c>hsh: target: reason</c> when one cannot be opened.
    /// </summary>
    private async Task<RedirectionScope?> OpenRedirectionsAsync(Exec exec, IReadOnlyList<ShellRedirection> redirections)
    {
        TextReader input = exec.In;
        TextWriter output = exec.Out;
        TextWriter error = exec.Err;
        RedirectionScope scope = new(exec.Err);
        ShellExpander expander = CreateExpander(exec);

        foreach (ShellRedirection redirection in redirections)
        {
            int descriptor = redirection.EffectiveDescriptor;
            if (descriptor > 2)
            {
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.BadFileDescriptor", descriptor]).ConfigureAwait(false);
                await scope.CloseAsync().ConfigureAwait(false);
                return null;
            }

            switch (redirection.Kind)
            {
                case ShellRedirectionKind.HereDocument:
                    input = new StringReader(await expander.ExpandStringAsync(redirection.Target).ConfigureAwait(false));
                    continue;

                case ShellRedirectionKind.DuplicateOutput or ShellRedirectionKind.DuplicateInput:
                    string source = await expander.ExpandStringAsync(redirection.Target).ConfigureAwait(false);
                    if (redirection.Kind == ShellRedirectionKind.DuplicateInput)
                    {
                        input = source switch
                        {
                            "0" => input,
                            "-" => TextReader.Null,
                            _ => null!
                        };
                        if (input is null)
                        {
                            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.BadFileDescriptor", source]).ConfigureAwait(false);
                            await scope.CloseAsync().ConfigureAwait(false);
                            return null;
                        }

                        continue;
                    }

                    TextWriter? copy = source switch
                    {
                        "1" => output,
                        "2" => error,
                        "-" => TextWriter.Null,
                        _ => null
                    };
                    if (copy is null)
                    {
                        await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.BadFileDescriptor", source]).ConfigureAwait(false);
                        await scope.CloseAsync().ConfigureAwait(false);
                        return null;
                    }

                    if (descriptor == 2)
                    {
                        error = copy;
                    }
                    else
                    {
                        output = copy;
                    }

                    continue;
            }

            List<string> targets = await expander.ExpandFieldsAsync([redirection.Target]).ConfigureAwait(false);
            if (targets.Count != 1)
            {
                await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.AmbiguousRedirect", RawText(redirection.Target)]).ConfigureAwait(false);
                await scope.CloseAsync().ConfigureAwait(false);
                return null;
            }

            string target = targets[0];
            if (redirection.Kind == ShellRedirectionKind.Input)
            {
                TextReader? reader = target switch
                {
                    "/dev/null" => TextReader.Null,
                    "/dev/stdin" => input,
                    _ => await OpenInputAsync(exec, target).ConfigureAwait(false)
                };
                if (reader is null)
                {
                    await scope.CloseAsync().ConfigureAwait(false);
                    return null;
                }

                if (!ReferenceEquals(reader, input) && reader != TextReader.Null)
                {
                    scope.Readers.Add(reader);
                }

                input = reader;
                continue;
            }

            TextWriter? writer = target switch
            {
                "/dev/null" => TextWriter.Null,
                "/dev/stdout" => output,
                "/dev/stderr" => error,
                "/dev/tty" => exec.Options.StandardOutput,
                _ => await OpenOutputAsync(exec, target, redirection.Kind == ShellRedirectionKind.Append, scope).ConfigureAwait(false)
            };
            if (writer is null)
            {
                await scope.CloseAsync().ConfigureAwait(false);
                return null;
            }

            if (descriptor == 2)
            {
                error = writer;
            }
            else
            {
                output = writer;
            }
        }

        scope.Exec = exec with { In = input, Out = output, Err = error };
        return scope;
    }

    /// <summary>Opens a file for reading with the shell's identity; prints the error and returns null when it cannot.</summary>
    private static async Task<TextReader?> OpenInputAsync(Exec exec, string target)
    {
        ShellSession session = exec.Session;
        if (session.Host is null)
        {
            await exec.Err.WriteLineAsync("hsh: " + exec.Text["Shell.NoCallingAppFiles", target]).ConfigureAwait(false);
            return null;
        }

        try
        {
            using (HackExecutionContext.Enter(session.Host, session.CurrentDirectory))
            {
                HackFileStream stream = await HackFile.OpenReadAsync(target, exec.Token).ConfigureAwait(false);
                return new StreamReader(stream, FileEncoding);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.Error", target, ShellText.DescribeError(exception, exec.Text)]).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Opens a file for writing with the shell's identity (ADR 0050 section 4): created or emptied
    /// at once (<c>&gt;</c>), or created and appended (<c>&gt;&gt;</c>), so a permission problem
    /// stops the command before it starts. The file is published when the scope closes.
    /// </summary>
    private static async Task<TextWriter?> OpenOutputAsync(Exec exec, string target, bool append, RedirectionScope scope)
    {
        ShellSession session = exec.Session;
        if (session.Host is null)
        {
            await exec.Err.WriteLineAsync("hsh: " + exec.Text["Shell.NoCallingAppFiles", target]).ConfigureAwait(false);
            return null;
        }

        HackFileStream? stream = null;
        try
        {
            using (HackExecutionContext.Enter(session.Host, session.CurrentDirectory))
            {
                if (append)
                {
                    // The engine has no append mode to check at open: an empty append creates the
                    // file or proves it writable, so a permission problem stops the stage first.
                    await HackFile.AppendAllTextAsync(target, string.Empty, exec.Token).ConfigureAwait(false);
                    stream = await HackFile.OpenAppendAsync(target, exec.Token).ConfigureAwait(false);
                }
                else
                {
                    stream = await HackFile.OpenWriteAsync(target, exec.Token).ConfigureAwait(false);
                }
            }

            // Truncation (or creation) happens at open, like O_TRUNC|O_CREAT.
            await stream.FlushAsync(exec.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (stream is not null)
            {
                await DisposeQuietlyAsync(stream).ConfigureAwait(false);
            }

            await exec.Err.WriteLineAsync(ErrorPrefix(exec) + exec.Text["Shell.Error", target, ShellText.DescribeError(exception, exec.Text)]).ConfigureAwait(false);
            return null;
        }

        StreamWriter writer = new(stream, FileEncoding) { NewLine = "\n" };
        scope.Writers.Add((target, writer));
        return writer;
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more was written to it; the open already reported what mattered.
        }
    }

    /// <summary>The source text of a word for error messages.</summary>
    private static string RawText(ShellWord word) =>
        string.Concat(word.Parts.Select(part => part switch
        {
            ShellLiteralPart literal => literal.Text,
            ShellVariablePart variable => "$" + variable.Name,
            ShellSpecialParameterPart special => "$" + special.Name,
            ShellParameterExpansionPart expansion => "${" + expansion.Name + "}",
            ShellTildePart => "~",
            _ => "$(…)"
        }));

    /// <summary>The files a command's redirections opened, published (closed) when it ends.</summary>
    private sealed class RedirectionScope(TextWriter error)
    {
        public Exec Exec { get; set; } = null!;

        public List<(string Target, StreamWriter Writer)> Writers { get; } = [];

        public List<TextReader> Readers { get; } = [];

        /// <summary>Publishes every file; prints <c>hsh: target: reason</c> and returns false if one fails.</summary>
        public async Task<bool> CloseAsync()
        {
            bool success = true;
            foreach ((string target, StreamWriter writer) in Writers)
            {
                try
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Microsoft.Extensions.Localization.IStringLocalizer texts = Exec is null ? ShellText.English : Exec.Text;
                    await error.WriteLineAsync("hsh: " + texts["Shell.Error", target, ShellText.DescribeError(exception, texts)]).ConfigureAwait(false);
                    success = false;
                }
            }

            foreach (TextReader reader in Readers)
            {
                reader.Dispose();
            }

            Writers.Clear();
            Readers.Clear();
            return success;
        }
    }
}
