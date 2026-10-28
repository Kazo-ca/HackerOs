using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Umount;

/// <summary>
/// Implements <c>umount /mnt/name</c> (ADR 0181, section 12): unmounts a network drive. The folder
/// of the computer is not touched; only its owner or an administrator may unmount it. Exit code 32
/// when it fails, as util-linux.
/// </summary>
public sealed class UmountCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string[] operands = [.. context.Arguments.Where(static arg => !arg.StartsWith('-'))];
        if (operands.Length != 1 || operands.Length != context.Arguments.Count)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 1;
        }

        string mountPoint = Mount.MountCommand.Absolute(context, operands[0]);
        try
        {
            await HackDriveInfo.UnmountAsync(mountPoint, cancellationToken);
            return 0;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return 32;
        }
    }
}
