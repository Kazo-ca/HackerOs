using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;

namespace Kazo.Os.Commands.Tee;

/// <summary>
/// <c>tee [-a] [FILE...]</c> (ADR 0064): copies standard input to standard output and to every
/// file as it arrives (<c>-a</c> appends). A file that cannot be opened is reported and the
/// others still get the copy, exit status 1. Files are published when input ends, as the
/// <c>Kazo*</c> streams write atomically on close.
/// </summary>
public sealed class TeeCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool append = false;
        List<string> files = [];
        foreach (string arg in context.Arguments)
        {
            if (arg is "-a" or "--append")
            {
                append = true;
            }
            else if (arg is "-i" or "--ignore-interrupts" or "-p")
            {
                // Accepted for compatibility.
            }
            else
            {
                files.Add(arg);
            }
        }

        int status = 0;
        List<(string Name, StreamWriter Writer)> outputs = [];
        foreach (string file in files)
        {
            try
            {
                KazoFileStream stream = append
                    ? await KazoFile.OpenAppendAsync(file, cancellationToken)
                    : await KazoFile.OpenWriteAsync(file, cancellationToken);
                outputs.Add((file, new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await context.StandardError.WriteLineAsync(CommandText.FileError(context, "tee", file, CommandText.Describe(context, exception)));
                status = 1;
            }
        }

        try
        {
            char[] buffer = new char[4096];
            int read;
            while ((read = await context.StandardInput.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                ReadOnlyMemory<char> chunk = buffer.AsMemory(0, read);
                await context.StandardOutput.WriteAsync(chunk, cancellationToken);
                foreach ((_, StreamWriter writer) in outputs)
                {
                    await writer.WriteAsync(chunk, cancellationToken);
                }
            }
        }
        finally
        {
            foreach ((string name, StreamWriter writer) in outputs)
            {
                try
                {
                    await writer.DisposeAsync();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await context.StandardError.WriteLineAsync(CommandText.FileError(context, "tee", name, CommandText.Describe(context, exception)));
                    status = 1;
                }
            }
        }

        return status;
    }
}
