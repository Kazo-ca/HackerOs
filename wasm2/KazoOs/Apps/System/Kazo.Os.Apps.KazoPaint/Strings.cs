namespace Kazo.Os.Apps.KazoPaint;

/// <summary>
/// Names Kazo Paint's texts (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French, read with <c>AppContext.GetLocalizer&lt;Strings&gt;()</c>
/// in components and <c>KazoLocalization.GetLocalizer&lt;Strings&gt;()</c> (the running process's language) elsewhere.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }
}
