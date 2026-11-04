using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;

namespace Contoso.Sdk3Compat;

/// <summary>
/// <c>sdk3-compat [text]</c>: writes the text to <c>~/sdk3-compat.txt</c>, copies the file into the
/// same folder with the SDK 3.0 copy rules (<see cref="KazoFileOperations.CopyAsync"/>, which names
/// the copy <c>sdk3-compat (copy).txt</c>), reads the copy back and prints it with the SDK versions,
/// so a test sees that the 2.0 additions, renamed in 3.0, still bind in the current OS.
/// </summary>
public sealed class CompatCommand : TerminalAppBase
{
    /// <summary>Creates the command for the manifest the OS loaded.</summary>
    /// <param name="manifest">This app's manifest.</param>
    public CompatCommand(AppManifest manifest)
        : base(manifest)
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string text = context.Arguments.Count > 0 ? string.Join(' ', context.Arguments) : "hello";
        string home = context.App.HomeDirectory;
        string note = home + "/sdk3-compat.txt";
        await KazoFile.WriteAllTextAsync(note, text, cancellationToken);
        KazoFileTransferResult copied = await KazoFileOperations.CopyAsync([note], home, cancellationToken);
        if (!copied.Succeeded)
        {
            await context.StandardError.WriteLineAsync(copied.DescribeFailures("copy").AsMemory(), cancellationToken);
            return 1;
        }

        string copy = copied.Completed[0].DestinationPath;
        string readBack = await KazoFile.ReadAllTextAsync(copy, cancellationToken);
        await context.StandardOutput.WriteLineAsync(
            $"sdk3-compat: {readBack} [{KazoPath.GetFileName(copy)}] (compiled against {CompatInfo.CompiledAgainst}, running on {KazoOsSdk.Version})".AsMemory(),
            cancellationToken);
        return 0;
    }
}
