using Kazo.Os.AppSdk;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// Names the texts of the FileView control (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French. The control renders inside an app, so it reads them
/// in that app's language: <c>HackLocalization.GetLocalizer&lt;Strings&gt;(AppContext)</c> with the
/// cascaded app context, or the ambient app's language without one.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }
}
