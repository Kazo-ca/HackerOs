using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.IO;

namespace Contoso.Sdk1Compat;

/// <summary>
/// <c>sdk1-compat [text]</c>: writes the text to <c>~/sdk1-compat.txt</c>, reads it back and prints
/// it with the SDK versions, so a test sees that file I/O, arguments and standard output of SDK 1.0
/// still bind in the current OS.
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
        await HackFile.WriteAllTextAsync("~/sdk1-compat.txt", text, cancellationToken);
        string readBack = await HackFile.ReadAllTextAsync("~/sdk1-compat.txt", cancellationToken);
        await context.StandardOutput.WriteLineAsync(
            $"sdk1-compat: {readBack} (compiled against {CompatInfo.CompiledAgainst}, running on {HackerOsSdk.Version})".AsMemory(),
            cancellationToken);
        return 0;
    }
}
