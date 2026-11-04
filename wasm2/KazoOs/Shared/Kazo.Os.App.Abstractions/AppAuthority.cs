namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Defines the ordered authority assigned by trusted ecosystem policy.
/// </summary>
/// <remarks>
/// Applications never grant this authority to themselves through their manifests.
/// </remarks>
public enum AppAuthority
{
    /// <summary>Authority available to a normal authenticated user.</summary>
    User = 0,

    /// <summary>Authority required to change protected OS configuration.</summary>
    Administrator = 1,

    /// <summary>Authority reserved for explicit, audited OS-owned operations.</summary>
    System = 2
}

/// <summary>
/// Evaluates the ordered Kazo OS authority hierarchy.
/// </summary>
public static class AppAuthorityPolicy
{
    /// <summary>
    /// Determines whether an acting authority satisfies a required authority.
    /// </summary>
    /// <param name="actual">Authority of the acting principal.</param>
    /// <param name="required">Minimum authority required by the operation.</param>
    /// <returns><see langword="true"/> when the acting authority is sufficient.</returns>
    public static bool Satisfies(AppAuthority actual, AppAuthority required) => actual >= required;
}