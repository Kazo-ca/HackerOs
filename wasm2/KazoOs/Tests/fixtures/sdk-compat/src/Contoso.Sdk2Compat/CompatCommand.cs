using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.IO;

namespace Contoso.Sdk2Compat;

/// <summary>
/// <c>sdk2-compat [text]</c>: writes the text to <c>~/sdk2-compat.txt</c>, copies the file into the
/// same folder with the SDK 2.0 copy rules (<see cref="HackFileOperations.CopyAsync"/>, which names
/// the copy <c>sdk2-compat (copy).txt</c>), reads the copy back and prints it with the SDK versions,
/// so a test sees that the 2.0 additions still bind in the current OS.
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
        string note = home + "/sdk2-compat.txt";
        await HackFile.WriteAllTextAsync(note, text, cancellationToken);
        HackFileTransferResult copied = await HackFileOperations.CopyAsync([note], home, cancellationToken);
        if (!copied.Succeeded)
        {
            await context.StandardError.WriteLineAsync(copied.DescribeFailures("copy").AsMemory(), cancellationToken);
            return 1;
        }

        string copy = copied.Completed[0].DestinationPath;
        string readBack = await HackFile.ReadAllTextAsync(copy, cancellationToken);
        await context.StandardOutput.WriteLineAsync(
            $"sdk2-compat: {readBack} [{HackPath.GetFileName(copy)}] (compiled against {CompatInfo.CompiledAgainst}, running on {HackerOsSdk.Version})".AsMemory(),
            cancellationToken);
        return 0;
    }
}
