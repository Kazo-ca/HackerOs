using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.IO;

namespace Contoso.Sdk11Compat;

/// <summary>
/// <c>sdk11-compat [text]</c>: writes the text to <c>~/sdk11-compat.txt</c>, reads it back and prints
/// it with the SDK versions and the verdict of the SDK 1.1 password rule
/// (<see cref="HackUserAccounts.ValidateNewPassword"/>) on that text, so a test sees that the 1.1
/// additions still bind in the current OS.
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
        await HackFile.WriteAllTextAsync("~/sdk11-compat.txt", text, cancellationToken);
        string readBack = await HackFile.ReadAllTextAsync("~/sdk11-compat.txt", cancellationToken);
        string password = HackUserAccounts.ValidateNewPassword(readBack) ?? "valid password";
        await context.StandardOutput.WriteLineAsync(
            $"sdk11-compat: {readBack} [{password}] (compiled against {CompatInfo.CompiledAgainst}, running on {HackerOsSdk.Version})".AsMemory(),
            cancellationToken);
        return 0;
    }
}
