namespace Kazo.Os.HostSmoke.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped unless <c>build/verify-hosts.sh</c> (or a
/// person) points the suite at a running host through <see cref="HostSmokeSettings.UrlVariable"/>.
/// </summary>
/// <remarks>
/// The suite never starts a host on its own: the script owns the build, the publish and the
/// process lifetimes, so one run checks exactly the binaries it built. A solution-wide
/// <c>dotnet test</c> therefore reports these tests as skipped instead of failing them.
/// </remarks>
public sealed class HostSmokeFactAttribute : FactAttribute
{
    /// <summary>Marks the test skipped when no host URL is configured.</summary>
    public HostSmokeFactAttribute()
    {
        if (HostSmokeSettings.FromEnvironment() is null)
        {
            Skip = $"{HostSmokeSettings.UrlVariable} is not set; run build/verify-hosts.sh.";
        }
    }
}
