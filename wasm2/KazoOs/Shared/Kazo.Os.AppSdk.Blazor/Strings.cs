namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Names the texts of the Blazor SDK (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French. They render inside an app, so they are read in that
/// app's language: <c>KazoLocalization.GetLocalizer&lt;Strings&gt;(AppContext)</c> with the window's
/// (or cascaded) context, or the ambient app's language without one.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }
}
