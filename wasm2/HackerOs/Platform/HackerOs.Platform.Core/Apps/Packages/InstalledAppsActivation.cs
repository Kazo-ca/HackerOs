namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// Brings the running catalog in line with the apps installed from packages, without restarting
/// the OS (ADR 0214 §1), as a Linux desktop rebuilds its application database after
/// <c>apt install</c>. The browser hosts implement it over the catalog they built at boot.
/// </summary>
public interface IInstalledAppsActivator
{
    /// <summary>
    /// Reads the installed apps again (both <c>[installed]</c> sections and every manifest) and
    /// replaces the running catalog's installed apps with them. An app whose code is already loaded
    /// in the page keeps the version it runs until the OS restarts (Q1 answer A).
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads; the catalog is then left unchanged.</param>
    /// <returns>What changed, and what waits for a restart.</returns>
    ValueTask<InstalledAppsActivation> ActivateAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IInstalledAppsActivator.ActivateAsync"/> changed in the running catalog.</summary>
/// <param name="Added">Apps that joined the catalog.</param>
/// <param name="Updated">Apps whose version changed in the catalog.</param>
/// <param name="Removed">Apps that left the catalog.</param>
/// <param name="WaitingForRestart">
/// Apps whose code is loaded in the page: their update, removal or rollback applies at the next
/// start of the OS (« Restart to finish », ADR 0212 §3).
/// </param>
public sealed record InstalledAppsActivation(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> WaitingForRestart)
{
    /// <summary>Nothing changed (also what a host without hot add returns).</summary>
    public static InstalledAppsActivation None { get; } = new([], [], [], []);

    /// <summary>Gets whether <paramref name="appId"/> waits for a restart.</summary>
    /// <param name="appId">The app.</param>
    public bool IsWaitingForRestart(string appId) => WaitingForRestart.Contains(appId, StringComparer.Ordinal);
}
