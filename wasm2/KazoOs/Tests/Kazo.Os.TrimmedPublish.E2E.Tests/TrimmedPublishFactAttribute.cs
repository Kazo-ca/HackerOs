namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped unless <c>build/publish-trimmed.sh</c> (or a
/// person) points the suite at a served trimmed publish through
/// <see cref="TrimmedPublishSettings.UrlVariable"/>.
/// </summary>
/// <remarks>
/// The suite never publishes on its own: the script owns the publish and the server, so one run
/// checks exactly the binaries it produced, and a solution-wide <c>dotnet test</c> reports these
/// tests as skipped instead of failing them.
/// </remarks>
public sealed class TrimmedPublishFactAttribute : FactAttribute
{
    /// <summary>Marks the test skipped when no URL is configured.</summary>
    public TrimmedPublishFactAttribute()
    {
        if (TrimmedPublishSettings.FromEnvironment() is null)
        {
            Skip = $"{TrimmedPublishSettings.UrlVariable} is not set; run build/publish-trimmed.sh.";
        }
    }
}
