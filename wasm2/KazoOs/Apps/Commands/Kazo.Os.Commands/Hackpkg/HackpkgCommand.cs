using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Hackpkg;

/// <summary>
/// <c>hackpkg install | remove | update | rollback | repair | list | info | sources | policy</c> (ADR 0182 §4,
/// ADR 0189 §6, ADR 0214): installs apps from their GitHub repository through <see cref="KazoAppPackages"/>.
/// </summary>
/// <remarks>
/// <para>
/// An app from a trusted source is installed for the session user by default
/// (<c>~/.local/share/hackeros/apps</c>), without an administrator. <c>--system</c> installs it for
/// every user, and an untrusted source or a blocked app always asks an administrator: the OS shows
/// its two-step window (a fresh sign-in, then the app, its trust level and its capabilities), every
/// time, even under <c>sudo</c> (ADR 0189 §4). <c>--allow-untrusted</c> is gone: the window replaces it.
/// </para>
/// <para>
/// <c>sources add|remove</c> and <c>policy block|unblock</c> edit <c>/etc/hackeros/app-sources.config</c>
/// and <c>/etc/hackeros/app-policy.config</c>, which asks an administrator (or nothing under <c>sudo</c>).
/// <c>list</c>, <c>info</c>, <c>sources</c> and <c>policy</c> alone only read.
/// </para>
/// <para>
/// An installed app is available at once (ADR 0214 §1); only a change to an app already loaded in
/// the page waits for a restart of Kazo OS, which the command says. <c>rollback</c> goes back to the
/// version the last update kept, <c>repair</c> finishes or undoes an interrupted install (§3, §4).
/// </para>
/// </remarks>
public sealed class HackpkgCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    private const string UserOption = "--user";
    private const string SystemOption = "--system";
    private const string TrustedOption = "--trusted";

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
        if (verb is not ("install" or "remove" or "update" or "rollback" or "repair" or "list" or "info" or "sources" or "policy"))
        {
            await context.StandardError.WriteLineAsync(text["UnknownCommand", verb]);
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        List<string> options = [.. args.Skip(1).Where(static arg => arg.StartsWith('-'))];
        List<string> operands = [.. args.Skip(1).Where(static arg => !arg.StartsWith('-'))];
        string? invalid = options.FirstOrDefault(option => !IsAllowed(verb, operands, option));
        if (invalid is not null || (options.Contains(UserOption) && options.Contains(SystemOption)))
        {
            await context.StandardError.WriteLineAsync(text["InvalidOption", invalid ?? SystemOption]);
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        if (!HasExpectedOperands(verb, operands))
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        KazoPackageScope scope = options.Contains(UserOption) ? KazoPackageScope.User
            : options.Contains(SystemOption) ? KazoPackageScope.System
            : KazoPackageScope.Auto;
        try
        {
            return verb switch
            {
                "install" => await InstallAsync(context, operands[0], scope, cancellationToken),
                "remove" => await RemoveAsync(context, operands[0], scope, cancellationToken),
                "update" => await UpdateAsync(context, operands.Count == 0 ? null : operands[0], cancellationToken),
                "rollback" => await RollbackAsync(context, operands[0], scope, cancellationToken),
                "repair" => await RepairAsync(context, options.Contains(SystemOption), cancellationToken),
                "list" => await ListAsync(context, cancellationToken),
                "info" => await InfoAsync(context, operands[0], cancellationToken),
                "sources" => await SourcesAsync(context, operands, options.Contains(TrustedOption), cancellationToken),
                _ => await PolicyAsync(context, operands, cancellationToken)
            };
        }
        catch (KazoPackageException exception)
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

    /// <summary>Whether <paramref name="option"/> belongs to <paramref name="verb"/>.</summary>
    private static bool IsAllowed(string verb, IReadOnlyList<string> operands, string option) => option switch
    {
        SystemOption => verb is "install" or "remove" or "rollback" or "repair",
        UserOption => verb is "install" or "remove" or "rollback",
        TrustedOption => verb == "sources" && operands.Count > 0 && operands[0] == "add",
        _ => false
    };

    /// <summary>Whether the operands fit the verb: the usage text is printed otherwise.</summary>
    private static bool HasExpectedOperands(string verb, IReadOnlyList<string> operands) => verb switch
    {
        "install" or "remove" or "rollback" or "info" => operands.Count == 1,
        "list" or "repair" => operands.Count == 0,
        "update" => operands.Count <= 1,
        "sources" => operands.Count == 0 || (operands.Count == 2 && operands[0] is "add" or "remove"),
        _ => operands.Count == 0
            || (operands.Count == 3 && operands[0] is "block" or "unblock" && TryParseKind(operands[1], out _))
    };

    private static bool TryParseKind(string value, out KazoPackageBlockKind kind)
    {
        (bool known, kind) = value switch
        {
            "app" => (true, KazoPackageBlockKind.App),
            "category" => (true, KazoPackageBlockKind.Category),
            "capability" => (true, KazoPackageBlockKind.Capability),
            _ => (false, KazoPackageBlockKind.App)
        };
        return known;
    }

    private static async Task<int> InstallAsync(
        TerminalExecutionContext context, string source, KazoPackageScope scope, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        await context.StandardOutput.WriteLineAsync(text["Downloading", source]);
        KazoPackageInstallReport report = await KazoAppPackages.InstallAsync(source, scope, cancellationToken);
        await WriteReportAsync(context, report);
        return 0;
    }

    private static async Task WriteReportAsync(TerminalExecutionContext context, KazoPackageInstallReport report)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        if (report.Trust == KazoPackageTrust.Untrusted)
        {
            await context.StandardError.WriteLineAsync(text["Untrusted", report.Source]);
        }

        if (report.BlockedBy is not null)
        {
            await context.StandardError.WriteLineAsync(text["BlockedBy", report.BlockedBy]);
        }

        await context.StandardOutput.WriteLineAsync(report.PreviousVersion is null
            ? text["Installed", report.Name, report.Version, report.Source]
            : text["Updated", report.Name, report.PreviousVersion, report.Version]);
        await context.StandardOutput.WriteLineAsync(report.SystemWide ? text["ForEveryone"] : text["ForYou"]);
        if (report.ReplacedPersonalCopies > 0)
        {
            await context.StandardOutput.WriteLineAsync(text["ReplacedPersonalCopies", report.ReplacedPersonalCopies]);
        }
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

        if (report.Signed)
        {
            await context.StandardOutput.WriteLineAsync(text["Signed"]);
        }

        await context.StandardOutput.WriteLineAsync(report.RestartRequired ? text["RestartRequired"] : text["AvailableNow"]);
    }

    private static async Task<int> RemoveAsync(
        TerminalExecutionContext context, string appId, KazoPackageScope scope, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        KazoInstalledPackage removed = await KazoAppPackages.RemoveAsync(appId, scope, cancellationToken);
        // Loaded: the removed app stays in the page until the next restart (ADR 0214 §1).
        await context.StandardOutput.WriteLineAsync(text[removed.Loaded ? "RemovedRestart" : "Removed", removed.AppId, removed.Version]);
        return 0;
    }

    private static async Task<int> RollbackAsync(
        TerminalExecutionContext context, string appId, KazoPackageScope scope, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        KazoPackageInstallReport report = await KazoAppPackages.RollbackAsync(appId, scope, cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["RolledBack", report.Name, report.PreviousVersion ?? "-", report.Version]);
        await context.StandardOutput.WriteLineAsync(report.RestartRequired ? text["RestartRequired"] : text["AvailableNow"]);
        return 0;
    }

    private static async Task<int> RepairAsync(TerminalExecutionContext context, bool system, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<string> repaired = await KazoAppPackages.RepairAsync(system, cancellationToken);
        if (repaired.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync(text["NothingToRepair"]);
            return 0;
        }

        foreach (string line in repaired)
        {
            await context.StandardOutput.WriteLineAsync($"hackpkg: {line}");
        }

        return 0;
    }

    private static async Task<int> UpdateAsync(TerminalExecutionContext context, string? appId, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<KazoInstalledPackage> installed = await KazoAppPackages.GetInstalledAsync(cancellationToken);
        IReadOnlyList<KazoInstalledPackage> targets = appId is null
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

        foreach (KazoInstalledPackage package in targets)
        {
            KazoPackageInfo info = await KazoAppPackages.GetInfoAsync(package.Source, cancellationToken);
            if (info.Latest is null || !IsNewer(info.Latest, package.Version))
            {
                await context.StandardOutput.WriteLineAsync(text["UpToDate", package.AppId, package.Version]);
                continue;
            }

            await context.StandardOutput.WriteLineAsync(text["Downloading", $"{package.Source}@{info.Latest}"]);
            // An update keeps the copy where it is; a source no longer trusted asks an administrator again.
            KazoPackageInstallReport report = await KazoAppPackages.InstallAsync(
                $"{package.Source}@{info.Latest}",
                package.SystemWide ? KazoPackageScope.System : KazoPackageScope.User,
                cancellationToken);
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
        IReadOnlyList<KazoInstalledPackage> installed = await KazoAppPackages.GetInstalledAsync(cancellationToken);
        if (installed.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync(text["NothingInstalled"]);
            return 0;
        }

        int idWidth = installed.Max(static package => package.AppId.Length);
        int versionWidth = installed.Max(static package => package.Version.Length);
        foreach (KazoInstalledPackage package in installed)
        {
            string pending = package.Loaded ? string.Empty : $" {text["PendingRestart"]}";
            pending += package.PreviousVersion is null ? string.Empty : $" {text["PreviousKept", package.PreviousVersion]}";
            string scope = package.SystemWide ? text["ScopeSystem"] : text["ScopeUser"];
            await context.StandardOutput.WriteLineAsync(
                $"{package.AppId.PadRight(idWidth)}  {package.Version.PadRight(versionWidth)}  {scope}  {package.Source}{pending}");
        }

        return 0;
    }

    private static async Task<int> InfoAsync(TerminalExecutionContext context, string source, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        KazoPackageInfo info = await KazoAppPackages.GetInfoAsync(source, cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["InfoSource", info.Source]);
        await context.StandardOutput.WriteLineAsync(text["InfoApp", info.AppId]);
        await context.StandardOutput.WriteLineAsync(text["InfoTrusted", TrustText(text, info.Trust)]);
        await context.StandardOutput.WriteLineAsync(text["InfoSignature", info.SignatureRequired ? text["SignatureRequired"] : text["SignatureNone"]]);
        await context.StandardOutput.WriteLineAsync(text["InfoLatest", info.Latest ?? "-"]);
        await context.StandardOutput.WriteLineAsync(text["InfoVersions", string.Join(", ", info.Versions)]);
        await context.StandardOutput.WriteLineAsync(text[
            "InfoInstalled",
            info.InstalledVersion is null ? "-"
                : $"{info.InstalledVersion} ({(info.InstalledSystemWide == false ? text["ScopeUser"] : text["ScopeSystem"])})"]);
        return 0;
    }

    private static string TrustText(IStringLocalizer<Strings> text, KazoPackageTrust trust) => trust switch
    {
        KazoPackageTrust.Verified => text["TrustVerified"],
        KazoPackageTrust.Trusted => text["TrustTrusted"],
        _ => text["TrustUntrusted"]
    };

    private static async Task<int> SourcesAsync(
        TerminalExecutionContext context, IReadOnlyList<string> operands, bool trusted, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        KazoPackageSources sources = operands.Count == 0
            ? await KazoAppPackages.GetSourcesAsync(cancellationToken)
            : operands[0] == "add"
                ? await KazoAppPackages.AddSourceAsync(operands[1], trusted, cancellationToken)
                : await KazoAppPackages.RemoveSourceAsync(operands[1], cancellationToken);
        if (operands.Count > 0)
        {
            await context.StandardOutput.WriteLineAsync(operands[0] == "add"
                ? text[trusted ? "SourceAddedTrusted" : "SourceAdded", operands[1]]
                : text["SourceRemoved", operands[1]]);
            return 0;
        }

        await context.StandardOutput.WriteLineAsync(text["SourcesHost", sources.GitHubHost]);
        foreach (KazoPackageCatalog catalog in sources.Catalogs)
        {
            string builtIn = catalog.BuiltIn ? $" {text["BuiltIn"]}" : string.Empty;
            await context.StandardOutput.WriteLineAsync(text[
                "SourcesCatalog", catalog.Name, catalog.Address, TrustText(text, catalog.Trust) + builtIn]);
        }

        await context.StandardOutput.WriteLineAsync(text[
            "SourcesTrusted",
            sources.TrustedSources.Count == 0 ? "-" : string.Join(", ", sources.TrustedSources.Select(source =>
                sources.BuiltInSources.Contains(source, StringComparer.OrdinalIgnoreCase) ? $"{source} {text["BuiltIn"]}" : source))]);
        await context.StandardOutput.WriteLineAsync(text[
            "SourcesUntrusted", sources.UntrustedSources.Count == 0 ? "-" : string.Join(", ", sources.UntrustedSources)]);
        await context.StandardOutput.WriteLineAsync(text["SourcesInterval", sources.UpdateIntervalHours]);
        foreach (string warning in sources.Warnings)
        {
            await context.StandardError.WriteLineAsync($"hackpkg: {warning}");
        }

        return 0;
    }

    private static async Task<int> PolicyAsync(
        TerminalExecutionContext context, IReadOnlyList<string> operands, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        if (operands.Count > 0)
        {
            _ = TryParseKind(operands[1], out KazoPackageBlockKind kind);
            bool blocked = operands[0] == "block";
            await KazoAppPackages.SetPolicyAsync(kind, operands[2], blocked, cancellationToken);
            await context.StandardOutput.WriteLineAsync(text[blocked ? "PolicyBlocked" : "PolicyUnblocked", operands[1], operands[2]]);
            return 0;
        }

        KazoPackagePolicy policy = await KazoAppPackages.GetPolicyAsync(cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["PolicyApps", Join(policy.BlockedApps)]);
        await context.StandardOutput.WriteLineAsync(text["PolicyCategories", Join(policy.BlockedCategories)]);
        await context.StandardOutput.WriteLineAsync(text["PolicyCapabilities", Join(policy.BlockedCapabilities)]);
        foreach (string warning in policy.Warnings)
        {
            await context.StandardError.WriteLineAsync($"hackpkg: {warning}");
        }

        return 0;
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "-" : string.Join(", ", values);
}
