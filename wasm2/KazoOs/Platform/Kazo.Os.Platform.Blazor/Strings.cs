using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Blazor;

/// <summary>
/// Names the texts of the platform's Razor UI (ADR 0104): <c>Resources/Strings.resx</c> in English
/// and <c>Resources/Strings.fr.resx</c> in French. Components read them with
/// <c>@inject IStringLocalizer&lt;Strings&gt; L</c>, which the host binds to the session language.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }

    /// <summary>
    /// Returns the texts in the language <paramref name="culture"/> supplies at each lookup, for the
    /// platform services that are not components (dialog window titles, the window menu, notifications).
    /// </summary>
    /// <param name="culture">Supplies the session language, or English when the host has none.</param>
    public static IStringLocalizer For(Func<CultureInfo> culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return new HackStringLocalizer(HackLocalization.GetResourceManager(typeof(Strings)), culture);
    }
}
