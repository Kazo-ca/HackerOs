using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Seeds a clean/new profile with the capability grants a manifest declares, following the
/// "who receives what" table of ADR 0049 section 2.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A declared normal capability is granted, sourced as <see cref="CapabilityGrantSource.BuildProfile"/>.</item>
/// <item>A declared dangerous capability (<see cref="AppCapabilities.IsDangerous"/>) of a system app
/// (ADR 0048) is granted, sourced as <see cref="CapabilityGrantSource.SystemPolicy"/>.</item>
/// <item>A declared dangerous capability of a non-system app is withheld: it needs an administrator
/// approval recorded in <c>/etc/kazoos/app-permissions.config</c>, which the launch gate of
/// ADR 0049 section 5 grants as <see cref="CapabilityGrantSource.AdministratorApproval"/>.</item>
/// </list>
/// Seeding never infers additional access from app kind, acting authority, or related
/// capabilities: an app that does not declare a capability remains denied by default, even for a
/// System-authority acting principal, until an explicit grant exists.
/// </remarks>
public static class CleanProfileCapabilityGrantSeeder
{
    /// <summary>
    /// Grants one user the capabilities one validated manifest declares, except the dangerous ones
    /// of a non-system app, and no more.
    /// </summary>
    /// <param name="repository">Target capability grant repository.</param>
    /// <param name="manifest">Validated app manifest whose declared capabilities become default grants.</param>
    /// <param name="userId">Exact user ID receiving the default grants.</param>
    /// <param name="isSystemApp">
    /// Whether the app is a system app of the host (<see cref="AppCatalog.IsSystemApp"/>), which
    /// receives its dangerous capabilities without approval.
    /// </param>
    /// <returns>The mutation result recorded for each granted capability, in manifest order.</returns>
    public static IReadOnlyList<CapabilityGrantMutationResult> SeedDeclaredCapabilities(
        ICapabilityGrantRepository repository,
        AppManifest manifest,
        string userId,
        bool isSystemApp)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        List<CapabilityGrantMutationResult> results = [];
        foreach (string capability in manifest.Capabilities)
        {
            bool dangerous = AppCapabilities.IsDangerous(capability);
            if (dangerous && !isSystemApp)
            {
                // Pending administrator approval (ADR 0049 section 4); never granted silently.
                continue;
            }

            CapabilityPolicyEvaluation evaluation = repository.Evaluate(
                manifest.Id,
                userId,
                capability,
                AppAuthority.System,
                AppAuthority.User);

            if (!evaluation.Granted)
            {
                results.Add(repository.Grant(
                    manifest.Id,
                    userId,
                    capability,
                    dangerous ? CapabilityGrantSource.SystemPolicy : CapabilityGrantSource.BuildProfile,
                    AppAuthority.System));
            }
        }

        return results;
    }
}
