namespace Kazo.Os.Apps.HackPaint;

/// <summary>
/// Names Hack Paint's texts (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French, read with <c>AppContext.GetLocalizer&lt;Strings&gt;()</c>
/// in components and <c>HackLocalization.GetLocalizer&lt;Strings&gt;()</c> (the running process's language) elsewhere.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }
}
