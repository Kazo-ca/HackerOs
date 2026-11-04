using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Core;

/// <summary>
/// Names the texts of the platform core (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French. Core code has no component to inject a localizer
/// into: it asks for the texts of one culture, the one of the process or session it speaks to.
/// </summary>
public sealed class Strings
{
    private Strings()
    {
    }

    /// <summary>Returns the core's texts in <paramref name="culture"/> (<c>en</c> or <c>fr</c>).</summary>
    /// <param name="culture">The language of the process or session the text is for.</param>
    public static IStringLocalizer For(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return new HackStringLocalizer(HackLocalization.GetResourceManager(typeof(Strings)), () => culture);
    }
}
