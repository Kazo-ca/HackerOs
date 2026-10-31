using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Apps.ThemePacks;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Theming.Abstractions;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// Theme packs of the Hack Store behind <see cref="HackAppPackages"/> (ADR 0190, ADR 0213): the OS
/// downloads the <c>.hacktheme</c> of a catalog itself, verifies it, and unpacks it for the session
/// user (written as that user, without an administrator, from a trusted catalog) or for every user
/// (two-step approval of ADR 0189 §4, then written as the system). A pack holds no code, so it
/// applies at once: no restart.
/// </summary>
internal sealed partial class AppManagementGateway
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackInstalledThemePack>> GetInstalledThemePacksAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        (IReadOnlyList<InstalledThemePack> system, IReadOnlyList<InstalledThemePack> personal) =
            await ReadThemePacksAsync(packages, cancellationToken).ConfigureAwait(false);
        return [.. system.Concat(personal.Where(pack => system.All(other => other.Id != pack.Id)))
            .OrderBy(static pack => pack.Id, StringComparer.Ordinal)
            .Select(ToPublic)];
    }

    /// <inheritdoc />
    public async ValueTask<HackThemePackInstallReport> InstallThemePackAsync(
        string source, HackPackageScope scope = HackPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway system = CreateSystemWriter();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(system, cancellationToken).ConfigureAwait(false);
        AppPackageSource parsed = await ResolveThemePackSourceAsync(packages, source, sources, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<InstalledThemePack> systemInstalled, IReadOnlyList<InstalledThemePack> personalInstalled) =
            await ReadThemePacksAsync(packages, cancellationToken).ConfigureAwait(false);
        AppPolicy policy = await packages.ReadPolicyAsync(system, cancellationToken).ConfigureAwait(false);
        // Download and every check first: no administrator is asked for a pack that would be refused.
        ThemePackPlan plan = await Translate(() => packages.PrepareThemePackAsync(
                parsed,
                sources,
                policy,
                (AppPackageScope)scope,
                _principal.HomePath,
                systemInstalled,
                personalInstalled,
                cancellationToken))
            .ConfigureAwait(false);
        ThemePack pack = plan.Pack;
        string by = _principal.LoginName.Value;
        if (plan.RequiresAdministrator)
        {
            string operation = Strings.For(_language())[
                plan.SystemWide
                    ? plan.Trust == AppSourceTrust.Untrusted ? "Admin.App.InstallUntrustedPackage" : "Admin.App.InstallPackage"
                    : "Admin.App.InstallUntrustedPackageForUser",
                pack.Name,
                pack.Version.ToString(),
                plan.Source.Name,
                _principal.LoginName.Value];
            by = await ApproveTwoStepAsync(
                operation,
                "themes.pack.install",
                new AppInstallApprovalRequest(
                    pack.Id,
                    pack.Name,
                    pack.Version.ToString(),
                    plan.PreviousVersion?.ToString(),
                    pack.SizeBytes,
                    plan.Source.ToString(),
                    plan.Trust,
                    [],
                    plan.PreviousVersion is null ? AppInstallApprovalOperation.Install : AppInstallApprovalOperation.Update,
                    plan.SystemWide,
                    string.Empty,
                    _principal,
                    _caller)
                {
                    BlockReason = plan.BlockReason,
                },
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> personalCopies = plan.SystemWide
            ? await packages.FindPersonalThemePackCopiesAsync(system, pack.Id, cancellationToken).ConfigureAwait(false)
            : [];
        await packages
            .CommitThemePackAsync(plan.SystemWide ? system : CreateUserWriter(), plan, by, cancellationToken, personalCopies)
            .ConfigureAwait(false);
        return new HackThemePackInstallReport(
            pack.Id,
            pack.Name,
            pack.Version.ToString(),
            plan.Source.ToString(),
            plan.PreviousVersion?.ToString(),
            plan.SystemWide,
            (HackThemePackParts)pack.Parts,
            ToPublic(plan.Trust));
    }

    /// <inheritdoc />
    public async ValueTask<HackInstalledThemePack> RemoveThemePackAsync(
        string packId, HackPackageScope scope = HackPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        AppPackageInstaller packages = RequirePackages();
        (IReadOnlyList<InstalledThemePack> systemInstalled, IReadOnlyList<InstalledThemePack> personalInstalled) =
            await ReadThemePacksAsync(packages, cancellationToken).ConfigureAwait(false);
        bool forUser = scope == HackPackageScope.User || (scope == HackPackageScope.Auto && personalInstalled.Any(pack => pack.Id == packId));
        if (forUser)
        {
            InstalledThemePack removedForUser = await Translate(
                    () => packages.RemoveThemePackAsync(CreateUserWriter(), packId, _principal.LoginName.Value, cancellationToken, _principal.HomePath))
                .ConfigureAwait(false);
            return ToPublic(removedForUser);
        }

        InstalledThemePack installed = systemInstalled.FirstOrDefault(pack => pack.Id == packId)
            ?? throw new HackPackageException("themes.pack.not-installed", $"The theme pack {packId} is not installed.");
        IAppFileSystemGateway system = CreateSystemWriter();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(system, cancellationToken).ConfigureAwait(false);
        AppSourceTrust trust = AppPackageSource.TryParse(installed.Source, out AppPackageSource? source)
            ? sources.GetTrust(source)
            : AppSourceTrust.Untrusted;
        string by = await ApproveTwoStepAsync(
            Strings.For(_language())["Admin.App.RemovePackage", installed.Name, installed.Source],
            "themes.pack.remove",
            new AppInstallApprovalRequest(
                packId,
                installed.Name,
                installed.Version.ToString(),
                null,
                null,
                installed.Source,
                trust,
                [],
                AppInstallApprovalOperation.Remove,
                true,
                string.Empty,
                _principal,
                _caller),
            cancellationToken).ConfigureAwait(false);
        InstalledThemePack removed = await Translate(() => packages.RemoveThemePackAsync(system, packId, by, cancellationToken)).ConfigureAwait(false);
        return ToPublic(removed);
    }

    private async ValueTask<(IReadOnlyList<InstalledThemePack> System, IReadOnlyList<InstalledThemePack> Personal)> ReadThemePacksAsync(
        AppPackageInstaller packages, CancellationToken cancellationToken) =>
        (await packages.ReadInstalledThemePacksAsync(CreateSystemWriter(), ThemePackLayout.SystemDirectory, cancellationToken).ConfigureAwait(false),
            await packages.ReadInstalledThemePacksAsync(CreateUserWriter(), ThemePackLayout.GetUserDirectory(_principal.HomePath), cancellationToken).ConfigureAwait(false));

    /// <summary>Reads what the user typed: <c>catalog:&lt;name&gt;/&lt;packId&gt;[@version]</c>, or a pack id alone looked up in the catalogs.</summary>
    private static async ValueTask<AppPackageSource> ResolveThemePackSourceAsync(
        AppPackageInstaller packages, string source, AppSourcesConfig sources, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (AppPackageSource.TryParse(source, out AppPackageSource? parsed))
        {
            return parsed;
        }

        string text = source.Trim();
        SemanticVersion? version = null;
        int at = text.LastIndexOf('@');
        if (at > 0 && SemanticVersion.TryParse(text[(at + 1)..], out SemanticVersion requested))
        {
            version = requested;
            text = text[..at];
        }

        if (!ThemePack.IsValidId(text))
        {
            throw new HackPackageException("themes.pack.bad-source", $"'{source}' is not a theme pack: use a pack id or catalog:name/pack.id, optionally followed by @version.");
        }

        return await Translate(() => packages.FindThemePackInCatalogsAsync(text, version, sources, cancellationToken)).ConfigureAwait(false);
    }

    private static HackInstalledThemePack ToPublic(InstalledThemePack pack) =>
        new(pack.Id, pack.Name, pack.Version.ToString(), pack.Source, pack.SystemWide, (HackThemePackParts)pack.Parts);
}
