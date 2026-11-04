namespace Kazo.Os.Ecosystem;

/// <summary>
/// Names the texts of the host's own screens (ADR 0104): boot, first run, sign-in, session startup,
/// recovery and fatal screens, in <c>Resources/Strings.resx</c> (English) and
/// <c>Resources/Strings.fr.resx</c> (French). Read with <c>@inject IStringLocalizer&lt;Strings&gt; L</c>,
/// bound to <c>SessionLocaleService</c>: the system language before sign-in.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }
}
