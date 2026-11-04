namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// Names the texts of the terminal controls (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French, read in the host app's language with
/// <c>AppContext.GetLocalizer&lt;Strings&gt;()</c>. The shell inside prints its own diagnostics in
/// its own language (Platform Core).
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }
}
