using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.Commands.Common;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Hackpkg;

/// <summary>
/// <c>hackpkg install | remove | update | list | info | sources</c> (ADR 0182, section 4): installs
/// apps from their GitHub repository through <see cref="HackAppPackages"/>.
/// </summary>
/// <remarks>
/// <para>
/// Like <c>apt</c>, an install, an update or a removal changes the machine: the OS asks an
/// administrator (or nothing under <c>sudo</c>). <c>list</c>, <c>info</c> and <c>sources</c> only read.
/// </para>
/// <para>
/// The OS catalog is built at startup (ADR 0182, Q5): an installed app appears after a restart of
/// HackerOS, which the command says.
/// </para>
/// </remarks>
public sealed class HackpkgCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    private const string AllowUntrustedOption = "--allow-untrusted";

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            await (args.Count == 0 ? context.StandardError : context.StandardOutput).WriteLineAsync(text["Usage"]);
            return args.Count == 0 ? 2 : 0;
        }

        string verb = args[0];
        List<string> operands = [.. args.Skip(1).Where(static arg => arg != AllowUntrustedOption)];
        bool allowUntrusted = args.Skip(1).Contains(AllowUntrustedOption);
        string? invalid = operands.FirstOrDefault(static arg => arg.StartsWith('-'));
        if (invalid is not null || (allowUntrusted && verb != "install"))
        {
            await context.StandardError.WriteLineAsync(text["InvalidOption", invalid ?? AllowUntrustedOption]);
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        int? expected = verb switch
        {
            "install" or "remove" or "info" => 1,
            "list" or "sources" => 0,
            "update" => operands.Count <= 1 ? operands.Count : -1,
            _ => null
        };
        if (expected is null)
        {
            await context.StandardError.WriteLineAsync(text["UnknownCommand", verb]);
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        if (operands.Count != expected)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        try
        {
            return verb switch
            {
                "install" => await InstallAsync(context, operands[0], allowUntrusted, cancellationToken),
                "remove" => await RemoveAsync(context, operands[0], cancellationToken),
                "update" => await UpdateAsync(context, operands.Count == 0 ? null : operands[0], cancellationToken),
                "list" => await ListAsync(context, cancellationToken),
                "info" => await InfoAsync(context, operands[0], cancellationToken),
                _ => await SourcesAsync(context, cancellationToken)
            };
        }
        catch (HackPackageException exception)
        {
            await context.StandardError.WriteLineAsync($"hackpkg: {exception.Message}");
            return 1;
        }
        catch (NotSupportedException)
        {
            await context.StandardError.WriteLineAsync(text["Unsupported"]);
            return 1;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            await context.StandardError.WriteLineAsync($"hackpkg: {CommandText.Describe(context, exception)}");
            return 1;
        }
    }

    private static async Task<int> InstallAsync(
        TerminalExecutionContext context, string source, bool allowUntrusted, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        await context.StandardOutput.WriteLineAsync(text["Downloading", source]);
        HackPackageInstallReport report = await HackAppPackages.InstallAsync(source, allowUntrusted, cancellationToken);
        await WriteReportAsync(context, report);
        return 0;
    }

    private static async Task WriteReportAsync(TerminalExecutionContext context, HackPackageInstallReport report)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        if (!report.Trusted)
        {
            await context.StandardError.WriteLineAsync(text["Untrusted", report.Source]);
        }

        await context.StandardOutput.WriteLineAsync(report.PreviousVersion is null
            ? text["Installed", report.Name, report.Version, report.Source]
            : text["Updated", report.Name, report.PreviousVersion, report.Version]);
        if (report.Capabilities.Count > 0)
        {
            await context.StandardOutput.WriteLineAsync(text[
                "Capabilities",
                string.Join(", ", report.Capabilities.Select(static capability =>
                    AppCapabilities.IsDangerous(capability) ? $"{capability} (!)" : capability))]);
            if (report.Capabilities.Any(AppCapabilities.IsDangerous))
            {
                await context.StandardOutput.WriteLineAsync(text["DangerousNote"]);
            }
        }

        await context.StandardOutput.WriteLineAsync(text["RestartRequired"]);
    }

    private static async Task<int> RemoveAsync(TerminalExecutionContext context, string appId, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        HackInstalledPackage removed = await HackAppPackages.RemoveAsync(appId, cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["Removed", removed.AppId, removed.Version]);
        return 0;
    }

    private static async Task<int> UpdateAsync(TerminalExecutionContext context, string? appId, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<HackInstalledPackage> installed = await HackAppPackages.GetInstalledAsync(cancellationToken);
        IReadOnlyList<HackInstalledPackage> targets = appId is null
            ? installed
            : [.. installed.Where(package => package.AppId == appId)];
        if (appId is not null && targets.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["NotInstalled", appId]);
            return 1;
        }

        if (targets.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync(text["NothingInstalled"]);
            return 0;
        }

        foreach (HackInstalledPackage package in targets)
        {
            HackPackageInfo info = await HackAppPackages.GetInfoAsync(package.Source, cancellationToken);
            if (info.Latest is null || !IsNewer(info.Latest, package.Version))
            {
                await context.StandardOutput.WriteLineAsync(text["UpToDate", package.AppId, package.Version]);
                continue;
            }

            await context.StandardOutput.WriteLineAsync(text["Downloading", $"{package.Source}@{info.Latest}"]);
            // An installed source was accepted once; an owner removed from [trusted] since is asked again.
            HackPackageInstallReport report = await HackAppPackages.InstallAsync(
                $"{package.Source}@{info.Latest}", allowUntrusted: false, cancellationToken);
            await WriteReportAsync(context, report);
        }

        return 0;
    }

    private static bool IsNewer(string candidate, string current) =>
        SemanticVersion.TryParse(candidate, out SemanticVersion next)
        && (!SemanticVersion.TryParse(current, out SemanticVersion installed) || next.CompareTo(installed) > 0);

    private static async Task<int> ListAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<HackInstalledPackage> installed = await HackAppPackages.GetInstalledAsync(cancellationToken);
        if (installed.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync(text["NothingInstalled"]);
            return 0;
        }

        int idWidth = installed.Max(static package => package.AppId.Length);
        int versionWidth = installed.Max(static package => package.Version.Length);
        foreach (HackInstalledPackage package in installed)
        {
            string pending = package.Loaded ? string.Empty : $" {text["PendingRestart"]}";
            await context.StandardOutput.WriteLineAsync(
                $"{package.AppId.PadRight(idWidth)}  {package.Version.PadRight(versionWidth)}  {package.Source}{pending}");
        }

        return 0;
    }

    private static async Task<int> InfoAsync(TerminalExecutionContext context, string source, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        HackPackageInfo info = await HackAppPackages.GetInfoAsync(source, cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["InfoSource", info.Source]);
        await context.StandardOutput.WriteLineAsync(text["InfoApp", info.AppId]);
        await context.StandardOutput.WriteLineAsync(text["InfoTrusted", info.Trusted ? text["Yes"] : text["No"]]);
        await context.StandardOutput.WriteLineAsync(text["InfoLatest", info.Latest ?? "-"]);
        await context.StandardOutput.WriteLineAsync(text["InfoVersions", string.Join(", ", info.Versions)]);
        await context.StandardOutput.WriteLineAsync(text["InfoInstalled", info.InstalledVersion ?? "-"]);
        return 0;
    }

    private static async Task<int> SourcesAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        HackPackageSources sources = await HackAppPackages.GetSourcesAsync(cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["SourcesHost", sources.GitHubHost]);
        await context.StandardOutput.WriteLineAsync(text[
            "SourcesTrusted", sources.TrustedOwners.Count == 0 ? "-" : string.Join(", ", sources.TrustedOwners)]);
        foreach (string warning in sources.Warnings)
        {
            await context.StandardError.WriteLineAsync($"hackpkg: {warning}");
        }

        return 0;
    }
}
