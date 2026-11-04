namespace Kazo.Os.Apps.ErrorLogViewer;

/// <summary>
/// Names the texts of the Error Log Viewer (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French, read with <c>AppContext.GetLocalizer&lt;Strings&gt;()</c>.
/// Only the viewer's chrome is translated: the lines of <c>/var/log</c> stay as they were written,
/// in English, for the tools that read them (ADR 0104, section 5).
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }
}
